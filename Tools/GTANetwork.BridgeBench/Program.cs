using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MessagePack;

namespace GTANetwork.BridgeBench;

/// <summary>
/// Frame protocol (the same the server will use towards the Bun runtime):
///   u32 little-endian length, then a MessagePack array [type:u8, id:u32|nil, name:str|nil, payload:any].
///   type 1 = call (runtime → engine; id present when a result is wanted), 2 = result (engine → runtime: payload = value),
///   3 = event (engine → runtime), 4 = state (engine → runtime: payload = array of player rows), 5 = log.
/// Calls the bench answers: "ping" (result = the id), "stats" (result = [calls, bytesIn, cpuMs]), "state.start" [players, hz],
/// "state.stop" (result = [frames, bytesOut, cpuMs]). Every other call without an id is counted only.
/// Usage: GTANetwork.BridgeBench --listen unix:/tmp/gtan-bridge.sock | --listen tcp:47000
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var listen = "unix:/tmp/gtan-bridge.sock";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--listen" && i + 1 < args.Length) listen = args[++i];
        }

        Socket server;
        if (listen.StartsWith("unix:", StringComparison.Ordinal))
        {
            var path = listen[5..];
            if (File.Exists(path)) File.Delete(path);
            server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(path));
        }
        else if (listen.StartsWith("tcp:", StringComparison.Ordinal))
        {
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, int.Parse(listen[4..])));
        }
        else
        {
            Console.Error.WriteLine("--listen unix:<path> or tcp:<port>");
            return 2;
        }
        server.Listen(1);
        Console.WriteLine("listening on " + listen);
        using var client = server.Accept();
        if (client.AddressFamily == AddressFamily.InterNetwork) client.NoDelay = true; // TCP only; not an option on a Unix socket
        Console.WriteLine("runtime connected");
        var session = new Session(client);
        session.Run();
        Console.WriteLine("runtime disconnected");
        return 0;
    }
}

internal sealed class Session
{
    private readonly Socket _socket;
    private readonly object _writeLock = new();
    private readonly ArrayBufferWriter<byte> _outgoing = new(1 << 16);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();

    private long _calls, _bytesIn, _bytesOut, _stateFrames, _stateBytes;
    private bool _replyPending;
    private int _stateMode;
    private volatile bool _publishing;
    private Thread? _publisher;
    private TimeSpan _cpuAtStateStart;

    public Session(Socket socket)
    {
        _socket = socket;
        new Thread(FlushLoop) { IsBackground = true, Name = "flush" }.Start();
    }

    /// <summary>Reads frames until the runtime hangs up.</summary>
    public void Run()
    {
        var buffer = new byte[1 << 20];
        var filled = 0;
        while (true)
        {
            int read;
            try { read = _socket.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None); }
            catch (SocketException) { break; }
            if (read <= 0) break;
            filled += read;
            _bytesIn += read;

            var offset = 0;
            while (filled - offset >= 4)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
                if (length <= 0 || length > buffer.Length - 4) throw new InvalidDataException("bad frame length " + length);
                if (filled - offset - 4 < length) break;
                HandleFrame(new ReadOnlyMemory<byte>(buffer, offset + 4, length));
                offset += 4 + length;
            }
            if (offset > 0)
            {
                Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                filled -= offset;
            }
            // Replies to what this chunk carried go out now, not at the next 1 ms tick: round trips stay at the socket's latency.
            lock (_writeLock)
            {
                if (_replyPending && _outgoing.WrittenCount > 0) FlushLocked();
                _replyPending = false;
            }
            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
        }
        _publishing = false;
    }

    private static readonly bool Debug = Environment.GetEnvironmentVariable("BRIDGE_DEBUG") == "1";
    private int _debugFrames;

    private void HandleFrame(ReadOnlyMemory<byte> frame)
    {
        var reader = new MessagePackReader(frame);
        var count = reader.ReadArrayHeader();
        var type = reader.ReadByte();
        uint? id = reader.TryReadNil() ? null : reader.ReadUInt32();
        string? name = reader.TryReadNil() ? null : reader.ReadString();
        if (Debug && _debugFrames++ < 8) Console.WriteLine($"frame {_debugFrames}: {frame.Length} bytes, array {count}, type {type}, id {id?.ToString() ?? "-"}, name {name ?? "-"}, hex {Convert.ToHexString(frame.Span[..Math.Min(24, frame.Length)])}");
        // payload is left in place; the bench does not need its value
        if (type != 1) return;
        _calls++;
        if (id == null) return;

        switch (name)
        {
            case "stats":
                Reply(id.Value, (ref MessagePackWriter w) => { w.WriteArrayHeader(3); w.Write(_calls); w.Write(_bytesIn); w.Write(_process.TotalProcessorTime.TotalMilliseconds); });
                break;
            case "state.start":
            {
                var n = reader.ReadArrayHeader();
                var players = n > 0 ? reader.ReadInt32() : 100;
                var hz = n > 1 ? reader.ReadInt32() : 10;
                _stateMode = n > 2 ? reader.ReadInt32() : 0; // 0 = msgpack arrays, 1 = one float32 buffer per frame
                StartPublishing(players, hz);
                Reply(id.Value, (ref MessagePackWriter w) => w.Write(true));
                break;
            }
            case "state.stop":
            {
                _publishing = false;
                _publisher?.Join(2000);
                var cpu = (_process.TotalProcessorTime - _cpuAtStateStart).TotalMilliseconds;
                Reply(id.Value, (ref MessagePackWriter w) => { w.WriteArrayHeader(3); w.Write(_stateFrames); w.Write(_stateBytes); w.Write(cpu); });
                break;
            }
            default:
                var echo = id.Value; Reply(echo, (ref MessagePackWriter w) => w.Write(echo)); // "ping" and anything else: the id back
                break;
        }
    }

    /// <summary>MessagePackWriter is a ref struct: a copy would keep the payload bytes in its own uncommitted buffer, so it is passed by ref.</summary>
    private delegate void Payload(ref MessagePackWriter writer);

    private void Reply(uint id, Payload payload)
    {
        if (Debug && _debugFrames <= 8) Console.WriteLine($"reply to {id}");
        lock (_writeLock)
        {
            var start = _outgoing.WrittenCount;
            _outgoing.GetSpan(4);
            _outgoing.Advance(4);
            var writer = new MessagePackWriter(_outgoing);
            writer.WriteArrayHeader(4);
            writer.Write((byte)2);
            writer.Write(id);
            writer.WriteNil();
            payload(ref writer);
            writer.Flush();
            _replyPending = true;
            var length = _outgoing.WrittenCount - start - 4;
            var span = System.Runtime.InteropServices.MemoryMarshal.AsMemory(_outgoing.WrittenMemory).Span.Slice(start, 4);
            BinaryPrimitives.WriteInt32LittleEndian(span, length);
            if (_outgoing.WrittenCount >= 1 << 16) FlushLocked();
        }
    }

    private void StartPublishing(int players, int hz)
    {
        _publishing = false;
        _publisher?.Join(2000);
        _stateFrames = 0;
        _stateBytes = 0;
        _cpuAtStateStart = _process.TotalProcessorTime;
        _publishing = true;
        _publisher = new Thread(() => PublishLoop(players, hz)) { IsBackground = true, Name = "state" };
        _publisher.Start();
    }

    /// <summary>One state frame per tick with every player's row: [id, x, y, z, rx, ry, rz, vx, vy, vz, health, armor, vehicle, seat, dim].</summary>
    private void PublishLoop(int players, int hz)
    {
        var period = TimeSpan.FromSeconds(1.0 / hz);
        var next = _clock.Elapsed;
        var rng = new Random(1);
        var x = new float[players];
        var y = new float[players];
        for (var i = 0; i < players; i++) { x[i] = rng.NextSingle() * 4000 - 2000; y[i] = rng.NextSingle() * 4000 - 2000; }
        while (_publishing)
        {
            next += period;
            lock (_writeLock)
            {
                var start = _outgoing.WrittenCount;
                _outgoing.GetSpan(4);
                _outgoing.Advance(4);
                var writer = new MessagePackWriter(_outgoing);
                writer.WriteArrayHeader(4);
                writer.Write((byte)4);
                writer.WriteNil();
                writer.WriteNil();
                if (_stateMode == 1)
                {
                    // one bin: players x 15 float32
                    var bin = new byte[players * 15 * 4];
                    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bin.AsSpan());
                    for (var i = 0; i < players; i++)
                    {
                        x[i] += 0.15f;
                        var o = i * 15;
                        floats[o] = i; floats[o + 1] = x[i]; floats[o + 2] = y[i]; floats[o + 3] = 30.5f;
                        floats[o + 4] = 0f; floats[o + 5] = 0f; floats[o + 6] = 90f;
                        floats[o + 7] = 1.5f; floats[o + 8] = 0f; floats[o + 9] = 0f;
                        floats[o + 10] = 200; floats[o + 11] = 50; floats[o + 12] = 0; floats[o + 13] = -1; floats[o + 14] = 0;
                    }
                    writer.Write(bin);
                }
                else
                {
                    writer.WriteArrayHeader(players);
                    for (var i = 0; i < players; i++)
                    {
                        x[i] += 0.15f;
                        writer.WriteArrayHeader(15);
                        writer.Write(i);
                        writer.Write(x[i]); writer.Write(y[i]); writer.Write(30.5f);
                        writer.Write(0f); writer.Write(0f); writer.Write(90f);
                        writer.Write(1.5f); writer.Write(0f); writer.Write(0f);
                        writer.Write(200); writer.Write(50); writer.Write(0); writer.Write(-1); writer.Write(0);
                    }
                }
                writer.Flush();
                var length = _outgoing.WrittenCount - start - 4;
                var span = System.Runtime.InteropServices.MemoryMarshal.AsMemory(_outgoing.WrittenMemory).Span.Slice(start, 4);
                BinaryPrimitives.WriteInt32LittleEndian(span, length);
                _stateFrames++;
                _stateBytes += length + 4;
                FlushLocked();
            }
            var wait = next - _clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
    }

    /// <summary>Batching: whatever accumulated goes out once per millisecond (or at 64 KB, see Reply).</summary>
    private void FlushLoop()
    {
        while (true)
        {
            Thread.Sleep(1);
            lock (_writeLock)
            {
                if (_outgoing.WrittenCount > 0)
                {
                    try { FlushLocked(); } catch (SocketException) { return; }
                }
            }
        }
    }

    private void FlushLocked()
    {
        var data = _outgoing.WrittenSpan;
        var sent = 0;
        while (sent < data.Length) sent += _socket.Send(data.Slice(sent));
        _bytesOut += data.Length;
        _outgoing.Clear();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GTANetworkShared;
using GTANetworkShared.Crypto;
using GTANetworkServer.Crypto;
using Lidgren.Network;

namespace GTANetwork.Bot;

/// <summary>
/// The load harness (T-002): <c>--bots N</c> holds N connections in this process. Every bot joins like a client (hail, session
/// handshake, ConnectionConfirmed, map and script download, ConnectionConfirmed(true)), then sends pure sync every 100 ms
/// and light sync every 1500 ms — the client's rates (Client/Sync/SyncSender) — while random-walking within <c>--move</c>
/// metres of its spawn. A few pump threads drain all the bots' messages; the receive counters go to <c>--report</c>.
/// </summary>
internal static class LoadBots
{
    public static int Run(Options o)
    {
        var clock = Stopwatch.StartNew();
        AesGcmNet.Install();
        var version = ParseableVersion.FromAssembly(typeof(ConnectionRequest).Assembly).ToString();
        var threads = o.Threads > 0 ? o.Threads : Math.Max(1, Math.Min(4, Environment.ProcessorCount));
        var bots = new LoadBot[o.Bots];
        var started = 0;
        var running = true;

        // one configuration for every peer: Lidgren locks it at the first Start and nothing changes it afterwards
        var config = new NetPeerConfiguration("GTANETWORK") { ConnectionTimeout = 30f };
        var rng = new Random(20260905);
        var workers = new List<Thread>();
        for (var t = 0; t < threads; t++)
        {
            var slice = t;
            var worker = new Thread(() =>
            {
                while (Volatile.Read(ref running))
                {
                    var now = clock.Elapsed;
                    var n = Volatile.Read(ref started);
                    for (var i = slice; i < n; i += threads) bots[i].Pump(now);
                    Thread.Sleep(2);
                }
            }) { IsBackground = true, Name = "bots-" + t };
            worker.Start();
            workers.Add(worker);
        }

        Program.Log("load", $"{o.Bots} bots -> {o.Host}:{o.Port}, one connect per {o.ConnectIntervalMs} ms, {threads} pump threads, move radius {o.Move.ToString("0", CultureInfo.InvariantCulture)} m, encryption {(o.NoEncryption ? "off" : "on")}{(o.Voice ? ", voice 50 frames/s each" : "")}");
        for (var i = 0; i < bots.Length; i++)
        {
            bots[i] = new LoadBot(i, o.Name + (i + 1).ToString(CultureInfo.InvariantCulture), config, !o.NoEncryption, o.Move, rng.Next(), o.Voice);
            bots[i].Connect(o.Host, o.Port, o.Password, version);
            Volatile.Write(ref started, i + 1);
            if (o.ConnectIntervalMs > 0) Thread.Sleep(o.ConnectIntervalMs);
        }

        // wait until every bot joined or failed
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(o.Timeout);
        var lastProgress = TimeSpan.Zero;
        int joined = 0, failed = 0;
        while (clock.Elapsed < deadline)
        {
            joined = bots.Count(b => b.Joined); failed = bots.Count(b => b.Failed);
            if (joined + failed >= bots.Length) break;
            if (clock.Elapsed - lastProgress > TimeSpan.FromSeconds(5))
            {
                lastProgress = clock.Elapsed;
                Program.Log("load", $"{joined} joined, {bots.Count(b => b.Connected && !b.Joined)} connected and downloading, {failed} failed");
            }
            Thread.Sleep(200);
        }
        var joinSeconds = clock.Elapsed.TotalSeconds;
        var reasons = bots.Where(b => b.Failed).GroupBy(b => b.FailReason ?? "?").ToDictionary(g => g.Key, g => g.Count());
        Program.Log("load", $"{joined}/{bots.Length} joined after {joinSeconds:0.0} s, {failed} failed" + (reasons.Count > 0 ? ": " + string.Join("; ", reasons.Select(r => $"{r.Value}x {r.Key}")) : ""));

        // hold: the bots move and sync; a line every 10 s
        var holdStart = clock.Elapsed;
        foreach (var b in bots) b.MarkHoldStart();
        var holdUntil = holdStart + TimeSpan.FromSeconds(o.Duration);
        var lastLine = holdStart; long lastPackets = bots.Sum(b => Interlocked.Read(ref b.PacketsIn)), lastBytes = bots.Sum(b => Interlocked.Read(ref b.BytesIn));
        while (clock.Elapsed < holdUntil)
        {
            Thread.Sleep(500);
            if (clock.Elapsed - lastLine < TimeSpan.FromSeconds(10)) continue;
            var dt = (clock.Elapsed - lastLine).TotalSeconds; lastLine = clock.Elapsed;
            long packets = bots.Sum(b => Interlocked.Read(ref b.PacketsIn)), bytes = bots.Sum(b => Interlocked.Read(ref b.BytesIn));
            var alive = bots.Count(b => b.Joined && !b.Closed);
            Program.Log("load", $"{alive} connected, in {(packets - lastPackets) / dt:0} pkt/s {(bytes - lastBytes) / dt / 1024:0.0} KB/s ({(packets - lastPackets) / dt / Math.Max(1, alive):0.0} pkt/s per bot), RSS {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");
            lastPackets = packets; lastBytes = bytes;
        }
        var holdSeconds = (clock.Elapsed - holdStart).TotalSeconds;

        // leave: stop the pumps, then every bot says goodbye
        Volatile.Write(ref running, false);
        foreach (var w in workers) w.Join(2000);
        var dropped = bots.Count(b => b.Joined && b.Closed);
        foreach (var b in bots) b.Leave();
        var until = clock.Elapsed + TimeSpan.FromSeconds(1);
        while (clock.Elapsed < until) { var now = clock.Elapsed; foreach (var b in bots) b.Pump(now); Thread.Sleep(20); }
        foreach (var b in bots) b.Shutdown();

        var process = Process.GetCurrentProcess();
        var joinedBots = bots.Where(b => b.Joined).ToList();
        var report = new
        {
            bots = bots.Length, joined, failed, disconnected = dropped, joinSeconds = Math.Round(joinSeconds, 1), holdSeconds = Math.Round(holdSeconds, 1),
            failReasons = reasons,
            inPerBot = new
            {
                pps = joinedBots.Count > 0 ? Math.Round(joinedBots.Average(b => b.HoldPackets / Math.Max(1, holdSeconds)), 2) : 0,
                bps = joinedBots.Count > 0 ? Math.Round(joinedBots.Average(b => b.HoldBytes / Math.Max(1, holdSeconds))) : 0,
            },
            inTotal = new { packets = bots.Sum(b => b.PacketsIn), bytes = bots.Sum(b => b.BytesIn) },
            handlesSeen = new
            {
                avg = joinedBots.Count > 0 ? Math.Round(joinedBots.Average(b => b.SeenCount), 1) : 0,
                max = joinedBots.Count > 0 ? joinedBots.Max(b => b.SeenCount) : 0,
            },
            rssBytes = process.WorkingSet64, threads = process.Threads.Count, cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 1),
        };
        if (!string.IsNullOrEmpty(o.Report))
        {
            File.WriteAllText(o.Report, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Program.Log("load", "report written to " + o.Report);
        }
        Program.Log("load", $"in per bot {report.inPerBot.pps} pkt/s, {report.inPerBot.bps / 1024.0:0.0} KB/s; handles seen avg {report.handlesSeen.avg}, max {report.handlesSeen.max}; RSS {process.WorkingSet64 / 1048576} MB, {process.Threads.Count} threads, CPU {report.cpuSeconds} s");

        Console.WriteLine();
        if (joined < bots.Length) { Program.Log("result", $"FAILED: {joined}/{bots.Length} bots joined"); return 1; }
        if (dropped > 0) { Program.Log("result", $"FAILED: {dropped} bot(s) lost the connection while holding"); return 1; }
        Program.Log("result", $"OK: {bots.Length} bots joined and stayed connected for {holdSeconds:0} s");
        return 0;
    }
}

/// <summary>One simulated player of the load harness: its own NetClient, session cipher, position and counters.</summary>
internal sealed class LoadBot
{
    private const int FreemodeMale = unchecked((int)0x705E61F2);   // mp_m_freemode_01
    private const double WalkSpeed = 1.5;                            // m/s

    public readonly string Name;
    private readonly NetClient _client;
    private readonly KeyPair _key;
    private NetSessionEncryption _session;
    private readonly float _moveRadius;
    private readonly bool _voice;
    private static readonly byte[] VoiceFrame = MakeVoiceFrame();
    private TimeSpan _nextVoice;
    private readonly Random _rng;
    private readonly Dictionary<int, FileType> _transfers = new();
    private readonly HashSet<string> _scriptResources = new();
    private readonly HashSet<int> _seen = new();

    public volatile bool Connected, Joined, Closed, Failed;
    public string FailReason;
    public long PacketsIn, BytesIn;
    private long _packetsAtHold, _bytesAtHold;
    private int _handle, _errors;
    private bool _leaving;
    private Vector3 _spawn = new(0f, 0f, 72f), _pos = new(0f, 0f, 72f);
    private float _heading;
    private bool _moving;
    private TimeSpan _nextTurn, _nextPure, _nextLight, _lastMove;

    public LoadBot(int index, string name, NetPeerConfiguration config, bool encrypt, float moveRadius, int seed, bool voice = false)
    {
        Name = name;
        _moveRadius = moveRadius;
        _voice = voice;
        _rng = new Random(seed);
        _client = new NetClient(config);
        if (encrypt) _key = KeyPair.Generate();
    }

    public long HoldPackets => Interlocked.Read(ref PacketsIn) - _packetsAtHold;
    public long HoldBytes => Interlocked.Read(ref BytesIn) - _bytesAtHold;
    public int SeenCount { get { lock (_seen) return _seen.Count; } }

    public void MarkHoldStart() { _packetsAtHold = Interlocked.Read(ref PacketsIn); _bytesAtHold = Interlocked.Read(ref BytesIn); }

    public void Connect(string host, int port, string password, string version)
    {
        _client.Start();
        var request = new ConnectionRequest
        {
            SocialClubName = Name, DisplayName = Name, ScriptVersion = version, GameVersion = 0, CEF = false, CEFDevtool = false, MediaStream = false,
            Password = string.IsNullOrEmpty(password) ? null : password, ClientPublicKey = _key?.PublicKey,
        };
        var hail = _client.CreateMessage();
        Program.WritePacket(hail, PacketType.ConnectionRequest, request);
        _client.Connect(host, port, hail);
    }

    /// <summary>Drains the messages and sends what is due. Called from one pump thread at a time.</summary>
    public void Pump(TimeSpan now)
    {
        NetIncomingMessage msg;
        while ((msg = _client.ReadMessage()) != null)
        {
            try
            {
                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged: OnStatus(msg, now); break;
                    case NetIncomingMessageType.Data: OnData(msg, now); break;
                }
            }
            catch (Exception ex)
            {
                if (_errors++ < 3) Program.Log("load", $"{Name}: {ex.GetType().Name} while handling {msg.MessageType}: {ex.Message}");
            }
            finally
            {
                _client.Recycle(msg);
            }
        }

        if (!Joined || Closed || _leaving) return;
        if (now >= _nextPure)
        {
            Move(now);
            SendSync(true);
            _nextPure = now + TimeSpan.FromMilliseconds(100);
        }
        if (now >= _nextLight)
        {
            SendSync(false);
            _nextLight = now + TimeSpan.FromMilliseconds(1500);
        }
        if (_voice && now >= _nextVoice)
        {
            // a 60-byte stand-in for a 24 kbit/s Opus frame; the server relays without decoding
            var voiceMsg = _client.CreateMessage();
            voiceMsg.Write((byte)PacketType.Voice);
            voiceMsg.Write(VoiceFrame.Length);
            voiceMsg.Write(VoiceFrame);
            Send(voiceMsg, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.Voice);
            _nextVoice = now + TimeSpan.FromMilliseconds(20);
        }
    }

    private static byte[] MakeVoiceFrame()
    {
        var frame = new byte[60];
        new Random(7).NextBytes(frame);
        return frame;
    }

    public void Leave()
    {
        _leaving = true;
        if (!Closed) _client.Disconnect("Quit");
    }

    public void Shutdown() => _client.Shutdown("bye");

    private void OnStatus(NetIncomingMessage msg, TimeSpan now)
    {
        var status = (NetConnectionStatus)msg.ReadByte();
        var reason = msg.ReadString();
        switch (status)
        {
            case NetConnectionStatus.Connected:
            {
                var response = Program.ReadPacket<ConnectionResponse>(msg.SenderConnection.RemoteHailMessage);
                var serverKey = response.ServerPublicKey;
                if (serverKey != null && serverKey.Length == 32 && _key != null)
                {
                    var key = SessionHandshake.DeriveSessionKey(_key.PrivateKey, serverKey, _key.PublicKey, serverKey);
                    _session = new NetSessionEncryption(_client, new SessionCipher(key, isServer: false), SessionHandshake.Fingerprint(serverKey));
                }
                _handle = response.CharacterHandle;
                Connected = true;
                var confirm = _client.CreateMessage();
                confirm.Write((byte)PacketType.ConnectionConfirmed);
                confirm.Write(false);
                Send(confirm, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                break;
            }
            case NetConnectionStatus.Disconnected:
                Closed = true;
                if (!Joined) { Failed = true; FailReason = string.IsNullOrEmpty(reason) ? "disconnected before joining" : reason; }
                else if (!_leaving && _errors++ < 3) Program.Log("load", $"{Name}: dropped: {reason}");
                break;
        }
    }

    private void OnData(NetIncomingMessage msg, TimeSpan now)
    {
        if (_session != null && !msg.Decrypt(_session)) return;
        Interlocked.Increment(ref PacketsIn);
        Interlocked.Add(ref BytesIn, msg.LengthBytes);
        var type = (PacketType)msg.ReadByte();
        switch (type)
        {
            case PacketType.FileTransferRequest:
            {
                var start = Program.ReadPacket<DataDownloadStart>(msg);
                _transfers[start.Id] = (FileType)start.FileType;
                if ((FileType)start.FileType == FileType.Script && !string.IsNullOrEmpty(start.ResourceParent)) _scriptResources.Add(start.ResourceParent);
                var accept = _client.CreateMessage();
                accept.Write((byte)PacketType.FileAcceptDeny);
                accept.Write(start.Id);
                accept.Write(true);
                Send(accept, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                break;
            }
            case PacketType.FileTransferComplete:
            {
                var id = msg.ReadInt32();
                if (_transfers.TryGetValue(id, out var fileType) && fileType == FileType.EndOfTransfer && !Joined)
                {
                    var confirm = _client.CreateMessage();
                    confirm.Write((byte)PacketType.ConnectionConfirmed);
                    confirm.Write(true);
                    confirm.Write(_scriptResources.Count);
                    foreach (var r in _scriptResources) confirm.Write(r);
                    Send(confirm, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                    Joined = true;
                    _nextPure = now; _nextLight = now;
                }
                _transfers.Remove(id);
                break;
            }
            case PacketType.PedPureSync:
            {
                var len = msg.ReadInt32();
                var ped = PacketOptimization.ReadPurePedSync(msg.ReadBytes(len));
                if (ped.NetHandle.HasValue) lock (_seen) _seen.Add(ped.NetHandle.Value);
                break;
            }
            case PacketType.BasicSync:
            {
                var len = msg.ReadInt32();
                PacketOptimization.ReadBasicSync(msg.ReadBytes(len), out var handle, out _);
                lock (_seen) _seen.Add(handle);
                break;
            }
            case PacketType.NativeCall:
                ApplyNative(Program.ReadPacket<NativeData>(msg));
                break;
            case PacketType.UpdateEntityProperties:
            {
                var u = Program.ReadPacket<UpdateEntity>(msg);
                if (u.NetHandle == _handle && u.Properties?.Position != null) { _pos = u.Properties.Position; _spawn = _pos; }
                break;
            }
            case PacketType.RpcRequest:
            {
                var r = Program.ReadPacket<RpcRequest>(msg);
                var reply = _client.CreateMessage();
                Program.WritePacket(reply, PacketType.RpcResponse, new RpcResponse { Id = r.Id, Ok = true, Payload = r.Payload ?? "null" });
                Send(reply, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Rpc);
                break;
            }
        }
    }

    /// <summary>The gamemode's spawn: SET_ENTITY_COORDS on this player moves the bot and its walking circle.</summary>
    private void ApplyNative(NativeData n)
    {
        var name = Program.NativeName(n.Hash);
        var args = n.Arguments ?? new List<NativeArgument>();
        if ((name == "SET_ENTITY_COORDS" || name == "SET_ENTITY_COORDS_NO_OFFSET") && args.Count >= 4 && TargetsMe(args[0]))
        {
            float? x = AsFloat(args[1]), y = AsFloat(args[2]), z = AsFloat(args[3]);
            if (x.HasValue && y.HasValue && z.HasValue) { _spawn = new Vector3(x.Value, y.Value, z.Value); _pos = _spawn; }
        }
        else if (name == "SET_ENTITY_HEADING" && args.Count >= 2 && TargetsMe(args[0]))
        {
            _heading = AsFloat(args[1]) ?? _heading;
        }
    }

    private bool TargetsMe(NativeArgument a) => a is LocalPlayerArgument || a is LocalGamePlayerArgument || (a is EntityArgument e && e.NetHandle == _handle);

    private static float? AsFloat(NativeArgument a) => a switch { FloatArgument f => f.Data, IntArgument i => i.Data, _ => null };

    /// <summary>Random walk at walking speed inside the circle around the spawn; a new heading every 3–8 s.</summary>
    private void Move(TimeSpan now)
    {
        if (_moveRadius <= 0) { _moving = false; return; }
        if (now >= _nextTurn)
        {
            _heading = (float)(_rng.NextDouble() * 360);
            _nextTurn = now + TimeSpan.FromSeconds(3 + _rng.NextDouble() * 5);
        }
        var dt = _lastMove == TimeSpan.Zero ? 0.1 : Math.Min(1, (now - _lastMove).TotalSeconds);
        _lastMove = now;
        var rad = _heading * Math.PI / 180;
        var nx = _pos.X - (float)(Math.Sin(rad) * WalkSpeed * dt);   // GTA heading: 0 = north (+Y), forward = (-sin h, cos h)
        var ny = _pos.Y + (float)(Math.Cos(rad) * WalkSpeed * dt);
        float dx = nx - _spawn.X, dy = ny - _spawn.Y;
        if (dx * dx + dy * dy > _moveRadius * _moveRadius)
        {
            // leaving the circle: turn back towards the spawn
            _heading = (float)((Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360);
            _moving = false;
            return;
        }
        _pos = new Vector3(nx, ny, _pos.Z);
        _moving = true;
    }

    private void SendSync(bool pure)
    {
        var rad = _heading * Math.PI / 180;
        var data = new PedData
        {
            NetHandle = _handle,
            Flag = 0,
            Position = _pos,
            Quaternion = new Vector3(0f, 0f, _heading),
            Velocity = _moving ? new Vector3(-(float)(Math.Sin(rad) * WalkSpeed), (float)(Math.Cos(rad) * WalkSpeed), 0f) : new Vector3(0f, 0f, 0f),
            PlayerHealth = 100,
            PedArmor = 0,
            Speed = (byte)(_moving ? 1 : 0),
            WeaponHash = unchecked((int)WeaponHash.Unarmed),
            WeaponAmmo = 0,
            AimCoords = new Vector3(0f, 0f, 0f),
            PedModelHash = FreemodeMale,
            Latency = 0.05f,
        };
        var bin = pure ? PacketOptimization.WritePureSync(data) : PacketOptimization.WriteLightSync(data);
        var msg = _client.CreateMessage();
        msg.Write((byte)(pure ? PacketType.PedPureSync : PacketType.PedLightSync));
        msg.Write(bin.Length);
        msg.Write(bin);
        if (pure) Send(msg, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.PureSync);
        else Send(msg, NetDeliveryMethod.ReliableSequenced, (int)ConnectionChannel.LightSync);
    }

    private void Send(NetOutgoingMessage msg, NetDeliveryMethod method, int channel)
    {
        if (_session != null) msg.Encrypt(_session);
        _client.SendMessage(msg, method, channel);
    }
}

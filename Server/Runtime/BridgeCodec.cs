using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using MessagePack;

namespace GTANetworkServer.Runtime
{
    /// <summary>
    /// The wire format between the engine and the Bun runtime (docs/tasks/T-006-server-runtime-on-bun-bridge.md): every frame is
    /// a little-endian u32 length followed by a MessagePack array <c>[type, id, name, payload]</c>.
    /// </summary>
    internal enum FrameType : byte
    {
        Call = 1,    // runtime -> engine: name = API member (or "exported.<resource>.<fn>"), payload = argument array; id when a result is wanted
        Result = 2,  // either direction: id of the request, payload = value or { "error": "..." }
        Event = 3,   // engine -> runtime: name = event, payload = argument array; id when the runtime must answer (cancelable events)
        State = 4,   // engine -> runtime: payload = state delta (see StateMirror)
        Log = 5,     // runtime -> engine: payload = text
        Load = 6,    // engine -> runtime: payload = { resource, entry, settings }; runtime answers with the id
        Unload = 7,  // engine -> runtime: payload = resource name
    }

    /// <summary>Accumulates outgoing frames and sends them in batches: every millisecond, at 64 KB, or on demand.</summary>
    internal sealed class FrameWriter
    {
        private readonly Socket _socket;
        private readonly object _lock = new object();
        private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>(1 << 16);
        private bool _wantFlush;

        public FrameWriter(Socket socket)
        {
            _socket = socket;
        }

        public delegate void Payload(ref MessagePackWriter writer);

        /// <summary>Appends one frame; flushImmediately marks the batch to go out at once (results, cancelable events).</summary>
        public void Write(FrameType type, uint? id, string name, Payload payload, bool flushImmediately)
        {
            lock (_lock)
            {
                var start = _buffer.WrittenCount;
                _buffer.GetSpan(4);
                _buffer.Advance(4);
                var writer = new MessagePackWriter(_buffer);
                writer.WriteArrayHeader(4);
                writer.Write((byte)type);
                if (id.HasValue) writer.Write(id.Value); else writer.WriteNil();
                if (name != null) writer.Write(name); else writer.WriteNil();
                if (payload != null) payload(ref writer); else writer.WriteNil();
                writer.Flush();
                var length = _buffer.WrittenCount - start - 4;
                BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span.Slice(start, 4), length);
                if (flushImmediately) _wantFlush = true;
                if (_wantFlush || _buffer.WrittenCount >= 1 << 16) FlushLocked();
            }
        }

        /// <summary>Called every millisecond by the bridge: sends whatever accumulated.</summary>
        public void FlushIfPending()
        {
            lock (_lock)
            {
                if (_buffer.WrittenCount > 0) FlushLocked();
            }
        }

        private void FlushLocked()
        {
            var data = _buffer.WrittenSpan;
            var sent = 0;
            while (sent < data.Length) sent += _socket.Send(data.Slice(sent));
            _buffer.Clear();
            _wantFlush = false;
        }
    }

    /// <summary>Splits the incoming byte stream into frames.</summary>
    internal sealed class FrameReader
    {
        private byte[] _buffer = new byte[1 << 20];
        private int _filled;

        /// <summary>Reads once from the socket and yields every complete frame; returns false when the peer closed.</summary>
        public bool Read(Socket socket, List<ReadOnlyMemory<byte>> frames)
        {
            frames.Clear();
            if (_filled == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            int read;
            try { read = socket.Receive(_buffer, _filled, _buffer.Length - _filled, SocketFlags.None); }
            catch (SocketException) { return false; }
            if (read <= 0) return false;
            _filled += read;

            var offset = 0;
            while (_filled - offset >= 4)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(offset, 4));
                if (length <= 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("bad frame length " + length);
                if (_filled - offset - 4 < length) break;
                // copy: the caller may keep the frame while the buffer is compacted
                frames.Add(new ReadOnlyMemory<byte>(_buffer.AsSpan(offset + 4, length).ToArray()));
                offset += 4 + length;
            }
            if (offset > 0)
            {
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, _filled - offset);
                _filled -= offset;
            }
            return true;
        }
    }

    /// <summary>A parsed frame header; the payload stays in the reader for the handler to decode.</summary>
    internal struct FrameHeader
    {
        public FrameType Type;
        public uint? Id;
        public string Name;

        public static FrameHeader Read(ref MessagePackReader reader)
        {
            var count = reader.ReadArrayHeader();
            if (count < 4) throw new InvalidDataException("frame array has " + count + " elements");
            var h = new FrameHeader { Type = (FrameType)reader.ReadByte() };
            h.Id = reader.TryReadNil() ? (uint?)null : reader.ReadUInt32();
            h.Name = reader.TryReadNil() ? null : reader.ReadString();
            return h;
        }
    }

    /// <summary>MessagePack values as plain CLR objects (the shape the dispatcher converts from) and back.</summary>
    internal static class Wire
    {
        public static object Read(ref MessagePackReader reader)
        {
            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Nil: reader.ReadNil(); return null;
                case MessagePackType.Boolean: return reader.ReadBoolean();
                case MessagePackType.Integer: return reader.NextCode == MessagePackCode.UInt64 ? (object)reader.ReadUInt64() : reader.ReadInt64();
                case MessagePackType.Float: return reader.NextCode == MessagePackCode.Float32 ? (object)(double)reader.ReadSingle() : reader.ReadDouble();
                case MessagePackType.String: return reader.ReadString();
                case MessagePackType.Binary: return reader.ReadBytes()?.ToArray();
                case MessagePackType.Array:
                {
                    var n = reader.ReadArrayHeader();
                    var list = new object[n];
                    for (var i = 0; i < n; i++) list[i] = Read(ref reader);
                    return list;
                }
                case MessagePackType.Map:
                {
                    var n = reader.ReadMapHeader();
                    var map = new Dictionary<string, object>(n);
                    for (var i = 0; i < n; i++)
                    {
                        var key = reader.NextMessagePackType == MessagePackType.String ? reader.ReadString() : Convert.ToString(Read(ref reader));
                        map[key ?? ""] = Read(ref reader);
                    }
                    return map;
                }
                default:
                    reader.Skip();
                    return null;
            }
        }

        public static void Write(ref MessagePackWriter writer, object value)
        {
            switch (value)
            {
                case null: writer.WriteNil(); break;
                case bool b: writer.Write(b); break;
                case byte v: writer.Write(v); break;
                case sbyte v: writer.Write(v); break;
                case short v: writer.Write(v); break;
                case ushort v: writer.Write(v); break;
                case int v: writer.Write(v); break;
                case uint v: writer.Write(v); break;
                case long v: writer.Write(v); break;
                case ulong v: writer.Write(v); break;
                case float v: writer.Write(v); break;
                case double v: writer.Write(v); break;
                case decimal v: writer.Write((double)v); break;
                case string s: writer.Write(s); break;
                case byte[] bytes: writer.Write(bytes); break;
                case Enum e: writer.Write(Convert.ToInt64(e)); break;
                case IDictionary<string, object> map:
                    writer.WriteMapHeader(map.Count);
                    foreach (var kv in map) { writer.Write(kv.Key); Write(ref writer, kv.Value); }
                    break;
                case System.Collections.IEnumerable list:
                {
                    var items = new List<object>();
                    foreach (var item in list) items.Add(item);
                    writer.WriteArrayHeader(items.Count);
                    foreach (var item in items) Write(ref writer, item);
                    break;
                }
                default:
                    writer.Write(value.ToString());
                    break;
            }
        }
    }
}

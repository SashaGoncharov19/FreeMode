using System;
using GTANetworkServer.Constant;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using MessagePack;

namespace GTANetworkServer.Runtime
{
    /// <summary>
    /// The engine side of the bridge to the Bun runtime that hosts TypeScript server resources (D-09,
    /// docs/tasks/T-006-server-runtime-on-bun-bridge.md). One runtime process per server, one connection at a time:
    /// a Unix domain socket on Linux/macOS, loopback TCP on Windows, both guarded by a token the runtime gets in its
    /// environment. Threads: the accept/reader thread parses frames and runs API calls right there (the same model as a
    /// C# resource's worker thread); the tick thread calls <see cref="Tick"/> for supervision and the 10 Hz state mirror;
    /// a flush thread sends batched frames every millisecond; any thread may send events.
    /// </summary>
    internal sealed class RuntimeBridge : IDisposable
    {
        private sealed class RuntimeResource
        {
            public Resource Resource;
            public ScriptingEngine Engine;
            public string Directory;
            public string Entry;
        }

        private sealed class Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public string Error;
        }

        private const int CancelableTimeoutMs = 250;

        private readonly string _runtimeDir;
        private readonly string _bun;
        private readonly string _token = Guid.NewGuid().ToString("N");
        private readonly ApiDispatcher _dispatcher = new ApiDispatcher();
        private readonly StateMirror _mirror = new StateMirror();
        private readonly Dictionary<string, RuntimeResource> _resources = new Dictionary<string, RuntimeResource>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<uint, Pending> _pending = new ConcurrentDictionary<uint, Pending>();
        private readonly Queue<Action<FrameWriter>> _outbox = new Queue<Action<FrameWriter>>(); // frames written before the runtime is up
        private readonly object _connLock = new object();
        private readonly Socket _listener;
        private readonly string _socketArg;
        private readonly string _socketPath;
        private Socket _conn;
        private FrameWriter _writer;
        private RuntimeProcess _process;
        private volatile bool _connected;
        private volatile bool _disposed;
        private int _nextId;
        private int _tick;
        private DateTime _nextStart = DateTime.MinValue;
        private readonly Queue<DateTime> _starts = new Queue<DateTime>();
        private int _timeoutsLogged;

        public bool Connected => _connected;

        /// <summary>Creates the bridge (listener + runtime process) or returns null with the reason (no Bun, no runtime folder).</summary>
        public static RuntimeBridge Create(out string error)
        {
            var bun = RuntimeProcess.FindBun(out error);
            if (bun == null) return null;
            var dir = Path.Combine(AppContext.BaseDirectory, "runtime");
            if (!File.Exists(Path.Combine(dir, "main.ts"))) dir = Path.GetFullPath("runtime");
            if (!File.Exists(Path.Combine(dir, "main.ts"))) { error = "runtime/main.ts not found next to the server (the Bun runtime folder is missing)"; return null; }
            try
            {
                return new RuntimeBridge(bun, dir);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private RuntimeBridge(string bun, string runtimeDir)
        {
            _bun = bun;
            _runtimeDir = runtimeDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _socketArg = "tcp:" + ((IPEndPoint)_listener.LocalEndPoint!).Port;
            }
            else
            {
                _socketPath = Path.Combine(Path.GetTempPath(), "gtan-runtime-" + Environment.ProcessId + ".sock");
                if (File.Exists(_socketPath)) File.Delete(_socketPath);
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                _socketArg = "unix:" + _socketPath;
            }
            _listener.Listen(1);
            new Thread(AcceptLoop) { IsBackground = true, Name = "runtime bridge" }.Start();
            new Thread(FlushLoop) { IsBackground = true, Name = "runtime flush" }.Start();
            StartProcess();
            Program.Output("Bun runtime for TypeScript resources: " + _bun + ", " + _socketArg + ", " + _dispatcher.MemberCount + " API members");
        }

        // ---- resources ----

        /// <summary>A TypeScript server script of a resource: loaded by the runtime now (or as soon as it is up).</summary>
        public void Register(Resource resource, ScriptingEngine engine, string entryRelativePath)
        {
            var item = new RuntimeResource
            {
                Resource = resource,
                Engine = engine,
                Directory = Path.GetFullPath(Path.Combine("resources", resource.DirectoryName)),
                Entry = entryRelativePath,
            };
            lock (_resources) _resources[resource.DirectoryName] = item;
            // not queued: a runtime that connects later loads every registered resource from the hello handshake
            Send(w => WriteLoad(w, item), flushImmediately: true, queueWhenDown: false);
        }

        public void Unregister(string resourceName)
        {
            lock (_resources) if (!_resources.Remove(resourceName)) return;
            Send(w => w.Write(FrameType.Unload, null, resourceName, null, false), flushImmediately: true, queueWhenDown: false);
        }

        private static void WriteLoad(FrameWriter w, RuntimeResource item)
        {
            w.Write(FrameType.Load, null, item.Resource.DirectoryName, (ref MessagePackWriter mp) =>
            {
                mp.WriteMapHeader(4);
                mp.Write("resource"); mp.Write(item.Resource.DirectoryName);
                mp.Write("dir"); mp.Write(item.Directory);
                mp.Write("entry"); mp.Write(item.Entry);
                mp.Write("settings");
                var settings = item.Resource.Settings;
                mp.WriteMapHeader(settings?.Count ?? 0);
                if (settings != null)
                    foreach (var kv in settings) { mp.Write(kv.Key); mp.Write(kv.Value.Value ?? kv.Value.DefaultValue ?? ""); }
            }, flushImmediately: true);
        }

        // ---- events ----

        /// <summary>An event for one resource's handlers; nothing is awaited.</summary>
        public void Event(ScriptingEngine engine, string name, params object[] args)
        {
            var resource = engine.ResourceParent?.DirectoryName;
            var wire = args.Select(ApiDispatcher.ToWire).ToArray();
            Send(w => w.Write(FrameType.Event, null, name, (ref MessagePackWriter mp) => WriteEventPayload(ref mp, resource, wire), false), flushImmediately: false);
        }

        /// <summary>A cancelable event (chat message/command, connection): waits up to 250 ms for the runtime's answer; true = cancel.</summary>
        public bool EventCancelable(ScriptingEngine engine, string name, params object[] args)
        {
            if (!_connected) return false;
            var resource = engine.ResourceParent?.DirectoryName;
            var wire = args.Select(ApiDispatcher.ToWire).ToArray();
            var id = (uint)Interlocked.Increment(ref _nextId);
            var pending = new Pending();
            _pending[id] = pending;
            Send(w => w.Write(FrameType.Event, id, name, (ref MessagePackWriter mp) => WriteEventPayload(ref mp, resource, wire), true), flushImmediately: true);
            if (!pending.Done.Wait(CancelableTimeoutMs))
            {
                _pending.TryRemove(id, out _);
                if (_timeoutsLogged++ < 5) Program.Output("Bun runtime: no answer to " + name + " within " + CancelableTimeoutMs + " ms (resource " + resource + "); not cancelled", LogCat.Warn);
                return false;
            }
            switch (pending.Result)
            {
                case bool b: return b;
                case IDictionary<string, object> map when map.TryGetValue("cancel", out var c): return c is bool cb && cb;
                default: return false;
            }
        }

        private static void WriteEventPayload(ref MessagePackWriter mp, string resource, object[] args)
        {
            mp.WriteMapHeader(2);
            mp.Write("r"); mp.Write(resource ?? "");
            mp.Write("a"); mp.WriteArrayHeader(args.Length);
            foreach (var a in args) Wire.Write(ref mp, a);
        }

        // ---- sending ----

        private void Send(Action<FrameWriter> write, bool flushImmediately, bool queueWhenDown = true)
        {
            lock (_connLock)
            {
                if (_writer != null && _connected)
                {
                    try { write(_writer); return; }
                    catch (Exception ex) { Program.Output("Bun runtime: send failed: " + ex.Message, LogCat.Warn); return; }
                }
                // not up yet (starting, or restarting): keep a bounded backlog of events for when it connects
                if (queueWhenDown && _outbox.Count < 2000) _outbox.Enqueue(write);
            }
        }

        private void FlushLoop()
        {
            while (!_disposed)
            {
                Thread.Sleep(1);
                FrameWriter w;
                lock (_connLock) w = _writer;
                try { w?.FlushIfPending(); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        // ---- the runtime process ----

        private void StartProcess()
        {
            var now = DateTime.UtcNow;
            while (_starts.Count > 0 && (now - _starts.Peek()).TotalSeconds > 60) _starts.Dequeue();
            if (_starts.Count >= 5)
            {
                if (_nextStart != DateTime.MaxValue) Program.Output("Bun runtime died 5 times within a minute; not restarting it (fix the resource, then restart the server)", LogCat.Error);
                _nextStart = DateTime.MaxValue;
                return;
            }
            _starts.Enqueue(now);
            _process?.Dispose();
            _process = new RuntimeProcess(_runtimeDir);
            try
            {
                _process.Start(_bun, _socketArg, _token);
            }
            catch (Exception ex)
            {
                Program.Output("Bun runtime could not start: " + ex.Message, LogCat.Error);
            }
            var attempt = _starts.Count;
            _nextStart = now.AddSeconds(attempt <= 1 ? 1 : attempt == 2 ? 2 : 5);
        }

        /// <summary>Tick thread, 60 Hz: restarts a dead runtime (back-off 1, 2, 5 s) and publishes the state mirror at 10 Hz.</summary>
        public void Tick()
        {
            if (_disposed) return;
            if (_process != null && !_process.IsRunning && DateTime.UtcNow >= _nextStart && _nextStart != DateTime.MaxValue)
            {
                Program.Output("Bun runtime is not running; starting it again", LogCat.Warn);
                StartProcess();
            }
            if (++_tick % 6 != 0 || !_connected) return;
            lock (_connLock)
            {
                if (_writer == null) return;
                try { _mirror.Publish(_writer, Program.ServerInstance.Clients); }
                catch (Exception ex) { Program.Output("Bun runtime: state mirror failed: " + ex.Message, LogCat.Warn); }
            }
        }

        // ---- the connection ----

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                Socket conn;
                try { conn = _listener.Accept(); }
                catch (Exception) { if (_disposed) return; Thread.Sleep(100); continue; }
                try { ServeConnection(conn); }
                catch (Exception ex) { if (!_disposed) Program.Output("Bun runtime connection ended: " + ex.Message, LogCat.Warn); }
                finally
                {
                    lock (_connLock)
                    {
                        if (_conn == conn) { _conn = null; _writer = null; }
                        _connected = false;
                    }
                    foreach (var kv in _pending) { kv.Value.Error = "runtime gone"; kv.Value.Done.Set(); }
                    _pending.Clear();
                    try { conn.Dispose(); } catch { }
                    if (!_disposed) Program.Output("Bun runtime disconnected", LogCat.Warn);
                }
            }
        }

        private void ServeConnection(Socket conn)
        {
            if (conn.AddressFamily == AddressFamily.InterNetwork) conn.NoDelay = true;
            var writer = new FrameWriter(conn);
            var reader = new FrameReader();
            var frames = new List<ReadOnlyMemory<byte>>();
            var greeted = false;
            while (reader.Read(conn, frames))
            {
                foreach (var frame in frames)
                {
                    var mp = new MessagePackReader(frame);
                    var header = FrameHeader.Read(ref mp);
                    if (!greeted)
                    {
                        // the first frame must be call "hello" [token]
                        var payload = Wire.Read(ref mp) as object[];
                        if (header.Type != FrameType.Call || header.Name != "hello" || payload == null || payload.Length == 0 || (payload[0] as string) != _token)
                        {
                            Program.Output("Bun runtime: connection refused (bad hello)", LogCat.Warn);
                            return;
                        }
                        greeted = true;
                        lock (_connLock)
                        {
                            _conn = conn;
                            _writer = writer;
                            _connected = true;
                            _mirror.Reset();
                            if (header.Id.HasValue) writer.Write(FrameType.Result, header.Id, null, (ref MessagePackWriter w) => { w.WriteMapHeader(2); w.Write("ok"); w.Write(true); w.Write("tickHz"); w.Write(60); }, true);
                            RuntimeResource[] items;
                            lock (_resources) items = _resources.Values.ToArray();
                            foreach (var item in items) WriteLoad(writer, item);
                            while (_outbox.Count > 0) { try { _outbox.Dequeue()(writer); } catch (Exception ex) { Program.Output("Bun runtime: backlog frame failed: " + ex.Message, LogCat.Warn); } }
                        }
                        Program.Output("Bun runtime connected (pid " + (_process?.Pid ?? 0) + ")");
                        continue;
                    }
                    HandleFrame(writer, header, ref mp);
                }
            }
        }

        private void HandleFrame(FrameWriter writer, FrameHeader header, ref MessagePackReader mp)
        {
            switch (header.Type)
            {
                case FrameType.Call:
                {
                    var args = Wire.Read(ref mp) as object[] ?? Array.Empty<object>();
                    object result = null;
                    string error = null;
                    try
                    {
                        result = Dispatch(header.Name, args);
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        error = inner.GetType().Name + ": " + inner.Message;
                        if (!header.Id.HasValue) Program.Output("Bun runtime: " + header.Name + " failed: " + error, LogCat.Warn);
                    }
                    if (header.Id.HasValue)
                    {
                        var value = result;
                        var err = error;
                        writer.Write(FrameType.Result, header.Id, null, (ref MessagePackWriter w) =>
                        {
                            if (err != null) { w.WriteMapHeader(1); w.Write("error"); w.Write(err); }
                            else Wire.Write(ref w, value);
                        }, flushImmediately: true);
                    }
                    break;
                }
                case FrameType.Result:
                {
                    var value = Wire.Read(ref mp);
                    if (header.Id.HasValue && _pending.TryRemove(header.Id.Value, out var pending))
                    {
                        pending.Result = value;
                        pending.Done.Set();
                    }
                    break;
                }
                case FrameType.Log:
                {
                    var payload = Wire.Read(ref mp);
                    var text = payload is object[] arr && arr.Length >= 2 ? arr[1]?.ToString() : payload?.ToString();
                    var level = payload is object[] arr2 && arr2.Length >= 2 ? Convert.ToString(arr2[0]) : "info";
                    Program.Output("[" + (header.Name ?? "runtime") + "] " + text, level == "error" ? LogCat.Error : level == "warn" ? LogCat.Warn : LogCat.Info);
                    break;
                }
                default:
                    mp.Skip();
                    break;
            }
        }

        /// <summary>"resource/member": the API of that resource's engine; the call runs on the bridge thread.</summary>
        private object Dispatch(string name, object[] args)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("call without a name");
            var slash = name.IndexOf('/');
            if (slash <= 0) throw new ArgumentException("call name must be resource/member: " + name);
            var resourceName = name.Substring(0, slash);
            var member = name.Substring(slash + 1);
            RuntimeResource item;
            lock (_resources) _resources.TryGetValue(resourceName, out item);
            if (item == null) throw new InvalidOperationException("resource " + resourceName + " is not loaded");
            return _dispatcher.Invoke(item.Engine.RuntimeApi, member, args);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _process?.Dispose(); } catch { }
            lock (_connLock)
            {
                try { _conn?.Dispose(); } catch { }
                _conn = null;
                _writer = null;
                _connected = false;
            }
            try { _listener.Dispose(); } catch { }
            if (_socketPath != null) { try { File.Delete(_socketPath); } catch { } }
        }
    }
}

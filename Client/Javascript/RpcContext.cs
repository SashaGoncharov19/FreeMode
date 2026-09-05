using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GTANetwork.Util;
using GTANetworkShared;
using Microsoft.ClearScript.V8;

namespace GTANetwork.Javascript
{
    /// <summary>
    /// <c>API.rpc</c> of a client script (T-008): <c>call(name, args)</c> asks the server's handler of that name and returns a
    /// Promise; <c>register(name, handler)</c> answers <c>API.callClient</c> from the server and <c>gtan.rpc.call</c> from this
    /// resource's CEF pages. The promises and the handler table live in JavaScript (a helper evaluated in the script's engine
    /// when it starts), so every continuation runs on the script thread; C# only moves packets (<see cref="RpcRouter"/>).
    /// </summary>
    public class RpcContext
    {
        // bind(send, respond) receives two host delegates: send(name, json, timeoutMs) -> request id (0 = not connected) and
        // respond(id, ok, json, code, message) for answers to the server's calls.
        internal const string HelperSource = @"(function () {
  var pending = {}, handlers = {}, send = null, respond = null, log = null;
  // no String(x) here: in the game's engine String is the host type System.String (AddHostType), and calling it means a generic type argument
  function str(v) { return v === null || v === undefined ? '' : '' + v; }
  function trace(text) { try { if (log) log(text); } catch (e) { } }
  function fail(reject, code, message) { var e = new Error(message || code || 'rpc failed'); e.code = code || 'handler'; reject(e); }
  function parse(json) { return json === null || json === undefined || json === '' ? undefined : JSON.parse(json); }
  function call(name, args, timeoutMs) {
    trace('call ' + name + ' (send is ' + typeof send + ')');
    return new Promise(function (resolve, reject) {
      var json = JSON.stringify(args === undefined ? null : args);
      if (json.length > 65536) { fail(reject, 'size', 'arguments over 64 KB'); return; }
      var id;
      try { id = send(str(name), json, timeoutMs === undefined || timeoutMs === null ? 0 : (timeoutMs | 0)); }
      catch (e) { trace('send threw: ' + e); throw e; }
      trace('send returned #' + id);
      if (id === 0) { fail(reject, 'disconnected', 'not connected to a server'); return; }
      pending[id] = { resolve: resolve, reject: reject };
    });
  }
  function settle(id, ok, json, code, message) {
    var p = pending[id];
    trace('settle #' + id + ' ' + (ok ? 'ok' : 'error ' + code) + (p ? '' : ' (no pending call)'));
    if (!p) return; delete pending[id];
    if (ok) p.resolve(parse(json)); else fail(p.reject, code, message);
  }
  function run(fn, json, done) {
    var finished = false;
    function ok(v) { if (finished) return; finished = true; done(true, JSON.stringify(v === undefined ? null : v), null, null); }
    function bad(e) { if (finished) return; finished = true; done(false, null, (e && e.code) || 'handler', str((e && e.message) || e)); }
    try {
      var r = fn(parse(json));
      if (r && typeof r.then === 'function') r.then(ok, bad); else ok(r);
    } catch (e) { bad(e); }
  }
  function invoke(id, name, json) {
    var fn = handlers[name];
    if (!fn) { respond(id, false, null, 'unknown', 'no client handler for ' + name); return; }
    run(fn, json, function (ok, out, code, message) { respond(id, ok, out, code, message); });
  }
  function fromPage(id, name, json, timeoutMs, pageRespond) {
    trace('fromPage #' + id + ' ' + name + ' (' + typeof json + ', ' + (json === null || json === undefined ? 'no args' : json.length + ' chars') + ')');
    var fn = handlers[name];
    if (fn) { run(fn, json, function (ok, out, code, message) { pageRespond(id, ok, out, code, message); }); return; }
    call(name, parse(json), timeoutMs).then(
      function (v) { trace('fromPage #' + id + ' resolved'); pageRespond(id, true, JSON.stringify(v === undefined ? null : v), null, null); },
      function (e) { trace('fromPage #' + id + ' rejected: ' + e); pageRespond(id, false, null, (e && e.code) || 'handler', str((e && e.message) || e)); });
    trace('fromPage #' + id + ' waiting');
  }
  return {
    bind: function (s, r, l) { send = s; respond = r; log = l || null; trace('bound: send ' + typeof send + ', respond ' + typeof respond); },
    call: call,
    register: function (name, fn) { if (typeof fn !== 'function') throw new Error('rpc.register: the handler must be a function'); handlers[str(name)] = fn; },
    unregister: function (name) { delete handlers[str(name)]; },
    has: function (name) { return typeof handlers[name] === 'function'; },
    settle: settle, invoke: invoke, fromPage: fromPage
  };
})()";

        private readonly ScriptContext _owner;
        private dynamic _helper;

        internal RpcContext(ScriptContext owner)
        {
            _owner = owner;
        }

        internal string ResourceName => _owner.ParentResourceName;

        /// <summary>Evaluates the helper in the script's engine and gives it the two host delegates. Before the script itself runs.</summary>
        internal void Attach(V8ScriptEngine engine)
        {
            _helper = engine.Evaluate("gtan-rpc.js", HelperSource);
            _helper.bind(new Func<string, string, int, uint>(Send), new Action<uint, bool, string, string, string>(Respond), new Action<string>(Trace));
        }

        /// <summary>The script stops: handlers and unanswered calls are dropped; the engine is about to be disposed.</summary>
        internal void Detach()
        {
            _helper = null;
            RpcRouter.Forget(this);
        }

        /// <summary>
        /// Calls the server handler <paramref name="name"/> (registered with API.registerRpc in C# or gtan.rpc.register in TypeScript)
        /// with one JSON-serialisable argument and returns a Promise of its return value. The promise rejects with an Error whose
        /// <c>code</c> is timeout, denied, unknown, rate, handler, size, invalid or disconnected. Default timeout 10 s, at most 60 s.
        /// </summary>
        public object call(string name, object args = null, int timeoutMs = 0)
        {
            if (_helper == null) throw new InvalidOperationException("rpc is not available in this script");
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("an RPC name must not be empty");
            return _helper.call(name, args, timeoutMs);
        }

        /// <summary>
        /// Registers the handler of <paramref name="name"/> for API.callClient(player, name, args) from the server and gtan.rpc.call
        /// from this resource's CEF pages: <c>function (args) { return value; }</c>; a returned Promise is awaited; throw (an Error with
        /// a <c>code</c>) to fail the call. Registering a name again replaces the handler.
        /// </summary>
        public void register(string name, object handler)
        {
            if (_helper == null) throw new InvalidOperationException("rpc is not available in this script");
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("an RPC name must not be empty");
            _helper.register(name, handler);
            RpcRouter.Registered(name, this);
        }

        public void unregister(string name)
        {
            if (_helper == null || string.IsNullOrEmpty(name)) return;
            _helper.unregister(name);
            RpcRouter.Unregistered(name, this);
        }

        /// <summary>True when this script registered a handler of that name.</summary>
        public bool has(string name)
        {
            return _helper != null && !string.IsNullOrEmpty(name) && (bool)_helper.has(name);
        }

        // ---- script thread only ----

        internal void Settle(uint id, bool ok, string payload, string code, string message)
        {
            if (_helper != null) _helper.settle(id, ok, payload, code, message);
        }

        internal void Invoke(uint id, string name, string payload)
        {
            if (_helper != null) _helper.invoke(id, name, payload);
        }

        internal void FromPage(uint id, string name, string payload, int timeoutMs, Action<uint, bool, string, string, string> respond)
        {
            if (_helper == null) { LogManager.RuntimeLog("rpc: page #" + id + " " + name + ": the owning script has no rpc helper"); respond(id, false, null, RpcCodes.Unknown, "the owning script is gone"); return; }
            LogManager.RuntimeLog("rpc: page #" + id + " " + name + " -> " + (has(name) ? "this script" : "the server") + " (" + ResourceName + ")");
            try
            {
                _helper.fromPage(id, name, payload, timeoutMs, respond);
                LogManager.RuntimeLog("rpc: page #" + id + " handed to the script");
            }
            catch (Exception ex)
            {
                LogManager.RuntimeLog("rpc: page #" + id + " fromPage threw " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }

        private uint Send(string name, string payload, int timeoutMs)
        {
            LogManager.RuntimeLog("rpc: send delegate entered for " + name);
            return RpcRouter.Send(this, name, payload, timeoutMs);
        }

        private void Trace(string text)
        {
            LogManager.RuntimeLog("rpc: [js " + ResourceName + "] " + text);
        }

        private void Respond(uint id, bool ok, string payload, string code, string message)
        {
            RpcRouter.Respond(id, ok, payload, code, message);
        }
    }

    /// <summary>
    /// Moves RPC packets between the network thread and the scripts' <see cref="RpcContext"/>s. Everything that touches a script
    /// engine is queued on the script thread (<see cref="JavascriptHook.ThreadJumper"/>); timeouts are checked every tick.
    /// </summary>
    internal static class RpcRouter
    {
        private sealed class Pending
        {
            public RpcContext Context;
            public string Name;
            public long Deadline;
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<uint, Pending> PendingCalls = new Dictionary<uint, Pending>();
        private static readonly Dictionary<string, RpcContext> Handlers = new Dictionary<string, RpcContext>(StringComparer.Ordinal);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _nextId;

        /// <summary>Script thread: sends a request; 0 when not connected to a server.</summary>
        internal static uint Send(RpcContext context, string name, string payload, int timeoutMs)
        {
            if (!Main.IsOnServer()) { LogManager.RuntimeLog("rpc: " + name + " not sent: not connected to a server"); return 0; }
            var timeout = RpcCodes.ClampTimeout(timeoutMs);
            uint id;
            do { id = (uint)Interlocked.Increment(ref _nextId); } while (id == 0);
            lock (Lock) PendingCalls[id] = new Pending { Context = context, Name = name, Deadline = Clock.ElapsedMilliseconds + timeout };
            LogManager.RuntimeLog("rpc: -> server #" + id + " " + name + " (" + (payload == null ? 0 : payload.Length) + " chars, timeout " + timeout + " ms, from " + context.ResourceName + ")");
            Main.SendRpc(PacketType.RpcRequest, new RpcRequest
            {
                Id = id,
                Name = name,
                Resource = context.ResourceName,
                Payload = payload,
                TimeoutMs = timeout,
                Origin = RpcOrigin.Client,
            });
            return id;
        }

        internal static void Respond(uint id, bool ok, string payload, string code, string message)
        {
            Main.SendRpc(PacketType.RpcResponse, new RpcResponse { Id = id, Ok = ok, Payload = payload, ErrorCode = code, ErrorMessage = message });
        }

        /// <summary>Network thread: the server answered one of our calls.</summary>
        internal static void OnResponse(RpcResponse response)
        {
            if (response == null) return;
            Pending pending;
            lock (Lock)
            {
                if (!PendingCalls.TryGetValue(response.Id, out pending)) { LogManager.RuntimeLog("rpc: <- server #" + response.Id + " for an unknown call (answered already, or timed out)"); return; }
                PendingCalls.Remove(response.Id);
            }
            LogManager.RuntimeLog("rpc: <- server #" + response.Id + " " + pending.Name + " " + (response.Ok ? "ok (" + (response.Payload == null ? 0 : response.Payload.Length) + " chars)" : "error " + response.ErrorCode + ": " + response.ErrorMessage));
            Queue(() => pending.Context.Settle(response.Id, response.Ok, response.Payload, response.ErrorCode, response.ErrorMessage));
        }

        /// <summary>Network thread: the server calls a handler one of our scripts registered.</summary>
        internal static void OnRequest(RpcRequest request)
        {
            if (request == null) return;
            RpcContext context;
            lock (Lock) Handlers.TryGetValue(request.Name ?? "", out context);
            LogManager.RuntimeLog("rpc: <- server call #" + request.Id + " " + request.Name + (context == null ? " (no handler)" : " -> " + context.ResourceName));
            if (context == null)
            {
                Respond(request.Id, false, null, RpcCodes.Unknown, "no client handler for " + request.Name);
                return;
            }
            Queue(() => context.Invoke(request.Id, request.Name, request.Payload));
        }

        internal static void Registered(string name, RpcContext context)
        {
            lock (Lock) Handlers[name] = context;
        }

        internal static void Unregistered(string name, RpcContext context)
        {
            lock (Lock)
            {
                RpcContext current;
                if (Handlers.TryGetValue(name, out current) && current == context) Handlers.Remove(name);
            }
        }

        /// <summary>The script is going away: its handlers and unanswered calls are dropped (nothing can be settled in a disposed engine).</summary>
        internal static void Forget(RpcContext context)
        {
            lock (Lock)
            {
                foreach (var name in Handlers.Where(kv => kv.Value == context).Select(kv => kv.Key).ToList()) Handlers.Remove(name);
                foreach (var id in PendingCalls.Where(kv => kv.Value.Context == context).Select(kv => kv.Key).ToList()) PendingCalls.Remove(id);
            }
        }

        /// <summary>Script thread, every tick: calls without an answer in time are rejected with "timeout".</summary>
        internal static void Tick()
        {
            List<KeyValuePair<uint, Pending>> expired = null;
            lock (Lock)
            {
                if (PendingCalls.Count == 0) return;
                var now = Clock.ElapsedMilliseconds;
                foreach (var kv in PendingCalls)
                {
                    if (kv.Value.Deadline > now) continue;
                    if (expired == null) expired = new List<KeyValuePair<uint, Pending>>();
                    expired.Add(kv);
                }
                if (expired == null) return;
                foreach (var kv in expired) PendingCalls.Remove(kv.Key);
            }
            foreach (var kv in expired)
            {
                try { kv.Value.Context.Settle(kv.Key, false, null, RpcCodes.Timeout, "no answer to " + kv.Value.Name + " in time"); }
                catch (Exception ex) { LogManager.LogException(ex, "RPC TIMEOUT"); }
            }
        }

        /// <summary>All scripts stopped (disconnect): nothing is pending any more.</summary>
        internal static void Reset()
        {
            lock (Lock)
            {
                PendingCalls.Clear();
                Handlers.Clear();
            }
        }

        private static void Queue(Action action)
        {
            lock (JavascriptHook.ThreadJumper)
            {
                JavascriptHook.ThreadJumper.Add(() =>
                {
                    try { action(); }
                    catch (Exception ex) { LogManager.LogException(ex, "RPC"); }
                });
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GTANetworkServer.Constant;
using GTANetworkShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GTANetworkServer
{
    /// <summary>
    /// Request/response calls between scripts (T-008). A client script's <c>API.rpc.call(name, args)</c> (or a CEF page's
    /// <c>gtan.rpc.call</c>, through its client script) reaches the handler a server resource registered with
    /// <see cref="API.registerRpc(string, Func{Client, object, object})"/> (C#) or <c>gtan.rpc.register</c> (TypeScript, in the Bun
    /// runtime) and is answered within the caller's timeout; <see cref="API.callClient"/> goes the other way. Names are global
    /// across resources (prefix them: "auth:login"). Before a handler runs: the payload size (64 KB), the caller's token bucket
    /// (<see cref="RatePerSecond"/>), the handler's allow check. Requests arrive on the tick thread; C# handlers run on their
    /// resource's script thread, TypeScript ones in the runtime; the answer is sent from wherever the handler finished. Errors carry
    /// a code and a message, never a stack trace.
    /// </summary>
    internal sealed class RpcDispatcher
    {
        /// <summary>Requests one player may make per second: a token bucket of this size, refilled at this rate.</summary>
        internal const int RatePerSecond = 30;

        private sealed class Registration
        {
            public string Name;
            public ScriptingEngine Engine;
            /// <summary>Null for a TypeScript resource: the handler lives in the Bun runtime and is reached through an "rpcRequest" event.</summary>
            public Func<Client, dynamic, object> Handler;
            public Func<Client, bool> Allow;
            public string Resource => Engine?.ResourceParent?.DirectoryName;
        }

        private sealed class Bucket
        {
            public double Tokens = RatePerSecond;
            public long Stamp = Environment.TickCount64;
        }

        private sealed class PendingCall
        {
            public Client Player;
            public string Name;
            public long Deadline;
            public TaskCompletionSource<object> Completion;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Registration> _handlers = new Dictionary<string, Registration>(StringComparer.Ordinal);
        private readonly Dictionary<Client, Bucket> _buckets = new Dictionary<Client, Bucket>();
        private readonly Dictionary<uint, PendingCall> _pending = new Dictionary<uint, PendingCall>();
        private int _nextId;
        private int _rateLogged;

        // ---- registry ----

        public void Register(string name, ScriptingEngine engine, Func<Client, dynamic, object> handler, Func<Client, bool> allow)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("an RPC name must not be empty");
            var registration = new Registration { Name = name, Engine = engine, Handler = handler, Allow = allow };
            lock (_lock)
            {
                if (_handlers.TryGetValue(name, out var existing) && existing.Resource != registration.Resource)
                    Program.Output("RPC " + name + ": registered by " + (existing.Resource ?? "?") + ", now taken over by " + (registration.Resource ?? "?"), LogCat.Warn);
                _handlers[name] = registration;
            }
        }

        public void Unregister(string name)
        {
            if (name == null) return;
            lock (_lock) _handlers.Remove(name);
        }

        /// <summary>A resource stopped: its handlers go with it.</summary>
        public void UnregisterResource(string resource)
        {
            lock (_lock)
            {
                foreach (var name in _handlers.Where(kv => kv.Value.Resource == resource).Select(kv => kv.Key).ToList()) _handlers.Remove(name);
            }
        }

        internal int HandlerCount { get { lock (_lock) return _handlers.Count; } }

        // ---- requests from clients (tick thread) ----

        public void HandleRequest(Client sender, RpcRequest request)
        {
            if (sender == null || request == null) return;
            if (string.IsNullOrEmpty(request.Name)) { Respond(sender, request.Id, false, null, RpcCodes.Invalid, "request without a name"); return; }
            if (request.Payload != null && request.Payload.Length > RpcCodes.MaxPayloadBytes)
            {
                Respond(sender, request.Id, false, null, RpcCodes.Size, "arguments over " + RpcCodes.MaxPayloadBytes / 1024 + " KB");
                return;
            }
            if (!TakeToken(sender))
            {
                if (_rateLogged++ < 5) Program.Output("RPC: " + sender.name + " sent more than " + RatePerSecond + " requests per second (" + request.Name + "); refused", LogCat.Warn);
                Respond(sender, request.Id, false, null, RpcCodes.Rate, "more than " + RatePerSecond + " requests per second");
                return;
            }

            Registration registration;
            lock (_lock) _handlers.TryGetValue(request.Name, out registration);
            if (registration == null) { Respond(sender, request.Id, false, null, RpcCodes.Unknown, "no handler for " + request.Name); return; }

            if (registration.Allow != null)
            {
                bool allowed;
                try { allowed = registration.Allow(sender); }
                catch (Exception ex)
                {
                    Program.Output("RPC " + request.Name + ": the allow check failed: " + ex.Message, LogCat.Warn);
                    allowed = false;
                }
                if (!allowed) { Respond(sender, request.Id, false, null, RpcCodes.Denied, "not allowed"); return; }
            }

            if (registration.Handler == null) { RunInRuntime(sender, request, registration); return; }

            JToken args;
            try { args = RpcJson.Parse(request.Payload); }
            catch (Exception ex) { Respond(sender, request.Id, false, null, RpcCodes.Invalid, "the arguments are not valid JSON: " + ex.Message); return; }

            var handler = registration.Handler;
            Action run = () =>
            {
                object result;
                try { result = handler(sender, args); }
                catch (Exception ex) { Fail(sender, request, ex); return; }
                Complete(sender, request, result);
            };
            if (registration.Engine != null) registration.Engine.Enqueue(run);
            else run();
        }

        private void RunInRuntime(Client sender, RpcRequest request, Registration registration)
        {
            var runtime = registration.Engine?.Runtime;
            if (runtime == null)
            {
                Respond(sender, request.Id, false, null, RpcCodes.Unknown, "the TypeScript runtime of " + (registration.Resource ?? "?") + " is not running");
                return;
            }
            var timeout = RpcCodes.ClampTimeout(request.TimeoutMs);
            runtime.EventWithResult(registration.Engine, "rpcRequest", new object[] { sender, request.Name, request.Payload }, timeout, (result, error) =>
            {
                if (error != null)
                {
                    if (error == RpcCodes.Timeout) Respond(sender, request.Id, false, null, RpcCodes.Timeout, "no answer from the runtime within " + timeout + " ms");
                    else Respond(sender, request.Id, false, null, RpcCodes.Handler, error);
                    return;
                }
                var map = result as IDictionary<string, object>;
                if (map == null) { Respond(sender, request.Id, false, null, RpcCodes.Handler, "the runtime answered without a result"); return; }
                map.TryGetValue("ok", out var okValue);
                if (okValue is bool ok && ok)
                {
                    map.TryGetValue("value", out var value);
                    string payload;
                    try { payload = RpcJson.Serialize(value); }
                    catch (Exception ex) { Fail(sender, request, ex); return; }
                    Respond(sender, request.Id, true, payload, null, null);
                }
                else
                {
                    map.TryGetValue("code", out var code);
                    map.TryGetValue("message", out var message);
                    Respond(sender, request.Id, false, null, code as string ?? RpcCodes.Handler, message as string ?? "the handler failed");
                }
            });
        }

        private void Complete(Client sender, RpcRequest request, object result)
        {
            if (result is Task task)
            {
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted) { Fail(sender, request, t.Exception); return; }
                    if (t.IsCanceled) { Respond(sender, request.Id, false, null, RpcCodes.Handler, "the handler was cancelled"); return; }
                    Complete(sender, request, TaskResult(t));
                }, TaskContinuationOptions.ExecuteSynchronously);
                return;
            }
            string payload;
            try { payload = RpcJson.Serialize(result); }
            catch (Exception ex) { Fail(sender, request, ex); return; }
            Respond(sender, request.Id, true, payload, null, null);
        }

        /// <summary>The value of a finished Task: null for a plain Task (or an async void-like Task), else its Result.</summary>
        internal static object TaskResult(Task task)
        {
            if (task is Task<object> typed) return typed.Result;
            var type = task.GetType();
            if (!type.IsGenericType) return null;
            var value = type.GetProperty("Result")?.GetValue(task);
            return value != null && value.GetType().Name == "VoidTaskResult" ? null : value;
        }

        private static void Fail(Client sender, RpcRequest request, Exception ex)
        {
            var inner = ex;
            while ((inner is TargetInvocationException || inner is AggregateException) && inner.InnerException != null) inner = inner.InnerException;
            if (inner is RpcException rpc)
            {
                Respond(sender, request.Id, false, null, rpc.Code, rpc.Message);
                return;
            }
            // the whole exception stays in the server log; the caller gets the message only
            Program.Output("RPC " + request.Name + " from " + sender.name + " failed: " + inner, LogCat.Warn);
            Respond(sender, request.Id, false, null, RpcCodes.Handler, inner.Message);
        }

        private static void Respond(Client player, uint id, bool ok, string payload, string code, string message)
        {
            try
            {
                if (player.NetConnection == null) return;
                var response = new RpcResponse { Id = id, Ok = ok, Payload = payload, ErrorCode = code, ErrorMessage = message };
                Program.ServerInstance.SendToClient(player, response, PacketType.RpcResponse, true, ConnectionChannel.Rpc);
            }
            catch (Exception ex)
            {
                Program.Output("RPC: could not answer " + player.name + ": " + ex.Message, LogCat.Warn);
            }
        }

        private bool TakeToken(Client player)
        {
            lock (_lock)
            {
                if (!_buckets.TryGetValue(player, out var bucket)) _buckets[player] = bucket = new Bucket();
                var now = Environment.TickCount64;
                bucket.Tokens = Math.Min(RatePerSecond, bucket.Tokens + (now - bucket.Stamp) / 1000.0 * RatePerSecond);
                bucket.Stamp = now;
                if (bucket.Tokens < 1) return false;
                bucket.Tokens -= 1;
                return true;
            }
        }

        // ---- calls to clients ----

        public Task<object> CallClient(Client player, string name, object args, int timeoutMs, string resource)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (string.IsNullOrEmpty(name)) { completion.SetException(new RpcException(RpcCodes.Invalid, "an RPC name must not be empty")); return completion.Task; }
            if (player == null || player.NetConnection == null) { completion.SetException(new RpcException(RpcCodes.Disconnected, "the player is not connected")); return completion.Task; }
            string payload;
            try { payload = RpcJson.Serialize(args); }
            catch (Exception ex) { completion.SetException(new RpcException(RpcCodes.Invalid, "the arguments cannot be serialised: " + ex.Message)); return completion.Task; }
            if (payload.Length > RpcCodes.MaxPayloadBytes) { completion.SetException(new RpcException(RpcCodes.Size, "arguments over " + RpcCodes.MaxPayloadBytes / 1024 + " KB")); return completion.Task; }

            var timeout = RpcCodes.ClampTimeout(timeoutMs);
            var id = (uint)Interlocked.Increment(ref _nextId);
            lock (_lock) _pending[id] = new PendingCall { Player = player, Name = name, Deadline = Environment.TickCount64 + timeout, Completion = completion };
            try
            {
                var request = new RpcRequest { Id = id, Name = name, Resource = resource, Payload = payload, TimeoutMs = timeout, Origin = RpcOrigin.Server };
                Program.ServerInstance.SendToClient(player, request, PacketType.RpcRequest, true, ConnectionChannel.Rpc);
            }
            catch (Exception ex)
            {
                lock (_lock) _pending.Remove(id);
                completion.TrySetException(new RpcException(RpcCodes.Disconnected, ex.Message));
            }
            return completion.Task;
        }

        public void HandleResponse(Client sender, RpcResponse response)
        {
            if (sender == null || response == null) return;
            PendingCall call;
            lock (_lock)
            {
                // ids are global: only the player the call went to may answer it
                if (!_pending.TryGetValue(response.Id, out call) || call.Player != sender) return;
                _pending.Remove(response.Id);
            }
            if (!response.Ok)
            {
                call.Completion.TrySetException(new RpcException(response.ErrorCode ?? RpcCodes.Handler, response.ErrorMessage));
                return;
            }
            try { call.Completion.TrySetResult(RpcJson.Parse(response.Payload)); }
            catch (Exception ex) { call.Completion.TrySetException(new RpcException(RpcCodes.Invalid, "the answer is not valid JSON: " + ex.Message)); }
        }

        /// <summary>Tick thread: calls without an answer in time fail with "timeout".</summary>
        public void Tick()
        {
            List<KeyValuePair<uint, PendingCall>> expired = null;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                var now = Environment.TickCount64;
                foreach (var kv in _pending)
                    if (kv.Value.Deadline <= now) (expired ??= new List<KeyValuePair<uint, PendingCall>>()).Add(kv);
                if (expired == null) return;
                foreach (var kv in expired) _pending.Remove(kv.Key);
            }
            foreach (var kv in expired)
                kv.Value.Completion.TrySetException(new RpcException(RpcCodes.Timeout, "no answer from " + kv.Value.Player.name + " to " + kv.Value.Name + " in time"));
        }

        public void PlayerDisconnected(Client player)
        {
            if (player == null) return;
            List<PendingCall> dropped = null;
            lock (_lock)
            {
                _buckets.Remove(player);
                foreach (var kv in _pending.Where(kv => kv.Value.Player == player).ToList())
                {
                    _pending.Remove(kv.Key);
                    (dropped ??= new List<PendingCall>()).Add(kv.Value);
                }
            }
            if (dropped == null) return;
            foreach (var call in dropped) call.Completion.TrySetException(new RpcException(RpcCodes.Disconnected, player.name + " disconnected"));
        }
    }

    /// <summary>JSON for RPC payloads: script values (entities as handles, Vector3 as {x,y,z}) to JSON and back to plain objects.</summary>
    internal static class RpcJson
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.CreateDefault();

        internal static string Serialize(object value) => ToToken(value).ToString(Formatting.None);

        /// <summary>The JSON value of a payload; null for no payload or a JSON null. Dates stay strings.</summary>
        internal static JToken Parse(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            using (var reader = new JsonTextReader(new StringReader(payload)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                return token.Type == JTokenType.Null || token.Type == JTokenType.Undefined ? null : token;
            }
        }

        internal static JToken ToToken(object value)
        {
            switch (value)
            {
                case null: return JValue.CreateNull();
                case JToken token: return token;
                case string s: return new JValue(s);
                case bool b: return new JValue(b);
                case Enum e: return new JValue(System.Convert.ToInt64(e));
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal: return new JValue(value);
                case NetHandle h: return new JValue(h.Value);
                case Client c: return new JValue(c.handle.Value);
                case Entity e: return new JValue(e.handle.Value);
                case ColShape shape: return new JValue(shape.handle);
                case Vector3 v: return new JObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
                case Color color: return new JObject { ["r"] = color.red, ["g"] = color.green, ["b"] = color.blue, ["a"] = color.alpha };
                case Task: throw new InvalidOperationException("a Task must finish before its value is serialised");
                case IDictionary dict:
                {
                    var o = new JObject();
                    foreach (DictionaryEntry kv in dict) o[System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = ToToken(kv.Value);
                    return o;
                }
                case IEnumerable list:
                {
                    var a = new JArray();
                    foreach (var item in list) a.Add(ToToken(item));
                    return a;
                }
                default: return JToken.FromObject(value, Serializer);
            }
        }

        /// <summary>JSON to what the bridge and MessagePack know: Dictionary&lt;string, object&gt;, List&lt;object&gt;, long, double, string, bool, null.</summary>
        internal static object ToPlain(JToken token)
        {
            switch (token)
            {
                case null: return null;
                case JObject o:
                {
                    var map = new Dictionary<string, object>();
                    foreach (var property in o.Properties()) map[property.Name] = ToPlain(property.Value);
                    return map;
                }
                case JArray a: return a.Select(ToPlain).ToList();
                case JValue v: return v.Value;
                default: return token.ToString(Formatting.None);
            }
        }
    }
}

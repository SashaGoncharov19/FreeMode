# T-008 — Typed RPC: server ⇄ client ⇄ CEF with request ids, timeouts, permissions, rate limits

Status: done
Epic: E-05 RPC and protocol security
Size: L
Branch: task/T-008-rpc from the integration branch
Depends on: T-004
PR: yes

## Goal

`await rpc.call("shop:buy", {item: 3})` from a client script reaches a server handler registered with
`API.registerRpc("shop:buy", handler, {allow: p => p.getData("auth:account") != null})` and resolves with its return
value (or rejects with a typed error) within a timeout; the server can call `rpc.callClient(player, name, args)`; a CEF
page can `await gtan.rpc.call(name, args)` (routed through its resource's client script to the server when the name is
server-side). All payloads are validated against declared TS types at the boundary.

## Why

Today everything is fire-and-forget (`triggerClientEvent`/`onServerEventTrigger`, `resourceCall`), so every request/response
is hand-rolled per gamemode; there is no permission model and no rate limiting.

## Scope

* In: messages, dispatchers on the three sides, TS API and typings, permissions, timeouts, rate limits, tests with the bot.
* Out: encryption/authentication of the session (T-009).

## Files

* New: `Shared/Rpc/RpcMessages.cs` (protobuf: `RpcRequest {id, name, payload(bytes JSON), from(0 server,1 client,2 cef)}`,
  `RpcResponse {id, ok, payload, error{code,message}}`), `Shared/Rpc/RpcCodes.cs`.
* Change: `Shared/Packets.cs` (`PacketType.RpcRequest = 41`, `RpcResponse = 42`; `ConnectionChannel.Rpc = 12`),
  `Server/ProcessMessages.cs` (handlers at the `PacketType` switch :240), `Server/API.cs` (`registerRpc`, `unregisterRpc`,
  `callClient(player, name, args, timeoutMs)` returning `Task<object>`; JS sees a Promise via ClearScript's task conversion),
  `Server/Managers/RpcDispatcher.cs` (new: registry, permission check, per-player token bucket 30 req/s, timeout table),
  `Client/Main/Network/ProcessMessages.cs` (handlers), `Client/Javascript/JavascriptHook.cs` (`API.rpc = new RpcContext(...)`:
  `call`, `register`, promise plumbing on the script thread via `ThreadJumper`), `Subprocess/GTANetwork.CefHost/Program.cs`
  (`ResourceBridgeInjector.Shim`: `gtan.rpc.call` → `CefSharp.PostMessage({type:"rpc", id, name, args})`; responses arrive as
  `eval` of `gtan.rpc._resolve(id, ok, payload)`), `Client/GUI/CEFManager.cs` (`jsMessage` type `rpc` → the resource's `RpcContext`),
  `Tools/GTANetwork.Bot/Program.cs` (`--rpc name json` for the test), `eng/integration-test.sh` (an RPC round trip through
  freeroam), `types/` emit (T-004's generator emits `registerRpc`/`rpc.call` generics from a small hand-written `types/rpc.d.ts`),
  `docs/CODEMAP.md` §8–§9, `CHANGELOG.md`.

## Approach

1. Wire: reliable-ordered channel `Rpc`; ids are per-connection incrementing `uint`; timeout default 10 s, max 60 s.
2. Server dispatcher on the tick thread (handlers run on the tick thread like events); async handlers (`Task`) complete later
   and the response is sent from the tick when the task finishes (poll a completion queue per tick).
3. Client dispatcher: responses resolve promises on the script thread; requests from the server run handlers registered by scripts.
4. CEF: request ids namespaced per browser; the resource script decides whether a name is local (`rpc.register` in the client
   script) or forwarded to the server (`rpc.call`).
5. Errors: `RpcError {code: "timeout"|"denied"|"unknown"|"rate"|"handler", message}`; never leak stack traces to clients.
6. Bot test: freeroam registers `ping` (returns `{t: now}`); the bot calls it and asserts a response within 200 ms.

## Acceptance criteria

- [x] Bot RPC round trip in `eng/integration-test.sh`; denied call returns `denied`; the 31st call within a second returns `rate`
      (the test sends 40 at once and expects at least one `rate`).
- [ ] Owner check: the `auth` login form uses `gtan.rpc.call("auth:login", …)` and shows the server's error text on a wrong password
      (bot-tested through the same handler; the page itself needs the game).
- [x] The RPC typings compile in the sample resource (`RpcContext` in `client.d.ts`, `gtan.rpc` in `cef.d.ts`, `registerRpc`/`callClient` in `server.d.ts`).

## Test plan

`docker compose run --rm dev eng/dev-test.sh`; owner run with the `auth` page.

## Risks and notes

Payload as JSON inside protobuf is a compromise (MessagePack later if size matters); size limit 64 KB per request.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-008-rpc`; `eng/dev-test.sh` green; PR opened.

* 2026-09-05 owner's runs 1–3: RPC from the `auth` page never answered in game while the bot proved the server. The JavaScript
  trace added in #18 showed `send threw: Error: Invalid generic type argument`: the helper called `String(name)`, and in the
  game's engine `String` is the host type `System.String` (`AddHostType("String", typeof(string))` in `StartScript`), so
  ClearScript read the call as a generic type argument; the rejection handler used `String(...)` too and threw unseen. Fixed on
  `task/T-008-client-helper-string` (`str()` instead of `String()`), reproduced and verified in ClearScript with `String`
  registered the same way, then in game with the new autotest (`Client/Util/AutoTest.cs`).

## Result

* **Changed**: `Shared/Packets.cs` (`PacketType.RpcRequest = 41`, `RpcResponse = 42`; `ConnectionChannel.Rpc = 12`), new
  `Shared/Rpc/RpcMessages.cs` (`RpcRequest {Id, Name, Resource, Payload (one JSON value), TimeoutMs, Origin}`, `RpcResponse {Id, Ok,
  Payload, ErrorCode, ErrorMessage}`) and `Shared/Rpc/RpcCodes.cs` (codes, 64 KB, 10 s / 60 s, `RpcException`); server: new
  `Server/Managers/RpcDispatcher.cs` (registry with global names, size → rate (token bucket 30/s per player) → allow checks, C# handlers
  on the resource's script thread via `ScriptingEngine.Enqueue`, `Task` results awaited, TypeScript handlers through
  `RuntimeBridge.EventWithResult` ("rpcRequest" event, answer `{ok, value | code, message}`), `callClient` with a per-tick timeout scan,
  cleanup on disconnect; `RpcJson` converts script values ⇄ JSON), `Server/API.cs` (`registerRpc(name, handler[, allow])`,
  `registerRpc(name)` for the runtime, `unregisterRpc`, `callClient(player, name, args, timeoutMs): Task<object>`),
  `Server/ProcessMessages.cs`, `Server/GameServer.cs` (`Rpc`, tick, disconnect), `Server/Resources.cs` (unregister on stop),
  `Server/Runtime/RuntimeBridge.cs` (`EventWithResult`, pending callbacks with deadlines, Task results of API calls finished later),
  `Server/Runtime/ApiDispatcher.cs` (Task passthrough, JToken → plain), `runtime/gtan/index.ts` (`gtan.rpc.register/unregister/callClient`,
  `RpcError`, the `rpcRequest` answer); client: new `Client/Javascript/RpcContext.cs` (`API.rpc` + `RpcRouter`; promises and the handler
  table in a JavaScript helper evaluated in the script's engine, timeouts on the script tick, cleanup on script stop),
  `Client/Javascript/JavascriptHook.cs`, `Client/Main/Network/{ProcessMessages,MainNetwork}.cs`, `Client/GUI/CEFManager.cs`
  (`rpc` host message → `BrowserJavascriptCallback.Rpc` → the owning script's `API.rpc`, answer evaluated in the page),
  `Shared/Cef/CefHostProtocol.cs` (`rpc`, fields `Rpc`, `Timeout`), `Subprocess/GTANetwork.CefHost/Program.cs` (`gtan.rpc.call` in the
  shim, the `rpc` page message); resources: `freeroam` (`freeroam:ping`, `freeroam:secret` behind `hasEntityData(auth:account)`), `tsdemo`
  (`tsdemo:echo`), `auth` (`auth:login` / `auth:register` handlers returning `{ok, message}`, `ui/app.js` calls them over `gtan.rpc.call`
  and shows the reason; the events and chat commands still work); bot `--rpc name json`, `--rpc-burst name n`, results in the `--expect`
  text; `eng/integration-test.sh` (round trip with echo, denied, unknown, rate; the TypeScript handler), `eng/integration-test-auth.sh`
  (wrong password → `{ok:false, message}`, right one → logged in, then `freeroam:secret` allowed); typings: `Tools/GTANetwork.TypeGen`
  `Overrides` (hand-written `RpcContext` with `Promise<T>`), `types/cef.d.ts` (`gtan.rpc`), regenerated `types/*.d.ts`,
  `api-catalogue.json`, `runtime/gtan/api.generated.d.ts`; `samples/ts-resource` uses `API.rpc`, `API.registerRpc`, `API.callClient`,
  `gtan.rpc.call`; docs: `CHANGELOG.md`, `docs/CODEMAP.md` §8/§9, `docs/PLAN.md` E-05, `docs/DECISIONS.md` D-13, `types/README.md`.
* **Verified**: `docker compose run --rm dev eng/dev-test.sh` → `All local checks passed.`: the bot's `rpc freeroam:ping ok {"t":…,"echo":{"n":1},"player":"CIBot"}`,
  `rpc freeroam:secret error denied`, `rpc tsdemo:echo ok {"from":"bun",…}` (a handler in Bun), `rpc no:such error unknown`,
  `rpc freeroam:ping error rate` for a burst of 40; in the auth test `rpc auth:login ok {"ok":false,"message":"Wrong name or password."}`
  then `{"ok":true,"message":"Logged in."}` and `freeroam:secret` allowed after the login; `bun run check` of the sample passes.
* **Owner check**: log in through the CEF form with a wrong password — the form shows "Wrong name or password."; with the right one it
  closes. `Runtime.log` should stay free of `RPC` errors.
* **Not done**: encryption/authentication of the session and generated payload validators (T-009); client → page RPC (pages are
  called with `Browser.call/eval` as before); `rpc.callClient` from C# to a bot (the bot answers with the arguments, used by nothing yet);
  the rate limit and size limit are constants (settings later if a gamemode needs more).

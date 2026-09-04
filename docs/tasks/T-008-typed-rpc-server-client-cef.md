# T-008 — Typed RPC: server ⇄ client ⇄ CEF with request ids, timeouts, permissions, rate limits

Status: ready
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

- [ ] Bot RPC round trip in `eng/integration-test.sh`; denied call returns `denied`; 31st call within a second returns `rate`.
- [ ] Owner check: the `auth` login form uses `gtan.rpc.call("auth:login", …)` and shows the server's error text on a wrong password.
- [ ] `types/rpc.d.ts` compiles in the sample resource.

## Test plan

`docker compose run --rm dev eng/dev-test.sh`; owner run with the `auth` page.

## Risks and notes

Payload as JSON inside protobuf is a compromise (MessagePack later if size matters); size limit 64 KB per request.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

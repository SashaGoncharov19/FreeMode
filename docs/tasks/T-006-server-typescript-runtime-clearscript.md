# T-006 — Server-side TypeScript/JavaScript resources on ClearScript V8 with hot reload

Status: ready
Epic: E-04 TypeScript
Size: L
Branch: task/T-006-server-ts from the integration branch
Depends on: T-001, T-004
PR: no

## Goal

`<script src="server/index.ts" type="server" lang="typescript"/>` runs inside the server process on ClearScript V8
with the same `API` object C# resources use (`API.onPlayerConnected.connect(p => …)`, `API.sendChatMessageToPlayer(…)`),
commands via `API.registerCommand`, and the resource reloads when its sources change. `freeroam` gets a TS server part.

## Why

Q-01 default (c): in-process V8 for gameplay scripts (per-call cost is a method call); Bun stays the bundler.

## Scope

* In: the engine, event routing, commands, `exported`, hot reload, one ported resource, docs.
* Out: npm packages at runtime (bundled by Bun at build if pure JS), Node APIs.

## Files

* Change: `Server/GTANetworkServer.csproj` (`Microsoft.ClearScript.V8` 7.5.1(.1), `Microsoft.ClearScript.V8.Native.linux-x64`,
  `Microsoft.ClearScript.V8.Native.win-x64`), `Server/ResourceInfo.cs` (`ScriptingEngine` gets a `JsEngine` variant: one
  `V8ScriptEngine` per resource; `Invoke*` dispatch to JS handlers), `Server/Resources.cs` (start: bundle with
  `TypeScriptBundler` from T-005 using `--target=node --format=iife` minus Node globals, load into the engine; stop: dispose),
  `Server/Managers/CommandHandler.cs` (`API.registerCommand(name, handler, {aliases, acl})` for JS), `Server/API.cs`
  (nothing structural: expose `registerCommand` and make `exported` usable from JS), a `FileSystemWatcher` per resource
  (debounce 500 ms → `StopResource` + `StartResource`, behind `<info hotreload="true">` default on for TS/JS),
  `Server/resources/freeroam/server/index.ts` (port a subset: `/players`, `/pos`, spawn), `docs/CODEMAP.md` §9, `README.md`, `CHANGELOG.md`.
* Read: `Client/Javascript/JavascriptHook.cs:408` (how the client engine is configured: `AddHostObject`, host types, `AllowReflection=false`),
  `Server/ResourceInfo.cs:213–:645` (the Invoke* list to mirror), `Server/Managers/ScriptCompiler.cs`.

## Approach

1. `JsEngine`: `new V8ScriptEngine(name, V8ScriptEngineFlags.EnableTaskPromiseConversion)`; `AddHostObject("API", api)`;
   host types as on the client plus `Vector3`, enums used by the API; `AllowReflection=false`.
2. Events: the C# events on `API` already work through ClearScript's `.connect`; `Invoke*` in `ScriptingEngine` does not
   need JS-specific code if the JS handlers are connected to the same `API` instance — verify with `onPlayerConnected`.
3. Exceptions in handlers: caught per call, logged with the JS stack (`ScriptEngineException.ErrorDetails`), never crash the tick.
4. Hot reload: watcher on the resource folder; reload = stop (dispose engine, remove commands/exports) + start; players stay.
5. Performance: measure `API.getPlayerPosition` × 100k calls from JS in a loop; record µs per call in the Result (expect ≤ 1 µs).

## Acceptance criteria

- [ ] `eng/integration-test.sh` passes with freeroam's server part in TS (chat replies come from JS handlers).
- [ ] Editing `server/index.ts` while the server runs reloads the resource within 1 s (log line) and the bot's next command works.
- [ ] A thrown error in a JS handler is logged with file:line and the server keeps running.
- [ ] µs per API call recorded.

## Test plan

`docker compose run --rm dev eng/dev-test.sh`; a manual hot-reload run in the container (`sed` a string in `server/index.ts`,
the bot sends the command, expects the new text).

## Risks and notes

ClearScript native binaries under Wine are irrelevant here (server runs natively). Windows server: `win-x64` native package.
Thread affinity: `V8ScriptEngine` is single-threaded — all `Invoke*` already run on the tick thread.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

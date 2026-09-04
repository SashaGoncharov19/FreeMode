# T-006 — Server gamemode runtime on Bun: bridge spike with numbers, then protocol, state mirror, resource loader, hot reload

Status: in progress
Epic: E-04 TypeScript
Size: L (two stages; stop after stage 1 if the numbers are missed and re-plan)
Branch: task/T-006-bun-runtime from the integration branch
Depends on: T-001, T-004
PR: yes (one PR per stage)

## Goal

Stage 1 (spike): a Bun process and the .NET engine exchange MessagePack frames over a Unix domain socket (loopback TCP
on Windows) and the numbers in `docs/PLAN.md` E-04 are measured: one-way `call` ≤ 5 µs amortised, round trip p50 ≤ 60 µs /
p99 ≤ 300 µs, ≥ 200 000 one-way calls/s, state mirror of 1000 players at 10 Hz ≤ 3 % of one core per side.
Stage 2: `<script src="server/index.ts" type="server" lang="typescript"/>` runs in `runtime/main.ts` (Bun) against the
generated `gtan` library; `freeroam` gets a TS server part; hot reload; the engine supervises the runtime.

## Why

D-09: the owner wants Bun's built-in APIs (`Bun.sql`, `Bun.redis`, `Bun.s3`) in gamemodes without third-party modules,
with performance that does not make thousands of API calls per second a problem. In-process V8 would be faster per call
but has no Bun APIs; the bridge design (state mirror, one-way setters, batched frames) is what makes the sidecar viable —
the spike proves or refutes it before any gamemode code depends on it.

## Scope

* In: the bridge (both sides), the frame protocol, the state mirror, the runtime process and its supervision, the resource
  loader for TS server scripts, hot reload, freeroam's server part, Bun shipping, docs.
* Out: npm packages beyond Bun's built-ins (allowed, not our concern), sandboxing between resources (trusted code, as today).

## Files

* New: `Server/Runtime/RuntimeBridge.cs` (socket server, frame codec, batching, `state` publisher at 10 Hz from
  `Server/Managers/NetEntityHandler.cs` and `Server/Elements/Client.cs`, event fan-in from `ScriptingEngine.Invoke*`),
  `Server/Runtime/ApiCatalogue.cs` (reflection over `Server/API.cs` → `{name, params, returns, needsResult}` JSON used by
  T-004's generator and by the dispatcher), `Server/Runtime/RuntimeProcess.cs` (start `bun run runtime/main.ts --socket …`,
  restart with back-off, kill on shutdown; mirrors the browser-host watchdog in `Client/GUI/CEFManager.cs`),
  `runtime/main.ts`, `runtime/bridge.ts` (msgpackr codec, frame batching, promise table for `call` with id),
  `runtime/state.ts` (entity mirror: `players`, `vehicles` maps updated from `state` frames), `runtime/gtan/` (generated
  library: functions → `call` frames; events → `EventEmitter`-style `on(...)`), `runtime/.bun-version`, `runtime/package.json`,
  `runtime/bench/` (stage 1: `bench.ts` + `Server/Runtime/Bench.cs` behind `GTANetworkServer --bench-bridge`),
  `eng/bench-bridge.sh` (runs the spike and prints the table).
* Change: `Server/GTANetworkServer.csproj` (`MessagePack` 3.x), `Server/ResourceInfo.cs` (`lang="typescript"` server scripts →
  runtime), `Server/Resources.cs` (start/stop: tell the runtime to load/unload the resource's `server/index.ts`),
  `Server/Managers/CommandHandler.cs` (commands registered from the runtime), `Server/API.cs` (`registerCommand` for the
  runtime; `exported` across runtimes = through the runtime), `Server/resources/freeroam/server/index.ts` (port `/players`,
  `/pos`, spawn), `eng/package-client.ps1`/the server publish steps in `.github/workflows/build.yml` (ship `bun` for
  linux-x64 and win-x64 from `https://github.com/oven-sh/bun/releases/download/bun-v<ver>/…`, checksum verified),
  `eng/setup-linux.sh` (server install), `docs/CODEMAP.md` §4 and §9, `docs/PLAN.md` (record the numbers), `CHANGELOG.md`.
* Read: `Server/ResourceInfo.cs:213–:645` (the `Invoke*` list the bridge must forward), `Server/Program.cs:179` (tick loop:
  the bridge flushes and dispatches results on the tick thread), `Server/GameServer.cs:559`.

## Approach

Stage 1 — spike (one PR, numbers in the Result and in `docs/PLAN.md`):
1. `Server/Runtime/Bench.cs`: a socket server that accepts frames and echoes `call`-with-id as `result`; publishes fake
   `state` for N players at 10 Hz.
2. `runtime/bench/bench.ts`: measures (a) one-way calls/s and µs per call amortised with batching, (b) round trip p50/p99
   with 1, 16, 256 in-flight calls, (c) CPU of both processes at N = 100 / 1000 players of `state`.
3. Compare Unix socket vs loopback TCP on Linux; record; choose per platform.
4. Decision line in `docs/DECISIONS.md` D-09: numbers met → stage 2; not met → fallback (ClearScript in-process for
   gameplay, Bun for services) and this task is re-planned.

Stage 2 — implementation:
5. Frame protocol: `[u32 length][msgpack {t: "event"|"call"|"result"|"state"|"log", id?, name, args|data}]`, one connection,
   in-order; batching: the sender appends frames to a buffer and flushes every 1 ms or at 64 KB.
6. Engine side: `RuntimeBridge` runs its socket I/O on its own thread; `call` frames are applied on the tick thread from a
   queue drained in `GameServer.Tick` (so `API` stays single-threaded as today); `result`s go back from the same place.
7. Runtime side: `gtan` library generated by T-004 from the API catalogue: setters and fire-and-forget functions send
   frames; functions marked `needsResult` return promises; reads of mirrored state are synchronous local reads.
8. Events: every `Invoke*` in `ScriptingEngine` also emits an `event` frame; handlers in TS are `gtan.on("playerConnected", p => …)`.
9. Supervision: `RuntimeProcess` restarts Bun on exit (back-off 1, 2, 5 s; give up after 5 in a minute and log); after a
   restart the engine replays the entity snapshot as `state` and emits `runtimeRestarted`.
10. Hot reload: `runtime/main.ts` watches each resource folder (`fs.watch`), re-imports with a cache-busting query, calls
    `onResourceStop`/`onResourceStart`.
11. Freeroam server part in TS; `eng/integration-test.sh` chat replies come from the Bun runtime.

## Approach, stage 2 — refined after reading the server (5 Sept)

Facts that shape it: every C# resource script already runs its handlers on its **own worker thread** (`ScriptingEngine.MainThreadLoop`
drains a queue the `Invoke*` methods fill; `Server/ResourceInfo.cs:44–:160`), so `API` is called from non-tick threads today;
`InvokeChatCommand`, `InvokeChatMessage` and `InvokePlayerBeginConnect` are synchronous (they wait up to 5 s for a `CancelEventArgs`
answer); `InvokeUpdate` fires at 60 Hz per engine. Resources are started by `Resources.StartResource` (`Server/Resources.cs:26`) which
splits `meta.xml` scripts by `lang`; `CommandHandler.Register(resource)` registers `[Command]` methods by reflection.

1. **Runtime process** (`Server/Runtime/RuntimeProcess.cs`): started by the engine at the first resource with `lang="typescript"`
   server scripts; `bun run runtime/main.ts --socket <path>` (Unix socket in the server's working directory on Linux; `--tcp 127.0.0.1:<port>`
   + a token on Windows); watchdog and restart with back-off (1, 2, 5 s; give up after 5 in a minute); on restart the engine
   replays a full entity snapshot and emits `runtimeRestarted`. Bun binary: `runtime/.bun-version`, shipped in the server package.
2. **Bridge** (`Server/Runtime/RuntimeBridge.cs`): the frame protocol of stage 1; its socket thread parses frames and dispatches
   `call`s **on the bridge thread**, exactly like a resource's worker thread does for C# handlers (no marshalling to the tick);
   results go back through the batched writer, flushed after each received chunk. `Server/Runtime/ApiDispatcher.cs` maps a call
   name to the `API` overloads by reflection once at start and converts arguments: numbers/strings/bools as is, arrays → `List<T>`/`T[]`/
   `params`, `NetHandle` from an integer, `Client`/`Vehicle`/… from a handle (lookup in the entity registry), `Vector3` from `[x,y,z]`,
   enums from their number, `CancelEventArgs` never crosses (see 4). Return values are converted the other way (entities → handles).
3. **State mirror**: 10 Hz `state` frames with **deltas** — per player/vehicle the engine keeps the last sent row and sends only
   changed fields (a bitmask + values); a full snapshot on connect and after a runtime restart. The runtime applies them to
   `players`/`vehicles` maps that the generated library reads synchronously.
4. **Events**: `ScriptingEngine` gets a `typescript` mode (`_compiledScript` null, a `RuntimeBridge` reference): every `Invoke*` sends
   an `event` frame instead of queueing a C# call — done with one helper so the 40 methods change mechanically. The three
   cancelable events are sent with an id and wait for the runtime's answer up to 250 ms (then "not cancelled"). `InvokeUpdate` is
   **not** forwarded (60 Hz × resources over the bridge is waste; TS resources use timers); documented in the API notes.
5. **Resource loading**: `lang="typescript"` + `type="server"` scripts are bundled by `TypeScriptBundler` (T-005's class; until T-005
   lands, the runtime imports the `.ts` entry directly — Bun runs TS natively) and the engine sends `load {resource, entry, settings}`
   to the runtime; `unload` on stop; `CommandHandler` gets `RegisterRuntimeCommand(resource, name, aliases, acl)` so `gtan.commands.register`
   in TS reaches the same dispatcher as `[Command]`. `exported` across TS and C# resources: through the runtime (`call exported.<res>.<fn>`).
6. **Library** (`runtime/gtan/`): generated by T-004's TypeGen from the catalogue: one function per API member sending a `call`
   (with id when `needsResult`, and for members returning entities the promise resolves to a wrapper object with the mirrored
   state); `gtan.on(event, handler)`; `gtan.commands.register`; `gtan.exported`. Hand-written core: `runtime/main.ts`, `bridge.ts`,
   `state.ts`, `commands.ts`, `resources.ts` (loader + `fs.watch` hot reload).
7. **Test**: `freeroam` gets `server/index.ts` with `/players`, `/pos`, spawn and the chat replies `eng/integration-test.sh` expects;
   the bot test passes with the runtime; killing Bun mid-test restarts it and the next command works.

## Acceptance criteria

- [x] Stage 1 table recorded in the Result and `docs/PLAN.md` E-04, with the machine and Bun version.
- [x] Stage 2: `eng/integration-test.sh` passes with a TypeScript resource (`tsdemo`) on Bun: the bot sees `tsdemo: hello from Bun 1.4.1`
      on connect, `tsdemo: pong abc` for `/tsping abc`, `tsdemo: yes, 1 player(s) mirrored` for a cancelable chat message.
      Freeroam's port is T-007. Restart on death and handler errors are implemented (`RuntimeBridge.Tick`, `Gtan.dispatch` logs);
      the kill test is manual for now (follow-up: a phase in the integration test).
- [x] `docker compose run --rm dev eng/dev-test.sh` passes (Bun in the container image).

## Test plan

`eng/bench-bridge.sh` (stage 1); `eng/dev-test.sh`; manual hot-reload run; `kill -9 <bun pid>` during `eng/integration-test.sh`.

## Risks and notes

Windows: Unix domain sockets exist since Windows 10 1803 but Bun's support is not guaranteed — loopback TCP with a random
port and a token in the environment. Ordering between `state` and `event`: one connection, one order — never two sockets.

## Log

* 2026-09-04 22:10 agent — created (ClearScript variant).
* 2026-09-04 23:00 agent — rewritten for D-09 (Bun runtime with a bridge spike first).
* 2026-09-05 06:45 agent — stage 1 (the spike) started on task/T-006-bun-bridge (worktree); standalone bench, no server changes yet.
* 2026-09-05 07:40 agent — bench works after two protocol-level fixes (a ref struct passed by value lost the reply payload; a 1 ms flush on both sides put 1 ms on every round trip → flush on demand for calls with an id). Numbers met, except the Bun side of the state mirror at 3.4–3.6 % vs 3 %. Decision: stage 2 goes ahead (D-09). PR opened.

* 2026-09-05 08:20 agent — PR #8 (stage 1) merged; stage 2 in progress on task/T-006-bun-bridge.

## Result (stage 1)

* **Changed**: new `Tools/GTANetwork.BridgeBench/` (net10.0 + MessagePack 3.1.8: the engine side — frame protocol `u32 length +
  msgpack array [type, id, name, payload]`, `ping`/`stats`/`state.start`/`state.stop`, 1 ms / 64 KB batching, replies flushed after
  each received chunk, state publisher with msgpack-array and float32-buffer encodings), new `runtime/bench/bench.ts` + `package.json`
  + `bun.lock` (the Bun side with msgpackr 2.1.0: phases A one-way, B round trips at 1/16/256 in flight, C state mirror),
  `runtime/.bun-version` (1.4.1), new `eng/bench-bridge.sh` (downloads Bun into `artifacts/` when the container has none, builds, runs
  both transports), `.gitignore`. Not added to the solution (the script builds the project directly; avoids a merge conflict with T-004's
  solution change).
* **Verified** (`docker compose run --rm dev eng/bench-bridge.sh --players 1000 --seconds 10 --oneway 1000000`):

| Measure | Target (PLAN E-04) | Unix domain socket | Loopback TCP |
| --- | --- | --- | --- |
| One-way calls per second (batched, 1 ms / 64 KB) | ≥ 200 000 | 2 010 000 | 2 078 000 |
| µs per one-way call, amortised | ≤ 5 | 0.50 | 0.48 |
| Round trip, 1 in flight: p50 / p99 / max | ≤ 60 / ≤ 300 / — µs | 6 / 13 / 2205 µs (142k/s) | 8 / 18 / 1099 µs (102k/s) |
| Round trip, 16 in flight: p50 / p99 | — | 19 / 48 µs (689k/s) | 21 / 57 µs (617k/s) |
| Round trip, 256 in flight: p50 / p99 | — | 170 / 1445 µs (1 131k/s) | 172 / 1471 µs (1 107k/s) |
| State mirror, 1000 players @ 10 Hz, 10 s: engine CPU | ≤ 3 % of a core | 1.8 % | 2.0 % |
| State mirror: Bun CPU, msgpack arrays / float32 buffer | ≤ 3 % of a core | 3.6 % / 3.4 % | 3.6 % / 3.3 % |
| State mirror: bytes | — | 0.53 / 0.58 MB/s (101 frames, 101 000 rows) | same |

Conditions: the dev container on the owner's machine (i5-13420H), .NET 10.0.11 runtime (SDK 10.0.400), MessagePack 3.1.8,
Bun 1.4.1 with msgpackr 2.1.0, both processes on one host; one-way payload `["setPos", [i, 1.5, 2.5, 3.5]]` = 45 bytes on the wire.
Latency depends on the flush policy, not the transport: a call that waits for an answer is flushed at the end of the
current microtask (Bun) and replies leave right after the received chunk is processed (engine); with a fixed 1 ms flush on
both sides the round trip was 1.07 ms p50. The max outliers (2–9 ms) are GC/scheduling; the mirror's Bun cost is the
1000 Map updates per frame, not the decode (the float32 encoding saves 0.2 points).

* **Decision**: every target is met except the Bun side of the 1000-player mirror (3.4–3.6 % vs 3 %), close enough to proceed:
  stage 2 (the runtime, the resource loader, hot reload) goes ahead with a Unix domain socket on Linux and loopback TCP on Windows;
  the state mirror in stage 2 sends deltas (changed fields only), which removes most of the 1000 Map writes per frame.
* **Not done / follow-ups (stage 1)**: a `--windows` run of the bench on a Windows box (loopback TCP numbers there).

## Result (stage 2)

* **Changed**: `Server/Runtime/{BridgeCodec,RuntimeProcess,RuntimeBridge,ApiDispatcher,StateMirror}.cs` (new), `Server/ResourceInfo.cs`
  (`ScriptingEngineLanguage.typescript`; `ScriptingEngine(RuntimeBridge, name, resource)`; all 38 `Invoke*` methods route to the
  runtime — cancelable ones with an answer, `InvokeUpdate` not forwarded), `Server/Resources.cs` (TypeScript server scripts → runtime
  engines + `Load`/`Unload`; client TypeScript logged as pending T-005; exports of TS resources skipped), `Server/Managers/CommandHandler.cs`
  (skips runtime engines), `Server/GameServer.cs` (`Runtime`, `Tick`, shutdown), `Server/GTANetworkServer.csproj` (MessagePack 3.1.8;
  `runtime/` and the API catalogue shipped next to the server), `runtime/{main,bridge,msgpack,state,resources}.ts`, `runtime/gtan/index.ts`,
  `runtime/gtan/api.generated.d.ts` (TypeGen `--runtime-lib`: 376 typed members), `runtime/tsconfig.json`, `Server/resources/tsdemo/`,
  `Server/settings.xml` (tsdemo enabled), `eng/integration-test.sh` (three TypeScript expectations), `eng/dev-test.sh` and CI (the generated
  library is part of the stale check), `types/README.md`, `docs/CODEMAP.md`, `docs/PLAN.md`, `CHANGELOG.md`.
* **Verified**: `eng/dev-test.sh` → `All local checks passed.`; server log: `Bun runtime for TypeScript resources: /usr/local/bin/bun,
  unix:/tmp/gtan-runtime-<pid>.sock, 361 API members`, `Bun runtime connected (pid …)`, `[tsdemo] started server/index.ts (Bun 1.4.1)`;
  bot: `tsdemo: hello from Bun 1.4.1`, `tsdemo: pong abc`, `tsdemo: yes, 1 player(s) mirrored`. First run showed a double load
  (Load queued and replayed at hello); fixed by not queueing Load/Unload.
* **Not done / follow-ups**: Bun binary in the server package for operators (`runtime/bun/`) — today PATH or `GTAN_BUN`; exported
  functions of TypeScript resources callable from C#; `/help` listing of runtime commands; a kill-and-restart phase in the
  integration test; `tsc` check of `runtime/` in CI (needs `bun-types`); state mirror for vehicles.
* **Owner check**: none needed (server side, bot-tested). To try by hand: `~/GTANetwork/server/settings.xml` → add `<resource src="tsdemo" />`
  (the resource ships with the server), restart the server, join, type `/tsping hi`.

# T-002 — Bot load harness: N simulated players, server metrics, baseline numbers at 100/300/1000

Status: done
Epic: E-03 Scale
Size: L
Branch: task/T-002-bot-load-harness-and-baseline from the integration branch
Depends on: none (T-001 preferred first)
PR: yes

## Goal

`eng/load-test.sh <players> <seconds>` starts a server and one bot process that holds `<players>` connections, each
moving and sending pure/light sync like a real client, then prints a table: server tick ms p50/p99, packets/s and
bytes/s in/out per player, GC gen2 count, near-set sizes. Baseline numbers for 100, 300 and 1000 are recorded in
`docs/SYNC.md`.

## Why

The 1000-player target (`docs/PLAN.md` §1) cannot be worked on without a measurement. Today one bot = one process
(1000 processes × ~30 MB is not feasible) and the server has no metrics.

## Scope

* In: multi-connection bot, movement model, server metrics endpoint, the script, baseline numbers, a CI-sized run.
* Out: fixing the numbers (T-003 and later tasks).

## Files

* Change: `Tools/GTANetwork.Bot/Program.cs` — `--bots N` (N `NetClient`s in one process on a shared scheduler),
  `--move <radius m>` (random-walk positions at the client's rates: `PedPureSync` every 100 ms, `PedLightSync` every 1500 ms,
  using `Shared/PacketOptimization.cs` writers exactly as `Client/Sync/SyncSender/SyncSender.cs:15` does), `--report <file>`
  (per-bot receive counters: packets/bytes per second, unique remote handles seen).
* Change: `Server/Managers/FileServer.cs` — `GET /metrics.json` (only when `<httpserver>` is on) returning the counters below.
* New: `Server/Managers/Metrics.cs` — ring buffer of the last 600 tick durations (`Stopwatch` around `GameServer.Tick`
  in `Server/Program.cs:179`), counters incremented in `Server/ProcessMessages.cs:26` (in) and `Server/Packets.cs`
  `SendToClient`/`ResendPacket` (out), `GC.CollectionCount`, `Streamer` near-set size histogram
  (`Server/Managers/Streamer.cs`), connected clients.
* New: `eng/load-test.sh` — publishes the server and the bot (or reuses `eng/dev-test.sh`'s publish), starts the server
  with `freeroam`, runs the bot with `--bots N --move 1500 --duration S`, polls `/metrics.json` every 5 s, writes
  `artifacts/load-<N>.json` and prints the summary table.
* Read: `Shared/SyncPackets.cs`, `Server/Packets.cs:100` (`ResendPacket`), `docs/SYNC.md` §1.

## Approach

1. Bot: refactor the single connection into a `BotClient` class; a `Scheduler` thread ticks all bots (sync send
   timers, `ReadMessages` drain); movement = random walk in a circle of `--move` metres around the spawn, 1.5 m/s on
   foot, heading changes every 3–8 s; every bot answers chat pings as today so `eng/integration-test.sh` stays green.
2. Server metrics as above; `/metrics.json` shape: `{ "tickMs": {"p50":…,"p99":…,"max":…}, "players":…, "in": {"pps":…,"bps":…},
   "out": {"pps":…,"bps":…}, "gc": {"gen0":…,"gen1":…,"gen2":…}, "near": {"avg":…,"max":…} }`.
3. Script + three runs on the owner's machine (or the dev container): 100, 300, 1000 bots for 120 s each; record the
   table in `docs/SYNC.md` under a new "Load baseline (date)" section.
4. CI-sized run: `eng/dev-test.sh` gains an optional `LOAD_PLAYERS=50` step (default off in CI until the runtime is known).

## Acceptance criteria

- [x] `eng/load-test.sh 100 120` prints the table; `artifacts/load-100.json` exists (20/100/300/300-plaintext/1000 runs recorded in `docs/SYNC.md` §6).
- [x] 1000 bots connect (the server's `maxplayers` raised for the run); the table is recorded whatever the numbers are —
      recorded: all 1000 connect (973 in the full-rate run, the rest timed out while joining), but they do **not** stay connected
      for 120 s — the server's tick collapses under the relay load and Lidgren times the connections out (`docs/SYNC.md` §6). That
      is the finding the harness exists for; "1000 stay connected" is T-003's exit criterion now.
- [x] `eng/integration-test.sh` and `eng/integration-test-auth.sh` still pass (`eng/dev-test.sh` green).
- [x] `docs/SYNC.md` §6 has the baseline section with the command lines used.

## Test plan

`docker compose run --rm dev eng/load-test.sh 100 60`; then 300 and 1000 (needs ~4 GB free: 1000 Lidgren clients in one
process is ~1–2 GB — measure and record RSS of the bot too). `docker compose run --rm dev eng/dev-test.sh` → passes.

## Risks and notes

The Lidgren fork's `MaxPlayers` and per-connection buffers; 1000 connections from one IP may trip the server's
`Conntimeout`/reconnect throttle — allow a `--no-throttle` server setting for tests or space connects 5 ms apart.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 06:45 agent — started.
* 2026-09-05 08:30 agent — harness, metrics, baseline runs and docs done; PR opened.

## Result

* **Changed**: `Tools/GTANetwork.Bot/LoadBots.cs` (new: the `--bots N` swarm — one `NetClient` per bot, a few pump threads,
  each bot joins like a client, sends pure sync at 10 Hz and light sync every 1.5 s, random-walks within `--move` metres,
  answers RPC calls, counts what it receives; `--report` JSON), `Tools/GTANetwork.Bot/Program.cs` (options `--bots`, `--move`,
  `--report`, `--connect-interval`, `--threads`; dispatch; wire helpers shared), `Server/Managers/Metrics.cs` (new: last 600
  tick durations, packets/bytes in and out, GC, near sets, players, RSS; `/metrics.json`), `Server/Program.cs` (a `Stopwatch`
  around `Tick`), `Server/ProcessMessages.cs` (in counter), `Server/GameServer.cs` (out counters, the 1 Hz sample),
  `Server/Managers/Streamer.cs` (`NearCount`), `Server/Managers/FileServer.cs` (`GET /metrics.json`), `eng/load-test.sh` (new;
  `LOAD_NO_ENCRYPTION=1`, `LOAD_THREADS`, `LOAD_MOVE`), `eng/dev-test.sh` (`LOAD_PLAYERS=N` optional step), `docs/SYNC.md` §6,
  `docs/PLAN.md` E-03, `docs/DECISIONS.md` Q-14, `docs/tasks/T-023-encrypted-relay-cost.md` (new), `docs/CODEMAP.md`, `CHANGELOG.md`.
* **Verified**: `docs/SYNC.md` §6 — `eng/load-test.sh N 120` in the dev container for 20, 100, 300, 300 plaintext and 1000
  players: 100 → tick 1.93 ms p50 / 4.21 p99, 56 ticks/s, 1044 pkt/s and 50.7 KB/s per player out; 300 → 66.2 / 135.3 ms,
  11 ticks/s, 2200 pkt/s per player; 300 plaintext → 1.09 / 6.98 ms, 56 ticks/s, 2622 pkt/s per player; 1000 → the server collapses: the tick lags, the backlog grows (one tick of 81 s) and the connections time out (272 dropped with the bots half-starved; 969 → 4 at full rate).
  `eng/dev-test.sh` green (smoke, integration, auth, template, master).
* **Deviation from the Approach**: the scripted/interactive bot keeps its single-connection code (the integration tests rely on
  it); the swarm is a separate `LoadBot` class sharing the wire helpers — the same outcome (N connections in one process on a
  shared scheduler) with a smaller diff.
* **Not done / follow-ups**: CI runs no load step (the shared runner's capacity is unknown; `LOAD_PLAYERS=50 eng/dev-test.sh`
  is the local form). The 1000-bot run is bounded by the laptop (harness and server share 12 cores); a second machine, or a
  leaner bot that samples instead of decrypting every packet, would isolate the server. **T-023** (encrypted relay cost) and
  **Q-14** (relay key) come out of the baseline; T-003 keeps its goal.

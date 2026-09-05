# T-002 — Bot load harness: N simulated players, server metrics, baseline numbers at 100/300/1000

Status: in progress
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

- [ ] `eng/load-test.sh 100 60` prints the table; `artifacts/load-100.json` exists.
- [ ] 1000 bots connect (the server's `maxplayers` raised for the run) and stay connected for 120 s; the table is recorded
      whatever the numbers are.
- [ ] `eng/integration-test.sh` and `eng/integration-test-auth.sh` still pass.
- [ ] `docs/SYNC.md` has the baseline section with the command lines used.

## Test plan

`docker compose run --rm dev eng/load-test.sh 100 60`; then 300 and 1000 (needs ~4 GB free: 1000 Lidgren clients in one
process is ~1–2 GB — measure and record RSS of the bot too). `docker compose run --rm dev eng/dev-test.sh` → passes.

## Risks and notes

The Lidgren fork's `MaxPlayers` and per-connection buffers; 1000 connections from one IP may trip the server's
`Conntimeout`/reconnect throttle — allow a `--no-throttle` server setting for tests or space connects 5 ms apart.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 06:45 agent — started.

## Result

(empty)

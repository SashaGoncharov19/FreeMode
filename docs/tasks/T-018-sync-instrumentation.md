# T-018 — Sync instrumentation: per-entity error overlay, packet-age stats, bot route replay

Status: needs owner (implemented; the overlay and the baseline at 0 / 150 ms RTT need the game)
Epic: E-11 Sync quality
Size: M
Branch: task/T-018-sync-metrics from the integration branch
Depends on: none
PR: yes

## Goal

In debug mode the client draws, per streamed player/vehicle, the distance between the rendered position and the last
received position, the age of the last packet and the packet rate, and logs a 10-second summary (`[SYNC] players N,
error p50/p95 m, age p95 ms, rate Hz`); the bot can replay a recorded route (`--route file.json`, positions with
timestamps) so the same movement can be compared before and after a sync change.

## Files

* Change: `Client/Sync/SyncPed.cs` (store last packet time/position; error computation in `Render`), `Client/Util/DebugInfo.cs`
  (drawing), `Client/Sync/Threads.cs:16` (summary every 10 s via `LogManager.VerboseLog`), `Tools/GTANetwork.Bot/Program.cs`
  (`--record file.json` while a human drives near the bot? — no: `--route` replays a JSON of `{t, x, y, z, heading}`; a route
  is produced by the client in debug mode: `Client/Sync/SyncSender/SyncSender.cs` writes `logs/route-<date>.json` when
  `GTAN_RECORD_ROUTE=1`), `docs/SYNC.md` (a "Measuring" section with the commands), `CHANGELOG.md`.

## Acceptance criteria

- [ ] Owner check: with a bot replaying a recorded route, the overlay shows the error numbers; `Runtime.log` has `[SYNC]` lines.
- [ ] `docs/SYNC.md` records a baseline (error p50/p95 at 0 ms and at 150 ms RTT with `tc netem` on the server host).

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 18:40 agent — started.
* 2026-09-05 19:40 agent — overlay, summary, route recording and replay, autotest stay done; PR opened; the baseline needs the owner in game.

## Result

* **Changed**: `Client/Sync/SyncMetrics.cs` (new: `SyncPed.LastRenderError` / `PacketRateHz` / `RecordRenderError`, the
  `SyncMetrics` 10-second summary, `RouteRecorder`), `Client/Sync/SyncPed.cs` (`RecordRenderError()` after the position update
  in `Render`), `Client/Sync/Threads.cs` (the summary tick), `Client/Util/DebugInfo.cs` (the per-player block in debug mode,
  `SyncDebug`), `Client/Sync/SyncSender/SyncSender.cs` (route recording after each pure packet), `Client/Util/AutoTest.cs`
  (`GTAN_AUTOTEST_STAY`), `Tools/GTANetwork.Bot/Program.cs` (`--route <file>`: JSON lines `{t,x,y,z,h}`, interpolated by elapsed
  time, looping), `docs/SYNC.md` §7, `CHANGELOG.md`, `docs/CODEMAP.md`.
* **Deviation from the Files list**: the route is recorded by the client (`GTAN_RECORD_ROUTE=1`), as the task's note suggested, not
  by the bot; the summary goes through `LogManager.VerboseLog` (debug mode) as specified.
* **Verified**: the client builds against the real ScriptHookVDotNet build; `eng/dev-test.sh` green; the bot's `--route` is exercised only by hand (the replay moves the bot; nothing in CI asserts on it).
  Two headless runs on the owner's machine (`GTAN_AUTOTEST=127.0.0.1:4498 GTAN_AUTOTEST_STAY=60`, a private server, four bots walking at freeroam's spawn points): the autotest passed (`RESULT: OK`, script and page RPC) and the `[SYNC]` summary was written every 10 s for the 60 s stay — but with `players 0 (nobody streamed in with a ped)`: no bot became a streamed-in ped within the minute (the second run also reused a server left over from the first, so the setup is not trusted); the in-game numbers therefore stay the owner's check. The first run also showed that a loaded machine needs more than 30 s to start the browser for the page check, so the autotest now waits 60 s.
* **Owner check**: `GTAN_RECORD_ROUTE=1 ~/GTANetwork/play.sh --debug`, walk and drive for a minute, quit; then
  `~/GTANetwork/GTANetwork.Bot --route ~/GTANetwork/logs/route-<stamp>.jsonl --duration 300 --name Route` and play with debug mode
  on: the overlay lists "Route" with err / age / Hz, `grep "\[SYNC\]" ~/GTANetwork/logs/Runtime.log` shows the summaries. Record
  the p50/p95 at 0 ms, then with `sudo tc qdisc add dev lo root netem delay 75ms` (150 ms RTT), into `docs/SYNC.md` §7.
* **Not done / follow-ups**: the baseline numbers themselves (they need the game and root for `tc netem`); a vehicle route
  replays as a ped path (the bot has no vehicle sync).

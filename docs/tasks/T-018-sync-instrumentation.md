# T-018 — Sync instrumentation: per-entity error overlay, packet-age stats, bot route replay

Status: in progress
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

## Result

(empty)

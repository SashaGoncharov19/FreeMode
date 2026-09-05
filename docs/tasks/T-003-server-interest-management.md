# T-003 — Server-side interest management: grid cells, per-type ranges, tiered rates, per-player budget

Status: in progress
Epic: E-03 Scale
Size: L
Branch: task/T-003-interest-management from the integration branch
Depends on: T-002
PR: yes

## Goal

At 1000 moving bots (T-002 harness) the server tick stays ≤ 16 ms p99 and egress ≤ 30 KB/s per player, with every
player receiving updates for the entities within its ranges at 10 Hz (≤ 50 m), 3 Hz (≤ 200 m), 1 Hz (beyond, within
streaming range), nearest first, within a per-player byte budget.

## Why

Today `Server/Managers/Streamer.cs` recomputes near/far sets with an O(N²) distance loop at 1 Hz (`NearRange 2500 m`,
`MaxNear 250`) and `Server/Packets.cs:100` relays every pure-sync packet to all near clients at the sender's rate:
600 MB/s at 1000 players (`docs/PLAN.md` E-03).

## Scope

* In: spatial grid, per-entity-type ranges (already in `ServerSettings`: `PlayerStreamingRange`, `VehicleStreamingRange`,
  `GlobalStreamingRange`), dimension awareness, rate tiers, budget, metrics for all of it.
* Out: client-side changes to interpolation (E-11), transport replacement (Q-10).

## Files

* Change: `Server/Managers/Streamer.cs` (grid of 200 m cells keyed by `(x>>…, y>>…)`; near set = cells within range;
  recompute every 250 ms for moving players), `Server/Packets.cs` (`ResendPacket` :100/:123 consults the recipient's tier for
  the sender and skips packets above the tier's rate; `SendBasicSync` :87 for the far tier), `Server/Elements/Client.cs`
  (per-recipient last-sent timestamps per sender, budget counters), `Server/Managers/Metrics.cs` (tier counts, dropped-by-budget),
  `Shared/ServerSettings.cs` (`<interest>` block: cell size, tier distances/rates, budget bytes/s; defaults as above).
* Read: `Server/ProcessMessages.cs:832` (`PedPureSync`), `Client/Streamer/StreamerThread.cs` (client budgets: 250 players,
  ranges 2000/1000/500 m — the server's tiers must cover the client's stream-in range), `docs/SYNC.md`.

## Approach

1. Grid + per-type ranges replacing the distance loop; keep the public shape (`GetNearClients()` etc.) so `Packets.cs` changes are local.
2. Tiers: rate limiting per (sender, recipient) with a small struct in a dictionary on the recipient's `Client`.
3. Budget: when a recipient's bytes in the current second exceed the budget, drop the farthest tier first; count drops.
4. Measure with T-002 at 300 and 1000; iterate on cell size and tier distances; record before/after in `docs/SYNC.md`.

## Acceptance criteria

- [ ] `eng/load-test.sh 1000 120`: tick p99 ≤ 16 ms, out bps per player ≤ 30 KB/s; numbers in `docs/SYNC.md`.
- [ ] Two real players (owner + one) still see each other move without visible steps at 10 m (no regression).
- [ ] `eng/dev-test.sh` passes.

## Test plan

`eng/load-test.sh 300 120` and `eng/load-test.sh 1000 120` before and after; owner check with two clients (steps in Result).

## Risks and notes

Vehicles with passengers: the vehicle's tier must be the minimum of its occupants' tiers. Dimension changes must move
the entity between grids in the same tick.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 10:20 agent — started (branched from the T-023 branch so the relay workers are in; the PR targets the integration branch after #22).

## Result

(empty)

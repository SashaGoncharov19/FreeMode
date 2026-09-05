# T-026 — Entity create/update/delete and unoccupied-vehicle sync under interest management

Status: ready
Epic: E-03 Scale
Size: M
Branch: task/T-026-entity-interest from the integration branch
Depends on: T-003
PR: yes

## Goal

Entity packets stop being broadcast to every connection: `CreateEntity`/`UpdateEntityProperties`/`DeleteEntity` for streamed
entities (vehicles, props, blips with a position, markers, pickups, labels) go to the players within the entity's dimension and
streaming range, with a catch-up when a player comes into range; unoccupied-vehicle sync uses the T-003 tiers by the vehicle's
position. The load harness with `/veh` spawns (`--say "/veh adder"` on N bots) shows the egress per player no longer grows with
the number of entities elsewhere on the map.

## Files

* Read: `Server/Packets.cs` (`UpdateEntityInfo`, `SendToAll` callers), `Server/Managers/NetEntityHandler.cs` (creates and updates),
  `Server/Managers/Streamer.cs` (the grid), `docs/SYNC.md` §3/§5 F.
* Change: `Server/Managers/NetEntityHandler.cs`, `Server/Packets.cs` (`ResendUnoccupiedPacket` on tiers), `Server/Managers/Streamer.cs`
  (an entity grid next to the player grid; "entered range" → send the create), `Server/Managers/Metrics.cs` (entity packets per player),
  `Tools/GTANetwork.Bot` (`--say` per bot for the load run), `docs/SYNC.md`, `CHANGELOG.md`.

## Acceptance criteria

- [ ] `eng/load-test.sh 300 120` with every bot spawning a vehicle: entity bytes per player ≤ 5 KB/s and independent of N; the
      bot integration tests pass (a player joining sees the vehicles near it — `create Vehicle` in Alice's log).
- [ ] Owner check: a vehicle spawned 3 km away appears when driving towards it.

## Log

* 2026-09-05 20:10 agent — created from T-003's follow-ups (SYNC §6: entity packets are still broadcast).

## Result

(empty)

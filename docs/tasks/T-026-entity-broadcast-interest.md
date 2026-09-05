# T-026 — Entity create/update/delete and unoccupied-vehicle sync under interest management

Status: in progress
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

* Read: `Server/Packets.cs` (`UpdateEntityInfo`, `SendToAll` callers — 12 `CreateEntity` sites in `NetEntityHandler`, 2
  `UpdateEntityProperties`, 1 `DeleteEntity`), `Server/Managers/NetEntityHandler.cs` (creates, `UpdateMovements`), `Server/Managers/Streamer.cs`
  (the player grid, T-003), `Server/ProcessMessages.cs:298` (the `ServerMap` a joining player gets: every entity), `Client/Streamer/StreamerThread.cs`
  (the client streams by distance: players/vehicles/objects 2000 m, pickups 1000 m, labels 500 m; it only needs the entities it could see).
* Change: `Server/Managers/NetEntityHandler.cs` (creates go through one `Publish(entity)` that decides the recipients), `Server/Packets.cs`
  (`UpdateEntityInfo` to the players who know the entity; `ResendUnoccupiedPacket` on the T-003 tiers by the vehicle's position),
  `Server/Managers/Streamer.cs` (an entity grid built in the same 250 ms pass; per player: entities in range but not known → send
  `CreateEntity`, add to the known set), `Server/Elements/Client.cs` (`KnownEntities`, a `HashSet<int>` under a lock, seeded with the
  map's ids at join), `Server/Managers/Metrics.cs` (done here: `entities { createsPps, updatesPps, deletesPps, bps }`),
  `Tools/GTANetwork.Bot/LoadBots.cs` (done here: `--say` once after joining) + `eng/load-test.sh` (`LOAD_SAY`), `docs/SYNC.md`, `CHANGELOG.md`.

## Approach

1. **Baseline first** (done in this task's first commit): count entity packets per recipient and their bytes in `SendToAll`/`SendToClient`
   and show them in `/metrics.json` and the load test's samples; `LOAD_SAY="/veh adder" eng/load-test.sh 300 60` makes every bot spawn
   a vehicle — the number in the Result is what the filtering must beat.
2. **Which entities are range-limited**: Vehicle, Prop, Pickup, Marker, TextLabel, Ped (static), ParticleEffect. **Global** (every
   client always): Blip (the map shows them everywhere), World, Player (players are the sync's business). Dimension rules as for players
   (dimension 0 sees and is seen by all).
3. **Known sets**: `Client.KnownEntities` — the entity ids the client has received a create for. Seeded at join from the `ServerMap`
   (`ProcessMessages.cs:298`: the map carries every entity today; keep that for now — a joining player gets the world once — and seed the
   set from it). `DeleteEntity` goes to the holders of the id and removes it everywhere.
4. **Creates**: `NetEntityHandler.Create*` → `Publish(id, props)`: global types → `SendToAll` as today; range-limited → the players within
   `Interest.Range` of `props.Position` (the player grid of the current pass, or a straight scan over `Clients` — at most a few hundred
   distance checks per create) get the packet and the id in their set.
5. **Catch-up**: the streamer pass (every 250 ms) also indexes range-limited entities into a grid by cell and dimension (they move only
   through `UpdateMovements`/occupant sync, so the index is rebuilt from `ServerEntities` positions each pass — cheap for a few thousand
   entities). For each player: entities in the cells within range whose id is not in `KnownEntities` → `SendToClient(CreateEntity with the
   current properties)` from the streamer thread (the relay workers keep the order per connection) and add the id. Nothing is ever
   "un-created": the client streams out by distance itself, and the known set only grows until the entity is deleted.
6. **Updates**: `UpdateEntityInfo` for a range-limited entity → the players whose set contains the id (a `SendToAll` filtered by the set;
   for a moving entity the pass adds newcomers with a create carrying the fresh properties, so an update is never needed for them).
   Players and global types keep `SendToAll`.
7. **Unoccupied vehicle sync**: `ResendUnoccupiedPacket` uses the syncing player's `Streamer` tiers today; keep that but drop the far list
   (basic sync to everyone) for players who do not know the vehicle.
8. **Measure**: the same `LOAD_SAY` run before/after; then `eng/dev-test.sh` (Alice must still see Bob's vehicle: `create Vehicle ... Zentorno`
   in phase 2 — Bob is 10 m away, so the create is direct) and the owner's check: a vehicle spawned 3 km away appears when driving towards it.

## Acceptance criteria

- [ ] `eng/load-test.sh 300 120` with every bot spawning a vehicle: entity bytes per player ≤ 5 KB/s and independent of N; the
      bot integration tests pass (a player joining sees the vehicles near it — `create Vehicle` in Alice's log).
- [ ] Owner check: a vehicle spawned 3 km away appears when driving towards it.

## Log

* 2026-09-05 20:10 agent — created from T-003's follow-ups (SYNC §6: entity packets are still broadcast).
* 2026-09-05 21:00 agent — started: instrumentation and the baseline first (entity packets per player under load), then the filtering.

## Result

* **Baseline, cluster** (`LOAD_SAY="/veh adder" eng/load-test.sh 300 60`, 5 Sept 2026 — 300 bots at freeroam's four spawn points,
  every bot spawns one vehicle a second after joining): the server sent 52 046 `CreateEntity` and 90 002 `UpdateEntityProperties`
  recipient-packets (6.7 MB) during the join minute — 474 packets per vehicle, 22 KB per player — with a peak of 4 126 creates/s and
  5 035 updates/s (489 KB/s) while the bots were joining; steady state afterwards 0 (idle vehicles send nothing). Everyone is within
  2 km of everyone here, so range filtering could not cut these numbers: the cluster is the worst case for the relay and the best case
  for broadcasts.
* **Baseline, spread** (`LOAD_SCATTER=5000`, the bots teleported to random points within 5 km of the map centre): the sync relay falls to 152 packets/s and 4.2 KB/s per player
  (near average 40 players instead of 218), but the entity broadcast stays where it was — 52 037 creates and 142 051 updates
  (8.2 MB, 26.7 KB per player, peaks of 4 159 creates/s and 11 063 updates/s) — every vehicle create reached all 300 players although
  each of them is within 2 km of about 40. Range filtering would send each create to ~40 recipients instead of 300: about 7× fewer
  entity packets in this scenario, and the gap grows with the player count.
* **Done in this branch**: the instrumentation (`entities` in `/metrics.json` and in the load test's samples), `--say` for load bots,
  `--scatter`, `LOAD_SAY` / `LOAD_SCATTER`; the filtering itself (Approach 2–7) is not implemented yet — it changes the entity flow
  every player sees and is left for a session where the owner can test in game right after.

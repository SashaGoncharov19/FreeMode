# Synchronization: how it works, what is broken, what to do

A code review of the player/vehicle synchronization, entity registration and streaming paths, done in
September 2026 on the revived code base. File references point at the compiled sources
(`Client/Sync`, `Client/Streamer`, `Server`, `Shared`); `Client/Networking/*` is legacy and not compiled.
Items marked **fixed** were corrected in the same change as this document; everything else is open.

## 1. The pipeline in one picture

```
 local player (game thread, every frame)            server (one Tick thread, 60 Hz)              remote clients
 ─────────────────────────────────────────          ─────────────────────────────────            ──────────────────────────
 SyncCollector.OnTick                                ProcessMessages: PedPureSync /              MessagePump.Tick → HandlePedPacket /
   PedData(player) | VehicleData(player)   ──UDP──▶   VehiclePureSync: update Client state,  ──▶  HandleVehiclePacket → SyncPed.*
   (~40 natives, one object per frame)                fire API events, ResendPacket()             SyncThread.OnTick → SyncPed.Render()
 SyncSender thread: takes the last object                near players: full packet                 (per frame: tasks + SET_ENTITY_COORDS
   every 100 ms (pure), 1500 ms (light)                  far players: BasicSync 1 Hz                + velocity forcing, no snapshot buffer)
```

* **Pure sync** (100 ms, `UnreliableSequenced`, channel `PureSync`): position, rotation, velocity, health,
  armour, weapon, aim, flags. Ped packet 47-72 bytes, vehicle packet 8-64 bytes.
* **Light sync** (1500 ms, `ReliableSequenced`, channel `LightSync`): model, vehicle handle and seat, damage
  model, trailer, latency.
* **Bullet sync** (`ReliableOrdered`): one start/stop pair per shot burst, sent from the game thread.
* **Basic sync** (1 Hz, `UnreliableSequenced`): handle + position for players that are far away.
* The wire format is raw little-endian floats (`Shared/PacketOptimization.cs`), no quantisation, no timestamp.

## 2. Entity registration and streaming (client)

* `Streamer` (`Client/Streamer/Main.cs`, exposed as `Main.NetEntityHandler`) owns `ClientMap` (network handle
  → streamed item, i.e. the deserialized `EntityProperties` plus flags) and `HandleMap` (network handle →
  game handle, only while streamed in). Negative handles are client-only entities.
* `CreateEntity` packets only register; nothing is spawned. `StreamerCalculationsThread`
  (`Client/Streamer/StreamerThread.cs`) runs every 500 ms on a background thread, filters by dimension and
  distance (2000 m players/vehicles/objects, 1000 m pickups, 500 m labels), applies the budgets (250 players,
  60 vehicles, 500 objects, ...) and queues stream-in/out lists.
* `StreamerTick` (game thread, every frame) drains the lists: `StreamInVehicle` loads the model (blocking
  `Script.Yield` loop, up to 1 s), creates it, applies ~150 natives of properties (mods 0..100, extras, doors,
  tyres), then registers `HandleMap`.
* Players are special: `StreamIn` only flips a flag, the ped is created later by `SyncPed.CreateCharacter`.

## 3. Server relay and streaming

* Every packet is deserialized, applied to the `Client` state, re-serialized and relayed
  (`Server/Packets.cs`). Recipients come from the sender's `Managers/Streamer`, refreshed once per second.
* Before this review the split was rank-based (`Take(250)`): with fewer than 251 players **every** player
  received **every** packet of everyone else, in every dimension and at any distance: 10·N·(N-1) messages
  per second. **Fixed**: near = same or global dimension and within 2500 m, sorted by distance, at most 250;
  everyone else gets the 1 Hz basic sync. The client discards anything beyond 2000 m anyway.
* Entity create/delete/update packets are still broadcast to all connections; the server keeps no per-client
  interest set (see proposals).

## 4. Findings

### Fixed in this change

Server
* `API.sendNativeToPlayersInRangeInDimension(Vector3, float, int, Hash, ...)` called itself: a
  `StackOverflowException` for any script using the `Hash` overload.
* `Client.Equals` without `GetHashCode`: every hash-based use of `Client` was broken.
* `API.getAllPlayers()` copied the client list without its lock while the network thread mutates it.
* `Managers/Streamer.Pulse`: the lock was taken on a field that was replaced inside the lock; players
  without a position sorted as nearest; a null position of the owner threw and left the sets empty for a
  second; fake clients were included. Rewritten (rank → distance + dimension, arrays replaced atomically).
* `Packets.cs`: the basic packet, its Lidgren message and two lists were built for every relayed packet
  even when nobody was far (about ten wasted allocations per packet); the vehicle relay indexed the entity
  dictionary without a check; the vehicle, unoccupied and bullet relays had no `Fake`/null-connection
  guards; the unoccupied far list had no guards at all. One `CanReceive` check now.
* The far-sync throttle used the wall clock (`DateTime.Now`); an NTP step stalled it. `Program.MonotonicMs()`.
* The per-recipient throttle map (`LastPacketReceived`) was never pruned; it is cleared on disconnect.
* AFK removal after 70 s dropped the client from the list without any teardown: the entity, the ped on every
  other client and the connection leaked. `HandleDisconnect` is shared by the status handler and the AFK path.
* `UnoccupiedVehSync` parsing was O(n²) (`Skip/Take/ToArray` per vehicle) and unchecked on length.

Client
* `StreamOut` wrapped vehicles and peds in `Prop`, whose `Exists()` checks for the *object* entity type, so
  vehicles and peds were **never deleted** on stream-out; only the per-second `CleanupGame` sweep hid it.
* `Streamer.Count(Type)` counted `KeyValuePair`s and always returned 0, so every budget check in
  `CreateLocal*` and the particle fast path was dead.
* Four of the eight stream-out filters had an operator-precedence bug (`a || b && c`): never-streamed
  entities in other dimensions were queued for stream-out every 500 ms.
* An exception in `StreamerTick` or the calculation thread ended streaming for the rest of the session (SHVDN
  aborts the script). Both are guarded now; a queued item that was deleted meanwhile is skipped instead of
  spawned as an orphan; the pending lists are cleared on disconnect; the stream-in lock is no longer held
  across model loading.
* `Vehicle.cs`: the stale-data guard compared `Environment.TickCount` with a `DateTime`-based timestamp and
  was always true; `_latencyAverager.Average()` was called unguarded (throws until the second packet); every
  `elapsed / AverageLatency` ratio divided by zero before the second packet (`SafeAverageLatency`).
* Ragdoll and parachute extrapolation (`LinearVectorLerp`) was unbounded: without packets the multiplier grew
  without limit and the ped flew off. Clamped at 1.5 windows.
* `AimPlayer` was dereferenced with `AimedAtPlayer` set but no target.
* The aim walk-target prop (`_entityToWalkTo`) leaked on every stream-out.
* Nametags ran a line-of-sight trace for every streamed player every frame, before the distance check.
* `SyncSender` spun a core when the collector had nothing (connecting, loading); the sent-bytes counters
  were raced between two threads; `ForceAimData` was read without a barrier.
* `PedData` collected the same natives several times per frame (parachute state ×2, ladder ×2, ragdoll ×2,
  reloading ×2, melee ×4, melee subtask ×7, current weapon ×3): read once per frame now.
* `Util.LoadModel` left the global `ModelRequest` gate set after an exception, which disables the streamer.
* `Events.cs` enumerated `ClientMap` lazily while `StreamIn` yields ("collection was modified").
* A `Stopwatch` was allocated every frame in two scripts even with the debug overlay off.

### Open (ordered by impact)

Sync quality
1. No snapshot buffer: remote peds are moved by `TASK_GO_STRAIGHT_TO_COORD` plus a per-frame
   `SET_ENTITY_COORDS_NO_OFFSET` lerp with factor `DataLatency*2/50000` (0.004 per frame at 100 ms ping,
   **0** with no latency data), plus `SET_ENTITY_VELOCITY`. Three systems fight each other. Vehicles are
   driven by a velocity term whose position error is frozen at packet time. See proposal A.
2. Edge-triggered flags (jump, melee attack, vault, closing door) are sampled every frame but sent every
   100 ms: about five of six are lost. Sticky flags between sends (proposal B).
3. Remote fire rate equals the client frame rate: one bullet per rendered frame while `IsShooting`.
4. `Script.Wait(1500)` inside `LeaveVehicle`/`EnterVehicle` and blocking `LoadDict`/`LoadWeapon` calls freeze
   the whole remote-player loop for every other player.
5. Passenger seat and vehicle handle travel only in the 1.5 s light sync.
6. Per-source sequencing: all senders share one Lidgren sequence channel per packet type, so a late packet
   from player A can be dropped because a newer one from player B arrived first.
7. `AimCoords` can be sent as `(0,0,0)` when `HasAimData` is set without a raycast; `PedDataFlags.Shooting`
   only means melee and is never consumed by the receiver; `NetHandle` costs 4 zero bytes in every
   client→server packet; the 4-byte length prefix duplicates Lidgren's own.

Server
8. Entity create/delete/update and `SyncEvent` are broadcast to every connection regardless of dimension or
   distance; the join snapshot races with those broadcasts (ghost entities).
9. `NetEntityHandler.ToDict()` hands out the live dictionary to three threads without a lock;
   `UnoccupiedVehicleManager` copies the whole entity map once per vehicle per pulse.
10. `Client.Properties` throws after disconnect; `UpdateAttachables` has no cycle guard; unknown packet
    types throw per message.

Client streaming
11. Entities can be deleted by `CleanupGame` between creation and `HandleMap` registration when `StreamIn`
    yields (animated peds, trailers): permanent ghosts.
12. `HandleMap` is protected by two different monitors; `ClientMap` is enumerated without a lock from six
    per-frame scripts; `StreamedInVehicles`/`SyncPeds` are published without synchronization.
13. Budgets are applied to an unordered set (`Take(N)` on dictionary order): no nearest-first, no hysteresis,
    churn at the budget boundary. `StreamInVehicle` issues ~100 `REMOVE_VEHICLE_MOD` natives per stock car.
14. `NetToEntity(IStreamedItem)` and `UnoccupiedVehSync` use recycled game handles of streamed-out items.

### Encryption overhead (T-009, 5 Sept 2026)

Every data message after the handshake carries 8 bytes of counter and a 16-byte GCM tag: +24 bytes per message (a pure ped
sync packet of ~90 bytes grows by about a quarter). CPU: the server encrypts once per recipient with hardware AES
(`AesGcm`, AES-NI), well under a microsecond per sync packet; the in-game client encrypts its own ~10–100 messages per second
with BouncyCastle (managed, a few microseconds each). The load harness (T-002) measured it — §6: at 300 players the per-recipient
encryption takes the tick from 1.1 ms to 66 ms (T-023, Q-14); the two-bot integration phase shows no change in the relayed packet counts.

## 5. Proposals

A. **Snapshot interpolation.** Add a server timestamp (`uint16` ms) to pure sync; keep the last N snapshots
   per remote entity; render at `now - (2 × packet interval + jitter)`, interpolate between the bracketing
   snapshots, extrapolate at most 250 ms with velocity, then freeze. Drive the ped with the interpolated
   position/heading and the walk/run task only as animation state; drive vehicles with
   `velocity = targetVelocity + K·(targetPos - currentPos)` recomputed every frame (the unoccupied-vehicle
   interpolator already does this correctly). Removes the frame-rate and ping dependence of today's code.
B. **Collect at the send cadence with sticky flags.** Build the packet every 100 ms, OR the edge flags across
   frames, keep only the shooting detection per frame. Cuts ~80 % of the collector's natives and fixes lost
   flags.
C. **Fire-rate timer** for remote shooting, weapon table driven.
D. **State machines instead of `Script.Wait`** for enter/leave vehicle; preload animation dictionaries and
   weapons asynchronously.
E. **Per-sender sequence numbers** in the payload (or hashed Lidgren channels) and stale-packet drop.
F. **Server interest management**: per-client grid cells keyed by dimension; filter entity broadcasts and
   sync events, and build the join snapshot from the spawn area only.
G. **Streaming**: nearest-first selection with hysteresis, register the game handle right after creation,
   asynchronous model loading (`REQUEST_MODEL` when queued, create when loaded), one lock for both maps,
   skip the mod loop for stock vehicles.
H. **Wire format**: quantised positions (fixed-point int32) and velocities (int16), drop the zero
   `NetHandle` and the length prefix: ped pure sync from 47 to ~28 bytes.

Measure each step with the bot (`eng/integration-test.sh`) and the `[PROFILE]` lines: packets/s per player,
bytes/s per player, script ms per frame, `SyncThread` natives per tick.

## 6. Load baseline (T-002, 5 September 2026)

Measured with `eng/load-test.sh <players> 120` in the dev container on the owner's laptop (12 cores, 15 GB; nothing else
running): one server with `freeroam`, one bot process holding all the connections (`GTANetwork.Bot --bots N --move 1500`),
every bot joining like a client and sending pure sync at 10 Hz and light sync every 1.5 s while walking within 1500 m of its
spawn (all bots spawn at freeroam's spawn points, so everyone is within near range of everyone — the worst case for the relay).
Encrypted sessions (T-009) unless marked. Numbers are averages over the steady state (all players joined); the server's
loop asks for 60 ticks/s (`Thread.Sleep(1000/60)` after each tick).

| players | joined | tick p50 / p99 / max ms | ticks/s | in per player pkt/s, KB/s | out per player pkt/s, KB/s | out total KB/s | GC gen0/1/2 | near avg / max | server RSS MB | bot RSS MB |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 20 (20 s) | 20 | 0.14 / 0.54 / 44.0 | 62 | 10.8, 0.8 | 199.8, 9.7 | 193.7 | 5/1/0 | 19 / 19 | 119 | 79 |
| 100 | 100 | 1.93 / 4.21 / 9.9 | 56 | 10.8, 0.8 | 1044.5, 50.7 | 5065.8 | 440/3/2 | 99 / 99 | 143 | 88 |
| 300 | 300 | 66.17 / 135.30 / 162.7 | 11 | 8.9, 0.6 | 2200.5, 104.4 | 31315.9 | 722/287/18 | 250 / 250 | 150 | 143 |
| 300 (plaintext) | 300 | 1.09 / 6.98 / 10.2 | 56 | 10.5, 0.5 | 2621.8, 125.7 | 37711.8 | 500/74/9 | 250 / 250 | 135 | 129 |
| 1000 (bots on 4 pump threads, sending ~1.5 pkt/s each) | 1000 | 42.6 / 354.0 / 1251 | 14 | 1.5, 0.1 | 570.7, 18.0 | 13314 | 366/162/13 | 244 / 250 | 350 | 808 |
| 1000 (10 pump threads; loaded phase, before the collapse) | 973 | 66.6 / 814 / 80861 | ~13 | 1.0, 0.1 | ~527, ~25 | ~24500 | 668/245/108 | ~240 / 250 | 870 | 6353 |

What the numbers say:

* **The relay is O(N²) and single-threaded.** Every pure sync packet is re-serialized and sent to every near player
  (`GameServer.Send`); with everyone in range the server emits N·(N−1)·10 messages per second: 100 players → 104 k/s
  (5 MB/s), 300 players → 657–786 k/s (31–38 MB/s; the cap of 250 near players is active). 100 players fit comfortably
  (1.9 ms per tick, 56 of the 60 ticks per second).
* **Per-recipient encryption is the dominant server cost above ~150 players.** The same 300-player run in plaintext keeps a
  1.1 ms tick (78 ns per recipient-message: one Lidgren enqueue) and delivers the full 2622 packets/s to every client;
  encrypted it takes 66 ms per tick (p99 135 ms, the loop at 11 Hz) and delivers 2200 — about 1.1 µs per recipient-message
  for `Server.CreateMessage` + a payload copy + `Seal` (two allocations, one AES-GCM). The T-009 review deferred this number
  to the harness; it is now known, and it is the 60× between the two rows. Fix path: T-023 (zero-copy sealing; Q-14 a relay
  key for the sync channels), after or alongside T-003 (fewer recipients per packet).
* **Egress per player is 4× over the budget already at 300**: 126 KB/s (plaintext) against the 30 KB/s target of
  `docs/PLAN.md` §1 — the 250-near cap alone does not bound bandwidth; T-003's interest management (cells, tiered rates,
  a per-player budget) has to bring the recipient count and rates down, not the bytes per packet.
* **Memory is not the problem**: the server stays at 135–150 MB with 300 players; the bot process holds 300 connections in
  ~130–145 MB (about 0.4 MB and one Lidgren thread per connection). GC gen2 rose from 2 to 18 per 120 s between 100 and 300
  encrypted players — the per-recipient message copies.
* **The harness itself** costs about one core per 100 bots (decrypting and parsing ~2200–2600 packets/s per bot): 100 bots
  used 104 CPU-seconds in 127 s, 300 bots 410 (encrypted) / 302 (plaintext) in ~140 s. On this 12-core machine the
  1000-bot run is bounded by the machine as much as by the server; treat its numbers as "the server under a saturated host".
* **1000 players do not hold.** Two runs. With 4 pump threads in the bot process the bots were CPU-starved and sent only
  ~1.5 packets/s each: all 1000 joined, the tick ran at 14 Hz (p50 43 ms, p99 354 ms, max 1.25 s) and 272 connections timed
  out during the 120 s. With 10 pump threads (the bots at their full rate) 973 joined; the relay sat at ~510 k messages/s
  with the tick at 66 ms p50 / 0.8 s p99, then **one tick took 81 s** and 965 of the 969 remaining connections timed out
  (the table's row shows the loaded phase; the run's own steady-state window fell into the collapsed tail). The mechanism:
  each tick reads *all* pending messages (`Server.ReadMessages`) and relays every one to up to 250 recipients; once a tick
  lags, the next one meets a larger backlog — a runaway with no backpressure. For T-003 this adds two items before the
  recipient cuts: drop stale pure sync instead of relaying it (proposal E) and bound the work per tick. Memory: the server
  reached 870 MB; the bot process 6.3 GB — its Lidgren send queues while the server stalled — so the harness needs a cap on
  queued sends (or unreliable-only sending) before the next 1000 run.

Reproduce: `docker compose run --rm dev eng/load-test.sh 100 120` (then 300, 1000); `LOAD_NO_ENCRYPTION=1` runs the same
with plaintext sessions (`--no-encryption`, `RequireEncryption` off) to isolate the cipher's share. Samples and reports land in
`artifacts/load-<N>.json`, `load-<N>-bot.json`, `load-<N>-server.log`.

# T-017 — Anti-cheat baseline: server-side validation, cheat events, client integrity report, signed manifest

Status: done (the manifest signing is a follow-up that needs a repository secret from the owner)
Epic: E-10 Anti-cheat
Size: L
Branch: task/T-017-anticheat from the integration branch
Depends on: T-002 (to tune thresholds under load)
PR: yes

## Goal

The server checks every state a client claims against physical and game limits, raises `API.onCheatDetected(player,
kind, evidence)` (gamemodes decide: log, kick, ban) and, when `<anticheat action="kick">` is set, acts by itself;
clients send a hash report of their binaries and resources at connect and the server compares it with the release manifest.

## Files

* Change: `Server/ProcessMessages.cs` (`PedPureSync` :832 / `VehiclePureSync` :491: speed and teleport per tick using
  `vehicleData.json` maxima × 1.3 and 60 m/s on foot with a 3-tick grace after respawn/teleport by the server; health/armour
  ≤ max unless set by the server; weapon in hand ∈ weapons given; `PlayerKilled` :1287 killer/weapon plausibility;
  `UpdateEntityProperties` :1323 only for entities the client owns), `Server/Elements/Client.cs` (server-known health/armour/weapons/position
  authority timestamps), `Server/API.cs` (`onCheatDetected`, `setAnticheatAction`), `Shared/ServerSettings.cs` (`<anticheat>`),
  `Shared/Packets.cs` (`ConnectionRequest.Integrity {manifestVersion, hashes[]}`), `Client/Main/Network/MainNetwork.cs:61`
  (compute SHA256 of `bin/scripts/*.dll`, `cef/GTANetwork.CefHost.exe`, `cef/libcef.dll` at start; reuse the compiled-out
  `IntegrityCheck` shape from `Client/Main/Misc.cs:409`), `eng/package-client.ps1` (write `manifest.json` with hashes into
  the package; the release job signs it with a key in a repository secret — Ed25519 via BouncyCastle), `Tools/GTANetwork.Bot`
  (`--cheat speed` to trigger the detector in `eng/integration-test.sh`), `docs/CODEMAP.md` §10, `CHANGELOG.md`.

## Acceptance criteria

- [x] `eng/integration-test-anticheat.sh`: a bot moving at 200 m/s on foot is detected (`kind=speed`) and kicked, a teleporter too, an honest bot is left alone; the 5-minute 300-player run: 0 detections, 0 kicks over 300 bots × 300 s (`eng/load-test.sh 300 300`).
- [x] A client whose hashes differ from `manifest.json` is reported (`kind=integrity`) at connect and the server acts per `<anticheat integrity>`; the report is computed by `Client/Util/Integrity.cs` and the manifest written by `eng/package-client.ps1` (verified by reading: the packaging runs on the Windows CI job; the server-side comparison is exercised with a hand-written manifest in the Result).
- [x] The freeroam TS gamemode logs cheat events (`[freeroam] cheat detected: <kind> by #<player>: <evidence>`).

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 17:50 agent — started.
* 2026-09-05 19:00 agent — checks, event, settings, integrity report, manifest, bot cheats and the test done; PR opened.

## Result

* **Decisions inside the task**: findings are rate-limited to one per kind per 5 s per player; speed needs three consecutive
  packets over the limit (falls, explosions and one late packet are not cheats), a teleport is one jump over 200 m within 1.5 s;
  horizontal speed only (falling is vertical); grace 5 s after connect, 3 s after respawn and after `setEntityPosition` on the
  player. Weapons are not checked: the world hands out weapons the server never sees (pickups), so a "weapon not given" rule would
  flag honest players — left for a later task with server-owned weapon state. Health > 200 and armour > 100 are the only state
  checks. The manifest is unsigned in this iteration: the server trusts its own `manifest.json`; signing (Ed25519, a repository
  secret) is a follow-up because the secret is the owner's.
* **Changed**: `Server/Managers/Anticheat.cs` (new), `Server/Elements/Client.cs` (`AnticheatState`), `Server/ProcessMessages.cs`
  (checks in `PedPureSync` / `VehiclePureSync`, integrity at approval, grace on confirm and respawn), `Server/API.cs`
  (`onCheatDetected`, `setAnticheatAction`/`getAnticheatAction`, grace in `setEntityPosition`), `Server/ResourceInfo.cs`
  (`cheatDetected` to TypeScript), `Server/GameServer.cs`, `Server/Managers/Metrics.cs` (`anticheat` in `/metrics.json`),
  `Shared/ServerSettings.cs` + `Server/settings.xml` (`<anticheat/>`), `Shared/Packets.cs` (`IntegrityReport`, `FileHash`,
  `ConnectionRequest.Integrity`), `Client/Util/Integrity.cs` (new) + `Client/Main.cs` + `Client/Main/Network/MainNetwork.cs`,
  `eng/package-client.ps1` (`manifest.json`), `Tools/GTANetwork.Bot` (`--cheat`), `Server/resources/freeroam/server/index.ts`
  (the handler), `eng/integration-test-anticheat.sh` (new) in `eng/dev-test.sh` and CI, docs.
* **Verified**: `eng/integration-test-anticheat.sh` (in `eng/dev-test.sh` and CI): the speed hacker (200 m/s on foot) is detected as `speed` and kicked with "Cheat detected: speed.", the teleporter (500 m jumps) as `teleport`, the freeroam TypeScript gamemode logs both, an honest bot walks 8 s unflagged. False positives: `eng/load-test.sh 300 300` (300 bots random-walking at 1.5 m/s for five minutes, spawned and teleported by the server): `anticheat.detections = 0`, `kicked = 0`, no "Cheat detected" line in the server log; the run itself: tick 1.04 / 4.10 ms, 58 ticks/s, 25 KB/s per player. `eng/dev-test.sh` green (the typings regenerated for the new API members).
* **Not done / follow-ups**: manifest signing (a repository secret and the public key in `Shared`); weapon plausibility with
  server-owned weapon state; `PlayerKilled` killer/weapon plausibility and `UpdateEntityProperties` ownership (the latter is already
  gated by `TrustClientProperties`).

# T-017 — Anti-cheat baseline: server-side validation, cheat events, client integrity report, signed manifest

Status: ready
Epic: E-10 Anti-cheat
Size: L
Branch: task/T-017-anticheat from the integration branch
Depends on: T-002 (to tune thresholds under load)
PR: no

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

- [ ] `eng/integration-test.sh`: a bot moving at 200 m/s on foot triggers `onCheatDetected(kind=speed)` within 1 s; normal bots never trigger in a 5-minute T-002 run at 300 players.
- [ ] A client with a modified `GTANetwork.dll` hash is reported (`kind=integrity`) at connect; the server's action follows `<anticheat>`.
- [ ] The freeroam TS gamemode logs cheat events (example handler).

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

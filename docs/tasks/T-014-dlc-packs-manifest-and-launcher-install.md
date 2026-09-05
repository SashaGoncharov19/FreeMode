# T-014 — Custom DLC packs: server manifest, launcher download and install-time overlay (design + first implementation)

Status: needs owner (manifest, download, protocol and refusal done; the update.rpf overlay waits for Q-15)
Epic: E-08 DLC packs
Size: L
Branch: task/T-014-dlc-packs from the integration branch
Depends on: T-010 (launcher core)
PR: yes

## Goal

A server lists DLC packs in `settings.xml` (`<dlcpack name url sha256 size/>`) and serves the list as `GET /dlcpacks.json`;
the launcher (GUI or CLI `prepare <server>`) downloads missing packs into `~/GTANetwork/dlcpacks/<name>/`, applies them for
the next game start (a `dlclist.xml` with the packs and a file overlay so the game folder is never modified permanently),
and restores after the game exits. The client sends the mounted pack set at connect; the server refuses players missing a
required pack with a message naming the packs. In-game download and restart-to-apply are T-022 (D-10).

## Files

* Change: `Shared/ServerSettings.cs` (`DlcPacks`), `Shared/Packets.cs` (`ConnectionRequest.DlcPacks`, `ConnectionResponse` reason),
  `Server/ProcessMessages.cs:117` (check), `Server/GameServer.cs` (announce the pack list to the master for the launcher, T-011),
  `Launcher.Core` (download with SHA256 verify, overlay apply/restore next to `Deployment.cs`), `Launcher.Gui` (a Packs page),
  `docs/CODEMAP.md`, `CHANGELOG.md`.
* Read: `Client/Main.cs:354` (`_ENABLE_MP_DLC_MAPS`), `Launcher/Deployment.cs` (how files are deployed/restored today).

## Design questions this task must answer first (write the answers into DECISIONS.md before coding)

1. How the overlay is applied without OpenIV.asi: (a) our own `fiDevice` redirect in the SHVDN C++/CLI shell
   (`Shv.NET/`, Windows CI job), (b) a `mods/`-folder redirect ASI we ship, (c) copy into `update/x64/dlcpacks/` and
   patch `dlclist.xml` inside `update.rpf` (modifies game files; rejected unless nothing else works).
2. Rockstar launcher/Steam integrity behaviour with (a)/(b) under Proton on the owner's machine.

## Acceptance criteria

- [ ] Owner check: a known add-on vehicle pack declared by the local server spawns in game after `prepare`; the game folder is
      byte-identical after exit (`Deployment.Restore` verified by hash). **Blocked on Q-15** (the apply step is not implemented).
- [x] A client without the pack is refused with the pack name in the message (`eng/integration-test-dlc.sh`).

## Log

* 2026-09-04 22:10 agent — created as draft.
* 2026-09-04 23:00 agent — ready: D-10 decided (download anywhere, apply at game start); the overlay question stays inside this task.
* 2026-09-05 13:00 agent — started (branched from the T-003 branch; the PR targets the integration branch after #23).
* 2026-09-05 14:30 agent — manifest, protocol, refusal, launcher download and the Packs page done and tested; Q-15 written; PR opened; `needs owner` for the overlay decision.

## Result

* **Design questions** (answered in `docs/DECISIONS.md` Q-15): the overlay without OpenIV.asi is (c) a session-time patch of
  `update/update.rpf` under the launcher's deploy/restore manifest, recommended; it needs the owner's acceptance and a test of the
  Rockstar launcher's reaction under Proton, so the apply step is not implemented here.
* **Changed**: `Shared/DlcPacks.cs` (new: `DlcPackInfo` wire shape, `DlcPackNames.IsValid/Missing`), `Shared/ServerSettings.cs`
  (`<dlcpack name url sha256 size required/>`), `Shared/Packets.cs` (`ConnectionRequest.DlcPacks`, ProtoMember 10),
  `Server/GameServer.cs` (validated list, logged at start), `Server/ProcessMessages.cs` (refusal naming the missing required packs),
  `Server/Managers/FileServer.cs` (`GET /dlcpacks.json`), `Client/Util/DlcPacks.cs` (new: reads `dlcpacks/mounted.json`) +
  `Client/Main/Network/MainNetwork.cs` (sends it), `Tools/GTANetwork.Bot` (`--dlc <name>`), `Launcher.Core/DlcPacks.cs` (new:
  fetch, state, download with SHA-256 and size verification, `PrepareAsync`), `Launcher.Core/Paths.cs` (`DlcPacksDir`),
  `Launcher/Program.cs` (`prepare <host:port>`), `Launcher.Gui` (the *Packs* page: server address, Fetch, the list with states,
  Download missing), `eng/integration-test-dlc.sh` (new) in `eng/dev-test.sh` and CI, docs.
* **Verified**: `eng/integration-test-dlc.sh`: `/dlcpacks.json` lists the declared packs; a bot without the pack is refused with
  "needs the DLC packs: testpack" and the server logs it; a bot with `--dlc testpack` joins; `GTANetwork.Launcher prepare` downloads
  the good pack (hash equal to the served file), reports the pack with the wrong declared hash and keeps no file for it, exits 1;
  a second `prepare` finds the pack up to date. `eng/dev-test.sh` green.
* **Not done / follow-ups**: the apply step (Q-15) — `mounted.json` is written by nobody yet, so every client reports no packs and a
  server with a required pack refuses everyone (by design until the overlay exists); the master list does not carry the pack list
  yet (the launcher asks the server directly); T-022 (in-game download + restart) depends on the apply step.

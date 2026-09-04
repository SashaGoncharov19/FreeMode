# T-014 — Custom DLC packs: server manifest, launcher download and install-time overlay (design + first implementation)

Status: ready
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
      byte-identical after exit (`Deployment.Restore` verified by hash).
- [ ] A client without the pack is refused with the pack name in the message.

## Log

* 2026-09-04 22:10 agent — created as draft.
* 2026-09-04 23:00 agent — ready: D-10 decided (download anywhere, apply at game start); the overlay question stays inside this task.

## Result

(empty)

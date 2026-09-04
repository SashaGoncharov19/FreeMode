# T-022 — DLC packs in game: download for the next server, restart-to-apply through the launcher, auto-join

Status: ready
Epic: E-08 DLC packs
Size: M
Branch: task/T-022-dlc-in-game from the integration branch
Depends on: T-014, T-012
PR: yes

## Goal

Connecting to a server whose packs are not installed shows the packs (name, size) in the CEF loader, downloads them into
`~/GTANetwork/dlcpacks/` while the player waits (or in the background from the menu, T-013), and — when the mounted set
differs — offers "Restart with packs": the client writes a relaunch request (pack set + server address) for the launcher,
exits the game; the launcher, still waiting on `GTA5.exe`, applies the overlay and starts the game with auto-join.

## Files

* Change: `Client/Main/Network/ProcessMessages.cs:816` (connect flow: compare the server's `dlcpacks.json` with the mounted set),
  `Client/Networking/DownloadManager.cs` or `Shared/ResourceFiles.cs` (pack download with SHA256 and resume), `ui/loader/**`
  (pack list, progress, the Restart button), `Shared/GTANSchemeListener.cs` (extend the auto-join memory-mapped record with a
  relaunch request, or a `relaunch.json` in the install), `Launcher.Core` (`LaunchSession`: after `GTA5.exe` exits, if a relaunch
  request exists → apply packs → start again → auto-join), `docs/CODEMAP.md`, `CHANGELOG.md`.

## Acceptance criteria

- [ ] Owner check: join a local server that declares a pack not yet installed → the loader downloads it → Restart → the game
      comes back with the pack mounted and auto-joins; total time recorded.
- [ ] Switching to a server with a subset of the mounted packs needs no restart.

## Log

* 2026-09-04 23:00 agent — created (D-10).

## Result

(empty)

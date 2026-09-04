# T-010 — Launcher GUI (Avalonia 12): Play, settings, log viewer; the CLI becomes a thin front end

Status: ready
Epic: E-06 Launcher
Size: L
Branch: task/T-010-launcher-gui from the integration branch
Depends on: T-001
PR: yes

## Goal

`GTANetwork.Launcher.Gui` (one binary for Linux and Windows) shows: status of the install (game found, Proton/Steam,
ScriptHookV present), a Play button (runs the existing launch pipeline), a Settings page bound to `PlayerSettings`
(launch method, paths, CEF options, debug), a Logs page tailing `logs/*.log`. The CLI keeps working (`play.sh` calls it).

## Files

* New: `Launcher.Core/` (move `Deployment.cs`, `GamePatcher.cs`, `Steam.cs`, `Vdf.cs`, `Paths.cs`, `GameProcess.cs`,
  `HitchMonitor.cs`, the launch pipeline out of `Program.cs` into `LaunchSession`), `Launcher.Gui/` (Avalonia 12.1.2, MVVM
  with CommunityToolkit.Mvvm; views `Home`, `Settings`, `Logs`), `Launcher/` keeps `Program.cs` + `Log.cs` referencing Core.
* Change: `GTANetwork.sln`, `.github/workflows/build.yml` (publish the GUI for linux-x64 and win-x64 single-file; artifacts),
  `eng/setup-linux.sh` (desktop entry points at the GUI; `play.sh` unchanged), `eng/package-client.ps1` (GUI in the Windows
  package), `Setup/Setup.nsi` (shortcut), `README.md`, `CHANGELOG.md`, `docs/CODEMAP.md` §7.

## Acceptance criteria

- [ ] `dotnet run --project Launcher.Gui` on the owner's Debian opens the window; Play starts the game exactly like `play.sh`.
- [ ] Settings edits are written to `settings.xml` and read back by the in-game client (owner check: change `CefFrameRate`).
- [ ] The CLI `GTANetwork.Launcher --help` and `eng/dev-test.sh` unchanged in behaviour.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

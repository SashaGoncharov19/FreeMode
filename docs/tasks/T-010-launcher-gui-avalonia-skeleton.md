# T-010 — Launcher GUI (Avalonia 12): Play, settings, log viewer; the CLI becomes a thin front end

Status: needs owner (implemented; the window itself must be seen on the owner's Debian)
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

- [ ] `~/GTANetwork/gui/GTANetwork.Launcher.Gui` on the owner's Debian opens the window; Play starts the game exactly like `play.sh`.
- [ ] Settings edits are written to `settings.xml` and read back by the in-game client (owner check: change `CefFrameRate`).
- [x] The CLI `GTANetwork.Launcher --help` / `doctor` and `eng/dev-test.sh` unchanged in behaviour (the doctor prints the same lines
      from `LaunchSession.Doctor`); the window builds headless (`--self-test`) in the dev container and CI.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-010-launcher-gui` (decision D-15); build and headless self-test green; PR opened.
  The window on a real display is the owner's check.

## Result

* **Changed**: new `Launcher.Core/` (the launcher's files moved here and made public; new `LaunchSession.cs` with
  `DetectedEnvironment`, `SettingsStore`, `DoctorLine`, `LaunchSession.Play/Launch/Doctor`; `Log.Written` event),
  `Launcher/Program.cs` reduced to the command line front end (same commands, options and messages), new `Launcher.Gui/`
  (Avalonia 12.1.2 + Avalonia.Desktop/Themes.Fluent/Fonts.Inter/Headless, CommunityToolkit.Mvvm 8.4.2: `Program.cs` with
  `--install-dir`, `--self-test`, `--help`; `App.axaml`; `Views/MainWindow.axaml` — navigation, Home with the status lines,
  Play/Stop/Check again, the Debug switch and the log, Settings bound to `PlayerSettings`, Logs with the file list and a
  300-line tail refreshed every 2 s; `ViewModels/MainViewModel.cs`), `GTANetwork.sln`, `.github/workflows/build.yml` (publish
  linux-x64 and win-x64 single-file with native libraries, run `--self-test`, artifacts `gtanetwork-launcher-gui-*`),
  `eng/dev-test.sh` (`--self-test` after the build), `eng/setup-linux.sh` (`--build` publishes the window into `<install>/gui/`,
  releases download `gtanetwork-launcher-gui-linux-x64-*` when present, the desktop entry opens the window when it exists),
  docs (`README.md`, `CHANGELOG.md`, `docs/CODEMAP.md` §7, `docs/DECISIONS.md` D-15 + Q-08, `docs/HANDOFF.md`).
* **Verified**: `dotnet build GTANetwork.sln` in the dev container; `GTANetwork.Launcher.Gui --self-test` (headless: the window
  shows, the status lines and the settings load, the log files list) → `self-test OK`; `GTANetwork.Launcher doctor` prints the
  same report as before; `eng/dev-test.sh` green.
* **Owner check**: `~/GTANetwork/gui/GTANetwork.Launcher.Gui` opens the window (dark, three sections); Home lists the same lines
  as `play.sh doctor`; Play deploys and starts the game like `play.sh` and Stop restores; Settings → change "Frames per second"
  → Save → `<CefFrameRate>` in `~/GTANetwork/settings.xml` changes; Logs shows `Runtime.log`.
* **Not done**: the server list with favourites and direct connect in the window (needs the master list, T-011; Q-11 a), the
  updater (manifest by SHA256, channels), an application icon, the Windows package (`eng/package-client.ps1`, `Setup.nsi`) does
  not include the window yet — it ships as its own artifact.

# Changelog

All notable changes to the revived GTA Network are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Two version numbers exist side by side:

* **Release tag** (`v0.1.0-alpha.19`, later `v0.1.0`, `v0.2.0` ...): the version people install. It is chosen by
  hand when a release is published (`build.yml` → *Run workflow* → `release_tag`), and the matching section of
  this file becomes the release notes. A `-` in the tag marks a pre-release.
* **Assembly/file version** (`0.1.<days since 2016-01-01>.<UTC minutes / 2>`, e.g. `0.1.3898.584`): derived from
  the commit date by `eng/version.sh` so that every artifact of one build carries the same number and the
  network protocol version check keeps working. It appears in artifact names and in the game.

## [Unreleased]

Nothing yet.

## [0.1.0-alpha.24] - 2026-09-04

### Fixed
* Client-side JavaScript could not see any member of any host object (`API.sendNotification` was `undefined`,
  so every client script failed at its first line): `libs/v8-x64.dll` was V8 5.4.500.40 from ClearScript
  5.4.6/5.4.7 while the native bridge `ClearScriptV8-64.dll` was a 5.4.9 build made for V8 5.5.372.40. The
  three files are now the official ClearScript.V8 5.4.9 package (managed assembly, bridge, V8 5.5.372.40).

## [0.1.0-alpha.23] - 2026-09-03

### Added
* `auth` resource (`Server/resources/auth`, disabled by default in `settings.xml`): accounts with registration
  and login through a CEF form (`client.js` + `ui/`) or `/register` and `/login`; chat and every other command
  are cancelled and the player stays frozen until logged in. Passwords are stored as salted PBKDF2-SHA256
  hashes in `accounts.json`; other resources read `API.getEntityData(player, "auth:account")`.
  `eng/integration-test-auth.sh` drives it with the bot in CI.

* `docs/SYNC.md`: review of the synchronization, relay and streaming code with the fixes below and the open
  items; `docs/CEF-UPGRADE.md`: plan for replacing the 2017 CEF build.

### Fixed
* Resource scripts under `Server/resources` were also compiled into the server assembly by the SDK's default
  source glob; they are now excluded and only compiled by the server at runtime.
* Docs: the bundled browser is CEF 3.2987 (Chromium 57, 2017), not CEF 85.
* Server sync relay: near/far recipients are chosen by distance (2500 m) and dimension instead of rank, so
  a full-map server no longer relays every packet to every player; the basic packet is only built when
  someone is far; `Fake`/null-connection guards in every relay; the throttle uses a monotonic clock and is
  pruned on disconnect; AFK players are torn down like real disconnects (entities leaked before);
  `sendNativeToPlayersInRangeInDimension(Hash)` recursed forever; `Client` got a `GetHashCode`;
  `getAllPlayers()` copies under the lock; unoccupied-vehicle packets are parsed in O(n).
* Client sync/streaming: vehicles and peds were never deleted on stream-out (`Prop.Exists()` type check);
  `Count(Type)` always returned 0; four stream-out filters had a precedence bug; an exception in the
  streamer ended streaming for the session; stale-data guard in `Vehicle.cs` compared two different clocks;
  divisions by zero before the second packet; unbounded ragdoll/parachute extrapolation; `AimPlayer` null
  dereference; leaked aim prop; nametag line-of-sight traces for every player every frame; sender thread
  spinning a core while connecting; racy counters; duplicate natives in the ped collector; `ModelRequest`
  stuck after an exception; lazy `ClientMap` enumeration across yields.

## [0.1.0-alpha.22] - 2026-09-03

### Added
* `[PROFILE] Present hook overlay` line in `Runtime.log` every 10 s: time spent in the DirectX overlay inside
  `Present` (render thread), the part of the frame the script profiler cannot see.
* The API probe enumerates the members JavaScript can see on `API` and compares with a one-method probe host
  object, to tell "ClearScript exposes nothing" from "ScriptContext is special".

## [0.1.0-alpha.21] - 2026-09-03

### Changed
* ScriptHookVDotNet thread hand-offs poll for up to 100 µs before blocking. Every native call from a script
  and every script tick is a round trip between the game thread and the script thread; with kernel events
  each round trip paid the wake-up latency of a sleeping thread (tens of microseconds, more under wine),
  which multiplied by a few hundred native calls per frame was a large share of the frame time.

## [0.1.0-alpha.20] - 2026-09-03

### Added
* `CHANGELOG.md` (this file) and `docs/ROADMAP.md` (the plan towards a RAGE Multiplayer 0.3.7-class platform).
* The GitHub release body is taken from the changelog section that matches the release tag.
* ScriptHookVDotNet profile summary: every 10 s one `[PROFILE]` line with frames, fps, script time per frame and
  the ten most expensive scripts (average and worst tick, native calls per tick), so a performance report can
  quote numbers instead of impressions.
* Client-side script errors now log the V8 error details (script line and stack) and the resource/file name; an
  "API probe" line in `Runtime.log` says whether `API` and its events are visible to JavaScript before a script
  starts.

## [0.1.0-alpha.19] - 2026-09-03

### Fixed
* freeroam `client.js`: the `onChatCommand` handler declared `(command, cancel)` although the API delivers one
  argument (the chat line) and cannot cancel it; typing `/ping` threw a TypeError in the game. `/ping` is now
  a server-side command and the client script reports "Client-side script is running" in chat through a
  `freeroam:clientReady` event, so a working JavaScript chain is visible.

## [0.1.0-alpha.18] - 2026-09-03

### Fixed
* The once-per-second stutter reported in game. The slow-tick profiler (alpha.15) blamed two scripts:
  `DebugInfo` ran five full entity pool scans every 500 ms (~170 ms per refresh) for numbers only shown by the
  streamer overlay, and `WeaponManager` removed every weapon hash in one go every 500 ms (~100 native calls,
  25-30 ms). The scans now run only while the overlay is on; the weapon sweep covers eight hashes per update.

### Changed
* README: in-game verification on GTA V Legacy 1.0.3889 under Proton, memory pattern fallbacks documented.

## [0.1.0-alpha.17] - 2026-09-03

### Fixed
* ScriptHookVDotNet on game build 1.0.3889: the classic memory patterns for the entity pool, the camera pool and
  the loading-text hook no longer matched, so `World.GetAllVehicles/Peds/Props` returned nothing. The scan now
  tries the classic pattern first and then the variant upstream ScriptHookVDotNet uses since build 1.0.3788;
  the text hook falls back to the hash-based label lookup with image-range checks. The log says which variant
  matched. Still missing on 1.0.3889: the euphoria functions (unused by GTA Network) and the "force offline" patch.

## [0.1.0-alpha.16] - 2026-09-03

### Fixed
* Client-side JavaScript never started: the ClearScript.V8 NuGet package (5.4.6) shipped a managed
  `ClearScript.dll` whose version did not match the native bridge `ClearScriptV8-64.dll` (5.4.9) from `libs/`,
  so `V8ScriptEngine` threw `FileLoadException` ("CLIENTSIDE SCRIPT ERROR" for every resource with JS). The client
  now references `libs/ClearScript.dll` 5.4.9.

## [0.1.0-alpha.15] - 2026-09-03

### Added
* ScriptHookVDotNet slow-tick profiler: a script that holds the game thread for 20 ms or more in one tick is
  named in `ScriptHookVDotNet-*.log` ("held the game thread for N ms"), at most once per 5 s per script.

## [0.1.0-alpha.14] - 2026-09-03

### Fixed
* Linux updater stuck on an old release: GitHub lists releases by tag name, so `alpha.9` sorted after
  `alpha.13`. The newest non-draft release by `published_at` is used now.

## [0.1.0-alpha.13] - 2026-09-03

### Changed
* `MasterServerAddress` in `settings.xml` is the only source of the master server address (the menu ignored it
  in favour of a constant) and it is empty by default: `master.gtanet.work` is gone. Favourites, recent servers
  and LAN discovery keep working without a master. The Linux installer blanks the old default in existing
  settings. Rebuilding the master server is tracked in issue #1.

## [0.1.0-alpha.12] - 2026-09-03

### Fixed
* The game crashed a few seconds after the main menu appeared: the welcome-message fetch caught `WebException`
  only, and the Cloudflare page now served under the old master address produced a JSON parse error on a
  background thread, which terminates GTA5.exe. Every background thread (welcome message, whitelist,
  screenshots) now catches everything, and `AppDomain.UnhandledException` is written to `logs/Error.log`.
* The blind write to script global 2576573 ("enable MP vehicles") used a 2016 index and corrupts memory on
  current builds; it is behind the new `EnableMpVehiclesGlobal` setting, off by default.

## [0.1.0-alpha.11] - 2026-09-03

### Changed
* Linux installer: the game's own Proton is tried first for the .NET install again (an old cabinet failure in
  the log no longer skips it), because the failure was caused by Proton's symlinks, which are now replaced.

## [0.1.0-alpha.10] - 2026-09-03

### Fixed
* Linux installer: every file symlink in `system32`, `syswow64` and `Microsoft.NET` of the prefix is replaced by
  a real copy before the .NET install (Proton links them to its builtin DLLs and the installer cannot overwrite
  them: "Failed to extract cabinet netfx_core.mzz"). The install runs with `WINEDEBUG=warn+msi` so the file the
  installer could not write is named after a failed attempt.

## [0.1.0-alpha.9] - 2026-09-03

### Fixed
* Linux installer: first version of the symlink fix (a hand-picked list of `Microsoft.NET` support files).

## [0.1.0-alpha.8] - 2026-09-03

### Fixed
* Linux installer: a freshly unpacked GE-Proton has no Steam Runtime container yet; protontricks is retried
  with `--no-runtime`.

## [0.1.0-alpha.7] - 2026-09-03

### Added
* Linux installer: downloads GE-Proton8-32 (checksum verified) into `compatibilitytools.d` when no stable Proton
  is installed and the .NET installer cannot extract its cabinets; `--dotnet-proton <name|auto>`; the Proton
  that worked is remembered in `setup.conf`; the prefix's Windows version is reset to 10 after winetricks.

## [0.1.0-alpha.6] - 2026-09-03

### Added
* Linux installer: retry the .NET install with Proton 8.0 / 9.0 / 10.0 from any Steam library when the cabinet
  extraction fails; free-space check; `logs/protontricks.log`.

## [0.1.0-alpha.5] - 2026-09-03

### Fixed
* Linux installer: leftover `GTA5.exe` / Rockstar Launcher processes in the prefix made `wineserver -w` wait
  forever; they are listed and can be stopped first. Retry without the Steam Runtime container; quieter
  `/proc` scan.

## [0.1.0-alpha.4] - 2026-09-03

### Changed
* Linux installer: protontricks and winetricks are installed into a private Python venv first (installing
  `python3-venv` and `cabextract` when needed); the Debian `contrib` route reports every APT source it examined.

## [0.1.0-alpha.3] - 2026-09-03

### Added
* Linux auto-updater: `update.sh` compares `release.txt` with the newest GitHub release and installs it;
  `play.sh`, `server/start.sh` and `bot.sh` update first (`GTAN_NO_UPDATE=1` skips). The installer keeps a copy
  of itself and its options (`setup.conf`) and updates itself before applying an update. Nothing is replaced
  while a server, bot or launcher from the install folder is running; `server/settings.xml`, the player name and
  ScriptHookV are kept.
* protontricks is installed on demand (Debian `contrib` handling, venv and Flatpak fallbacks).
* The Legacy `ScriptHookV.dll`/`dinput8.dll` is preferred when the ScriptHookV archive ships both builds.

## [0.1.0-alpha.2] - 2026-09-03

### Fixed
* Linux installer: the release JSON was read from stdin that a heredoc had shadowed.

## [0.1.0-alpha.1] - 2026-09-03

The first build of the revival. Everything below compares with the 2019-2020 code base.

### Added
* **Build system**: SDK-style projects with NuGet references and one `GTANetwork.sln`; the version is computed
  from the commit date (`eng/version.sh`, `Directory.Build.props`). Client, NativeUI and the classic launchers
  (net48) compile on any OS through the reference assemblies; `Shv.NET/ref` is a managed stub of the
  ScriptHookVDotNet API so the in-game client builds without MSVC.
* **Server on .NET 8**, native on Linux: Roslyn for runtime C#/VB resources, POSIX signals for clean shutdown,
  an `HttpListener` file server (replaces Nancy/OWIN), no `Thread.Abort`, SHA256 file hashes, a
  `settings.xml`, the `example` and `freeroam` resources.
* **Cross-platform launcher** (net8.0): finds Steam, GTA V, the Proton prefix and Proton from the VDF files,
  deploys the ASI loader files with a manifest for rollback, patches the profile, starts the game through
  Steam or Proton, restores the folder afterwards; `doctor` command.
* **ScriptHookVDotNet fork**: builds without the official SDK (`Shv.NET/sdk-compat`), VS2022 toolset; every
  memory pattern goes through a checked lookup, so a pattern that no longer matches the installed game build is
  logged and its feature disabled instead of crashing the game.
* **Headless bot** (`Tools/GTANetwork.Bot`): joins a server over the real protocol (discovery, handshake, file
  transfer, chat, commands, position sync) and decodes what the server pushes; interactive stdin mode.
* **CI** (`.github/workflows/build.yml`): Linux job (build, publish, server smoke test, bot integration test
  with one and two players), Windows job (C++/CLI hook, client package, NSIS installer), releases from tags or
  a manual run with `release_tag`; Linux builds are self-contained.
* **Linux one-shot installer** `eng/setup-linux.sh`: installs client, launcher, server and bot from a GitHub
  release into `~/GTANetwork`, extracts ScriptHookV from the user's zip, writes `settings.xml`, installs
  .NET 4.8 and VC++ 2022 into the game prefix, creates `play.sh`, `server/start.sh`, `bot.sh` and a desktop entry.
* README in English and Ukrainian: architecture, flow, building, Linux/Proton guide.

### Fixed
* Server: broadcasting a native call to all players reused one Lidgren message and threw on the second
  recipient, so any `setEntityPosition`/`setTime`-style API call failed with two players online.
* Server: the sync relay threw with an empty recipient list when a single player was online.

[Unreleased]: https://github.com/SashaGoncharov19/FreeMode/compare/v0.1.0-alpha.24...HEAD
[0.1.0-alpha.24]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.24
[0.1.0-alpha.23]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.23
[0.1.0-alpha.22]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.22
[0.1.0-alpha.21]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.21
[0.1.0-alpha.20]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.20
[0.1.0-alpha.19]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.19
[0.1.0-alpha.18]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.18
[0.1.0-alpha.17]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.17
[0.1.0-alpha.16]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.16
[0.1.0-alpha.15]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.15
[0.1.0-alpha.14]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.14
[0.1.0-alpha.13]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.13
[0.1.0-alpha.12]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.12
[0.1.0-alpha.11]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.11
[0.1.0-alpha.10]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.10
[0.1.0-alpha.9]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.9
[0.1.0-alpha.8]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.8
[0.1.0-alpha.7]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.7
[0.1.0-alpha.6]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.6
[0.1.0-alpha.5]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.5
[0.1.0-alpha.4]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/SashaGoncharov19/FreeMode/releases/tag/v0.1.0-alpha.1

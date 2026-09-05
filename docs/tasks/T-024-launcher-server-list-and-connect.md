# T-024 — Launcher window: server list from the master, favourites and recent, connect in one click

Status: needs owner (implemented; the one-click connect must be seen in game)
Epic: E-06 Launcher with a GUI (Linux and Windows) and an updater
Size: M
Branch: task/T-024-launcher-server-list from the integration branch
Depends on: T-010, T-011
PR: yes

## Goal

The launcher window has a *Servers* page: the master list (`MasterServerAddress` → `GET /servers/full`: name, players, gamemode,
map, version, verified, key), the favourites and recent servers from `settings.xml`, a direct-connect field; *Connect* starts the
game and joins that server as soon as the game is ready (no menu), pinning the key the master gave. The CLI has the same:
`GTANetwork.Launcher run --connect host:port[#key]`.

## Why

Q-11 (a): one-click play from the launcher next to the in-game menu. The master list exists (T-011) and the in-game menu reads it;
the launcher only had Play.

## Scope

* In: the page, the master fetch, favourites/recent (shared with the in-game menu through `settings.xml`), direct connect, the
  `GTAN_CONNECT` hand-over to the client, the CLI flag.
* Out: LAN discovery in the launcher (the in-game menu does it), the updater (its own task), ping columns.

## Files

* Read: `Client/Util/AutoTest.cs` (how the game connects by itself after loading), `Client/GUI/CefMenu.cs` (`MasterRow`, the
  favourites/recent handling), `Launcher.Core/LaunchSession.cs:239` (which variables reach the game).
* Change: `Client/Main.cs` + new `Client/Util/AutoConnect.cs` (`GTAN_CONNECT=host:port[#key][;password]`: connect when the game is
  ready), `Launcher.Core/LaunchSession.cs` (forward `GTAN_CONNECT`), new `Launcher.Core/ServerList.cs` (fetch + favourites/recent),
  new `Shared/MasterList.cs` (`MasterServerRow`, the `/servers/full` shape), `Launcher/Program.cs` (`--connect`), `Launcher.Gui`
  (the Servers page), `docs/CODEMAP.md`, `CHANGELOG.md`.

## Acceptance criteria

- [x] `GTANetwork.Launcher.Gui --self-test` passes; the Servers page merges the master rows (`MasterServerRow`, the `/servers/full` shape) with the favourites/recent of `settings.xml` (`ServerList.Merge`).
- [ ] Owner check: *Connect* on the local server starts the game and joins without touching the menu; `Runtime.log` has
      `autoconnect: connecting to host:port`.

## Log

* 2026-09-05 20:10 agent — created and started (follows T-011; the plan's "server list in the GUI").
* 2026-09-05 20:50 agent — page, fetch, favourites/recent, connect hand-over and the CLI flag done; PR opened; needs the owner in game.

## Result

* **Changed**: `Shared/MasterList.cs` (new: `MasterServerRow`), `Client/Util/AutoConnect.cs` (new) + `Client/Main.cs` (the tick),
  `Launcher.Core/ServerList.cs` (new: fetch, merge, favourites, recent, the `GTAN_CONNECT` target), `Launcher.Core/LaunchSession.cs`
  (forwards `GTAN_CONNECT` and `GTAN_AUTOTEST_STAY`), `Launcher/Program.cs` (`--connect`), `Launcher.Gui` (the Servers page:
  master rows with players/gamemode/version/verified/pinned, ★ favourites written to `settings.xml`, recent, direct connect;
  Connect sets `GTAN_CONNECT`, remembers the server as recent and runs Play), `CHANGELOG.md`, `docs/CODEMAP.md`.
* **Verified**: the solution builds, the GUI headless self-test passes, the client builds against the real ScriptHookVDotNet build;
  `eng/dev-test.sh` green. The join itself needs the game.
* **Owner check**: `~/GTANetwork/gui/GTANetwork.Launcher.Gui` → Servers → type `127.0.0.1:4499` → Connect (or
  `~/GTANetwork/GTANetwork.Launcher run --connect 127.0.0.1:4499`): the game starts and joins the local server without the menu;
  `Runtime.log` has `autoconnect: game ready ...` and `autoconnect: connecting to 127.0.0.1:4499`. With `MasterServerAddress` set,
  the page lists the master's servers and Connect on one of them pins its key (`session: ... pinned` in `Runtime.log`).
* **Not done / follow-ups**: LAN discovery in the launcher; ping per server; the updater (E-06).

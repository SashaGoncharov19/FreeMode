# T-024 — Launcher window: server list from the master, favourites and recent, connect in one click

Status: in progress
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

- [ ] `GTANetwork.Launcher.Gui --self-test` still passes; the Servers page lists the rows of a master (`eng/integration-test-master.sh`'s
      shape) and the favourites/recent of `settings.xml`.
- [ ] Owner check: *Connect* on the local server starts the game and joins without touching the menu; `Runtime.log` has
      `autoconnect: connecting to host:port`.

## Log

* 2026-09-05 20:10 agent — created and started (follows T-011; the plan's "server list in the GUI").

## Result

(empty)

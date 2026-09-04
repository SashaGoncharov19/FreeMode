# T-013 — In-game CEF main menu: server list, favourites, direct connect, settings (replaces the NativeUI server browser)

Status: ready
Epic: E-12 CEF UI
Size: L
Branch: task/T-013-cef-menu from the integration branch
Depends on: T-011, T-012
PR: yes

## Goal

At game start (before any server) the player sees `ui/menu/index.html`: servers from the master list with ping and
players, favourites and recent (from `PlayerSettings`), direct connect, settings (the items `Client/Main/Menu.cs` has
today), Quit. NativeUI remains for the pause-menu tabs until they are ported.

## Files

* New: `ui/menu/**` (list, filters, settings form; talks to the client through `resourceCall("menu:…")`/`gtan.rpc` (T-008)).
* Change: `Client/Main/Menu.cs` (the NativeUI main menu + server browser behind a setting `<CefMenu>true</CefMenu>`; the
  master calls move into a `ServerListService` used by both), `Client/GUI/CEFManager.cs` (`SystemBrowser` from T-012 reused;
  `CefPreload` behaviour: the host starts at game start when `CefMenu` is on), `Shared/PlayerSettings.cs` (`CefMenu`, default
  true once verified), `Client/Main/Network/MainNetwork.cs:61` (connect from the menu), `docs/CEF-UPGRADE.md`, `CHANGELOG.md`.

## Acceptance criteria

- [ ] Owner check: game start → menu within 3 s of the client loading; the owner's server appears (master or LAN), connect works,
      favourites persist across restarts; settings changes land in `settings.xml`.
- [ ] Memory: with the menu on and no server joined, `hitch-monitor.log` shows Chromium RSS; the idle exit keeps working after connect.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

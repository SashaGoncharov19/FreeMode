# T-013 — In-game CEF main menu: server list, favourites, direct connect, settings (replaces the NativeUI server browser)

Status: needs owner (implemented; in-game check pending)
Epic: E-12 CEF UI
Size: L
Branch: task/T-013-cef-menu from the integration branch
Depends on: T-012 (T-011 supplies the master list later; the menu already reads `MasterServerAddress` the way the NativeUI browser does)
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
- [x] Without the game: `eng/cef-harness.sh` renders the page (`menu page OK`), the client compiles against the real ScriptHookVDotNet.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-013-cef-menu` without waiting for T-011: the page lists favourites, recent, LAN and the
  master list the NativeUI browser already reads; the master service itself stays T-011. PR opened; the in-game check is the owner's.

* 2026-09-05 owner's first run: the page came up (`menu: page ready after 884 ms`) but took no input — the client-owned browser
  was not in `CEFManager.Browsers`, the list the input routing reads; every pause-key toggle created a new browser (1.5 s stalls
  in `[HITCH]`); the owner wants the menu right after the intro. Fixed on `task/T-013-menu-keep-browser`: input registration,
  `CefMenu.Prepare()` at client start (page loads concealed while the game loads), `Conceal()` instead of `Hide()` for the
  classic menu, `rpc:` log lines for the RPC path the owner reported as silent.

## Result

* **Changed**: new `Client/GUI/CefMenu.cs` (the client-owned full-screen browser, its state, the page's actions run on Main's tick,
  the 30 s fallback to NativeUI), new `ui/menu/{index.html, menu.js, style.css}` (servers with filter/favourites/password prompt,
  direct connect, settings form, footer with Classic menu and Quit), `Shared/PlayerSettings.cs` (`CefMenu`, default true),
  `Client/Main.cs` (the host starts with the game when the menu is on; `Init` shows the page instead of NativeUI; `CefMenu.Tick()`),
  `Client/Main/Cleanup.cs` (`ResetWorld` brings the page back after a disconnect), `Client/Main/Menu.cs` (`RebuildServerBrowser`,
  `AddServerToRecent`, `AddToFavorites`, `RemoveFromFavorites` shared with the page; the master list and its error reach the page;
  pause key opens the classic menu over the page, Back returns), `Client/Main/Network/ProcessMessages.cs` (discovery answers → the
  page; connecting hides it; a disconnect lets it return), `Tools/CefHarness/HostTest.cs` (`MenuPageCheck`, browser 4), docs
  (`CHANGELOG.md`, `docs/CEF-UPGRADE.md`, `docs/CODEMAP.md`, `docs/HANDOFF.md`).
* **Verified**: the client and the browser host compile against the real ScriptHookVDotNet in the dev container; `eng/cef-harness.sh`
  → `menu page OK: https://gtan/menu/index.html painted` (see the PR for the numbers).
* **Owner check**: start the game (`play.sh --debug`): the menu page appears once the game has loaded (Runtime.log: `menu: shown`,
  `menu: page ready after N ms`); the local server shows under LAN within a second of Refresh; double-click or Connect joins it and
  the page goes (`menu: hidden … (connect …)`); after `/quit` or a kick the page is back; ★ on a row survives a restart
  (`settings.xml` `<FavoriteServers>`); Settings → Save writes `settings.xml`; the pause key shows the classic menu, Back returns.
* **Not done**: a ping column (needs the master list, T-011), the pause-menu tabs (players, current server, host) stay NativeUI,
  server branding on the page.

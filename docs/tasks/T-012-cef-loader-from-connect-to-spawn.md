# T-012 — CEF loading screen from "connect" until spawn

Status: needs owner
Epic: E-12 CEF UI
Size: M
Branch: task/T-012-cef-loader from the integration branch
Depends on: none
PR: yes

## Goal

From the moment the player connects until the first sync packet is applied, a full-screen CEF page shipped with the
client (`ui/loader/index.html`) covers the game and shows: server name, connection state (connecting → approved →
downloading N/M files, X MB → starting scripts → spawning), errors with a Retry/Back; then it fades out. Servers may
brand it (logo/background URL from `SharedSettings`).

## Files

* New: `ui/loader/index.html`, `ui/loader/loader.js`, `ui/loader/style.css` (no framework; the page receives events via
  `window.gtanLoader.update(json)` called from the client through `Browser.eval`).
* Change: `Subprocess/GTANetwork.CefHost/Program.cs` (`--ui-root <install>/ui`; `LocalResourceRequestHandler` maps
  `https://gtan/<path>` to the ui root; the bridge shim is injected too), `Client/GUI/CEFManager.cs` (`StartHost` passes `--ui-root`;
  a `SystemBrowser` created by the client itself, not by a resource, full-screen, top-most element), `Client/Main/Network/ProcessMessages.cs:816`
  (`StatusChanged`: create/show the loader on `InitiatedConnect`, update on approval, hide on the first `PedPureSync`
  applied or on the server's spawn event), `Client/Networking/DownloadManager.cs` and `Client/Main/Network/Download.cs`
  (progress callbacks: files done/total, bytes), `Client/Main/Misc.cs` (replace the NativeUI loading prompts with loader
  updates when the loader is on), `Shared/Packets.cs` (`SharedSettings.LoaderLogoUrl`, `LoaderBackgroundUrl` — optional),
  `eng/package-client.ps1`, `eng/dev-sync-client.sh`, `eng/setup-linux.sh` (ship `ui/`), `docs/CEF-UPGRADE.md` (a "Client UI pages" section), `CHANGELOG.md`.

## Approach

1. The host already starts on `InitiatedConnect`; the loader browser is created as soon as `ready` arrives (≈1 s).
   Until then the existing text prompt stays (no black screen).
2. `SystemBrowser`: a `Browser` owned by `CEFManager` (not by a resource), id reserved (e.g. 1000000), 1920x1080 → resized to the
   back buffer size on the overlay's resize event; input goes to it while visible.
3. Events → `Browser.eval("gtanLoader.update(" + json + ")")`; states as in Goal; a `Retry` calls `resourceCall("loader:retry")`
   handled by the client (not a resource).
4. Hide: fade 300 ms via CSS, then `Close()`; the idle-exit timer then applies unless a resource keeps a browser.

## Acceptance criteria

- [x] Harness: `eng/cef-harness.sh` (default `--ui-root <repo>/ui`) loads `https://gtan/loader/index.html` in a third browser,
      sends a state through `gtanLoader.update`, and the page paints: `loader page OK … 230400 opaque pixels of 230400`.
- [ ] Owner check: join the local server: the loader appears about a second after "connect", shows the phases and the file
      progress and fades out when the scripts start (the `auth` form appears after it); `Runtime.log` has `loader: shown for …`
      and `loader: hidden after N ms (resources ready, …)`; the N is the join time to record.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 00:20 agent — started on task/T-012-cef-loader (worktree).
* 2026-09-05 00:40 agent — decided: the loader hides when the resources are downloaded and the scripts start (`InvokeFinishedDownload`), not at "spawn" — on login-gated servers the player is not spawned until the auth page, which must be visible; server branding deferred (files arrive during the loader phase). Harness passes; synced into ~/GTANetwork; PR opened.

## Result

* **Changed**: new `Client/GUI/ConnectLoader.cs` (the client-owned full-screen browser and its state pushes), new `ui/loader/`
  (`index.html`, `style.css`, `loader.js`: `gtanLoader.update/hide`), `Subprocess/GTANetwork.CefHost/Program.cs` (`--ui-root`,
  `https://gtan/<path>` served from it with a path guard), `Client/GUI/CEFManager.cs` (`--ui-root <install>\ui` passed to the host;
  `Browser.PageMessage`/`PageLoaded` hooks for browsers without a script engine), `Client/Main/Network/ProcessMessages.cs`
  (show on `InitiatedConnect`, phases on `Connected`/download start, hide on `Disconnected`), `Client/Main/Network/Download.cs`
  (HTTP progress → loader; hide in `InvokeFinishedDownload`), `Client/Main/Misc.cs` (`LoadingPromptText` → detail line),
  `Client/Main/Network/MainNetwork.cs` (hide on local disconnect), `Shared/PlayerSettings.cs` (`CefLoader`, default true),
  `Tools/CefHarness` (`--ui-root`, loader page check), `eng/cef-harness.sh` (default ui root), `eng/package-client.ps1` and
  `eng/dev-sync-client.sh` (ship/sync `ui/`), `docs/CEF-UPGRADE.md`, `docs/CODEMAP.md`, `docs/HANDOFF.md`, `CHANGELOG.md`.
* **Verified**: `eng/cef-harness.sh` → `loader page OK: https://gtan/loader/index.html painted (230400 opaque pixels of 230400, 1 frame(s))`,
  `RESULT: OK … (22 events); latency eval -> frame: median 3.1 ms`; `harness-host.log` shows `[local] https://gtan/loader/index.html`,
  `style.css`, `loader.js` served. Client and host built against the real ScriptHookVDotNet and synced into `~/GTANetwork` (with `ui/`).
* **Not done / follow-ups**: server branding of the page (logo/background) — needs a URL source outside the resource files;
  a Retry/Back button (the existing warning dialogs handle failures); the in-game menu (T-013) reuses `https://gtan/` and `PageMessage`.
* **Owner check**: `~/GTANetwork/play.sh --debug`, join the local server. Expect: about a second after "connect" a dark full-screen page
  "GTA Network" with the server address, "Connecting" → "Connected" → "Downloading files" with a progress bar, then it fades and the
  `auth` login form appears. Then:
  ```bash
  grep -n "loader:" ~/GTANetwork/logs/Runtime.log | tail -5
  grep -n "gtan/" ~/GTANetwork/logs/CEF-host.log | tail -5
  ```
  Bad: no page (check `CEF.log` for the browser creation and `CEF-host.log` for `refused`), or the page stays after the login
  form appears (send the two log excerpts).

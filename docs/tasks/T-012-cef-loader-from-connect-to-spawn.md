# T-012 — CEF loading screen from "connect" until spawn

Status: ready
Epic: E-12 CEF UI
Size: M
Branch: task/T-012-cef-loader from the integration branch
Depends on: none
PR: no

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

- [ ] Harness: `eng/cef-harness.sh --ui-root <repo>/ui` loads `https://gtan/loader/index.html` (a new harness option) and
      `gtanLoader.update` changes the page (pixel check).
- [ ] Owner check: join the local server: the loader appears within 1.5 s of "connect", shows download progress and disappears at spawn;
      `Runtime.log` has `loader: shown at …, hidden at … (N ms)`; join time recorded.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)

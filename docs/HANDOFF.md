# Handoff — start here

Read this first if you are picking up the project in a new session. It is the live state: what we are
doing, where it hurts, and what to do next. The long-term plan is `docs/ROADMAP.md`; this file is the
"right now".

## The project in three lines

GTA Network / **FreeMode** is a revival of the 2016–2018 GTA V multiplayer mod. Goal: a modern,
Linux-first build (server, launcher and bot are native .NET 8 on Linux; the game runs through Proton)
with CI and GitHub releases. It is tested by the owner **by hand on Debian 13 + GTA V Legacy 1.0.3889
under Proton** — there is no Windows machine and no GTA V in CI, so anything touching the hook or
rendering can only be verified in game by the owner.

## Architecture in one screen

* **Client** = `GTANetwork.dll` (**.NET Framework 4.8**), loaded inside `GTA5.exe` by a
  **ScriptHookVDotNet** fork (C++/CLI, hosts the desktop CLR). SHVDN runs the client in a **second
  AppDomain** (`ScriptDomain_...`); scripts run on their own threads and hand off to the game thread; a
  watchdog aborts a script that blocks for seconds, so nothing on a script thread may block for long.
* **Browser** = **`cef\GTANetwork.CefHost.exe`**, a separate process (net48, `Subprocess/GTANetwork.CefHost`)
  that runs Chromium 151 through **CefSharp.OffScreen**. The game starts it with redirected stdin/stdout,
  sends commands and receives events as length-prefixed JSON (`Shared/Cef/CefHostProtocol.cs`) and reads
  every browser's pixels from a shared-memory frame buffer (`Shared/Cef/CefFrameBuffer.cs`, seqlock). The
  pixels are drawn by the DirectX 11 overlay hooked onto the game's swap chain (SharpDX, under DXVK).
  **Nothing of CefSharp or libcef is loaded into GTA5.exe.**
* **Script engine**: client-side JavaScript on **ClearScript 7.5 (V8 12)**, in the game process.
* **Server / launcher / bot**: .NET 8, Linux-native. The **headless bot** (`Tools/GTANetwork.Bot`) speaks
  the real protocol (Lidgren + protobuf) and is how we test the server without the game (CI runs it).
* **CEF harness** (`Tools/CefHarness`, `eng/cef-harness.sh`): drives the browser host under Proton in the
  game's Wine prefix, without the game: start, browser, local page, pixels, page→game bridge, resize, close.
* **Install layout** on the owner's machine: `~/GTANetwork/` with `bin/` (SHVDN.dll, native libs,
  ClearScript native), `bin/scripts/` (managed client DLLs), `cef/` (browser host + Chromium runtime),
  `server/`, `logs/`, `resources/` (downloaded resource files), and `play.sh` / `setup-linux.sh`.

## Current state (4 September 2026, evening)

* **`master`**: `v0.1.1` — the **old** browser stack (CefGlue, Chromium 57, single-process). Last
  configuration verified working in game (auth login form, account registered).
* **`claude/modernize-deps-4d8uyn`** (working branch, all new commits go here): the modernisation —
  CefSharp 151, ClearScript 7.5, debug mode, dev container, and now **the browser in its own process**.
  Pre-releases `v0.2.0-alpha.1 … alpha.5` ran Chromium *inside* the game and crashed (below); the
  separate-process fix is **in the working tree, uncommitted, not yet tried in game**. No PR open.
* **The blocker is understood and fixed in code** (root cause below). It is verified outside the game:
  the harness passes under Proton against both the freshly built and the installed browser host (Chromium
  ready in ~0.9 s, first frame ~1.6 s, the page's `resourceCall` reaches the game side). **What is missing
  is the in-game run by the owner.**

## THE FINDING — why Chromium 151 died inside the game, and the fix

**Root cause: CefSharp cannot run in a non-default AppDomain, and ScriptHookVDotNet runs the client in
one.** CefSharp is C++/CLI. Chromium's own threads (UI thread, IO thread, …) have no managed context; when
one of them first calls into CefSharp's managed code, the CLR enters the **default AppDomain**, where none
of our assemblies are loaded or resolvable (the default domain probes the game folder). The result is a
managed exception on a Chromium thread — the `0xe0434352` (`80070002` = FileNotFound, later
`80131506`) seen in the Wine logs — which is unhandled there and kills the process. `Cef.Initialize` never
returns because the UI thread dies during start-up. The old CefGlue binding was pure P/Invoke with
delegates bound to the script domain, which is why it worked.

Evidence (all reproducible in a minute with `eng/cef-harness.sh`, no game needed):

| Run | Result |
| --- | --- |
| `--in-process` (Chromium inside the harness, default AppDomain) | works: paint in 0.8 s |
| `--in-process --external-pump` | works |
| `--appdomain` (second AppDomain, assemblies still visible to the default domain) | Initialize returns, then `Unhandled exception 0xe0434352` on `CefUIThread` |
| `--alone --appdomain` (exe alone, nothing resolvable in the default domain = the game) | **`Cef.Initialize` never returns, `0xe0434352` on a Chromium thread, access violation, hang** — the in-game picture exactly |
| default: `GTANetwork.CefHost.exe` driven over the protocol | works: ready 0.9 s, pixels 1.6 s, `resourceCall` from the page, resize, clean exit |

Consequences: no Chromium switch, GL mode, GPU mode or message-pump mode could ever have fixed it (the
IO-thread callbacks — resource handlers, child process launch — always come from Chromium's own threads).
The external message pump was implemented and tested for completeness; it is not in the code any more.

**The fix**: Chromium runs in **`cef\GTANetwork.CefHost.exe`**, a plain process where CefSharp lives in the
default AppDomain. This is also the design the owner asked for (“окремо процесом, бест практіс, щоб був
реальний перфоманс”): the game process never loads libcef, Chromium's threads and subprocesses cannot take
the game down, and the frame transport is ready for the shared-texture step.

## What changed in code (uncommitted at the time of writing)

* `Shared/Cef/CefHostProtocol.cs` — commands/events (`create`, `load`, `loadHtml`, `eval`, `back`, `close`,
  `resize`, `focus`, `fps`, mouse/key events, `shutdown`; `ready`, `initFailed`, `created`, `frame`, load
  events, `console`, `jsMessage`, `renderTerminated`, `log`, `closed`) and `CefHostChannel` (length-prefixed
  JSON over the host's stdin/stdout).
* `Shared/Cef/CefFrameBuffer.cs` — the shared-memory frame (64-byte header, BGRA, sequence counter as a
  seqlock; writer in the host, reader in the game).
* `Shared/CefLaunch.cs` — the Chromium switch list, shared by the host and the harness.
* `Subprocess/GTANetwork.CefHost/` — the host: CefSharp init, one `HostedBrowser` per browser with the render
  handler writing frames, the local `https://<resource>/` file serving (from `--resource-root`, with the
  `resourceCall` bridge injected as the **first script of every served HTML page** — CefSharp 151 has no V8
  extensions any more, and the asynchronous injections alone lost the race against page scripts), pop-up and
  context-menu handling, `resourceCall`/`resourceEval` forwarding, a parent-process watchdog.
* `Client/GUI/CEFManager.cs` — starts the host (`--parent`, `--log`, `--chromium-log`, `--cache`,
  `--resource-root`, `--gpu`, `--gpu-process`, `--media-stream`, `--verbose`, `--devtools`), reads events,
  pumps frames into the overlay (~60 Hz, one shared read per browser when nothing changed), `Browser` and
  `BrowserInput` with the same API the scripts and `CefController` used (input enums mirror CefSharp's
  values). `Client/GUI/CefClient.cs` is now just `CefUtil` + `OverlayRenderHandler` (frame → bitmap).
  The client csproj no longer references CefSharp.
* `Tools/CefHarness/` — the harness (host-protocol mode by default; `--in-process`, `--appdomain`, `--alone`
  for the diagnosis above). `eng/cef-harness.sh` runs it under Proton in the game's prefix.
* `eng/dev-sync-client.sh` syncs the host into `cef/` (and removes stale DLLs from `bin/scripts`);
  `eng/dev-build-client.sh` builds the host too; `eng/package-client.ps1` ships the host's output folder as
  `cef\`; `Launcher/Deployment.cs` checks for the host exe. `.devcontainer/Dockerfile` got `python3`
  (`eng/integration-test-auth.sh` needs it); `.gitignore` got `.env` and `.gtan-install/`.
* Removed: the `CefExternalPump` experiment; `CEFManager.RegisterAssemblyResolver`.

**Second lesson (4 Sept, evening):** the first in-game run of the browser host froze the game at ~4 fps with a
system cursor over it. Not the protocol: the host was a *console* exe, and Wine's `conhost` gave it a visible
console window (`CreateNoWindow` notwithstanding) that took the foreground from GTA V, which then ran with its
background frame limiter. The host is a `WinExe` now (no console can exist; stdin/stdout are still the pipes the
game hands over) — `eng/cef-harness.sh --hold 12` with `xwininfo -root -tree` before/after shows no new window
from the host or Chromium. Any future helper process must be a Windows-subsystem exe for the same reason.

**Third lesson (4 Sept, evening): performance.** With the host a GUI exe the form appeared, but late (30 s), and the
*whole machine* lagged. Three causes, all measured: (1) **memory** — the machine has 15 GB, the desktop (Firefox,
Claude, Steam, gnome-shell) plus GTA V plus Chromium pushed it into swap (4.4 GB in swap right after the session,
memory PSI `full avg300=3.9`); Chromium came as **8 processes / 2.9 GB RSS** (two renderers of 750 MB — one of them a
*spare* — network and storage utilities of ~400 MB each, a metrics utility); (2) **`--debug`** — Proton's default
`WINEDEBUG` for `PROTON_LOG=1` traces every stack walk (`+unwind`: .NET's GC alone produces millions of lines) and GTA V
calls `ActivateKeyboardLayout` in a hot loop while loading (10 million `fixme:keyboard` lines): **1.2 GB of Wine log per
session**, written by every process including Chromium's; (3) the host was started only when the first browser was
created, so join → form included Chromium's cold start (~4 s under load). Fixes: `Shared/CefLaunch.cs` switches
validated against the strings of `libcef.dll` (the old `NetworkServiceInProcess` feature no longer existed — Chromium
ignores unknown names silently): `renderer-process-limit=1`, `process-per-site`, `disable-site-isolation-trials`,
`NetworkServiceInProcess2`, no `SpareRendererForSitePerProcess`, `AudioServiceOutOfProcess`, `ProcessorMetrics`,
`MediaRouter`, `OptimizationHints`, `HeavyAdIntervention`, `Translate`, `AutofillServerCommunication`, plus
`disable-print-preview`, `disable-speech-api`, `disable-breakpad`, `metrics-recording-only`, `disable-hang-monitor`,
32 MB disk cache → **3 processes / 1.6 GB RSS** (host, one renderer, storage service — Chromium 151 has no in-process
option for the latter). The host is started with `WINEDEBUG=-all` (`GTAN_CEF_WINEDEBUG` overrides), so Chromium's
processes never write to the Wine log; the launcher's `--debug` now sets
`WINEDEBUG=+timestamp,+pid,+tid,+seh,+threadname,+loaddll,+mscoree,-keyboard` (`GTAN_WINEDEBUG` overrides). The host
starts on `InitiatedConnect`, so it is warm by the time a resource opens a page. **`--debug` still costs frame rate;
normal play is `play.sh` without it.**

**Fourth step (4 Sept, late evening): the frame path and the keyboard.** In-game result was "everything works";
the owner then asked for real browser performance (loading windows/UI fast, a benchmark), a fix for Caps Lock typing a
character, and, later, 3D browsers. Done: dirty-rectangle frames end to end (host → shared memory → staging image →
persistent texture, `UpdateSubresource` in Present, no bitmaps), 4 ms frame pump, 60 fps default; keys that are not
characters send no `Char`, Caps Lock state goes through `ToUnicodeEx`; `eng/cef-harness.sh --bench 15 --size 1280x720`
(and `--gpu`) measures the path: software 59 frames/s at 1280x720, 0.39 ms per copy, host 4 % CPU; `--gpu` 60 frames/s
with ANGLE/D3D11 (DXVK) up in the host and a longest gap of 20 ms instead of 230 — GPU compositing works outside the game
process. The owner's `settings.xml` has `<CefGpu>true</CefGpu>` for the in-game test; if it misbehaves, set it back to
false. 3D browsers: design in `docs/CEF-UPGRADE.md`, roadmap item 5.

## How to test in game (what to ask the owner for)

The install is already synced (`eng/dev-sync-client.sh --cef`, and the launcher published into it with
`dotnet publish Launcher/GTANetwork.Launcher.csproj -r linux-x64 --self-contained -p:PublishSingleFile=true -o /gtanetwork`
from the container), auto-update is off (`setup.conf` `AUTO_UPDATE=0`, so `play.sh` never replaces the dev build with
a release). The owner runs `~/GTANetwork/play.sh` (`--debug` only to collect logs), joins the local server with the
`auth` resource; the login form must appear.
Expected lines:

```bash
grep -n "browser host\|CEF initialised\|created!\|End: https://auth\|host stderr\|EXCEPTION" ~/GTANetwork/logs/CEF.log | tail -20
tail -20 ~/GTANetwork/logs/CEF-host.log          # the host's own log (Cef.Initialize, browsers, frames)
tail -20 ~/GTANetwork/logs/CEF-chromium.log      # Chromium's log (Warning level; Verbose in debug mode)
pgrep -fa 'GTANetwork.CefHost|BrowserSubprocess'  # while the game runs: the host + Chromium subprocesses
```

If something is off: `CEF.log` says whether the host started (`Browser host started, pid`), whether Chromium
came up (`CEF initialised: Chromium 151…`), whether the browser exists (`Browser 1 created!`) and whether
frames arrive (debug mode: `Frame 420x480 …`). `Error.log` has managed exceptions of the client;
`~/steam-271590.log` (with `--debug`) is the Wine log. The harness passing against the installed host
(`eng/cef-harness.sh --install-cef`) means the host side is fine and the problem is in the game side
(`CEFManager`, overlay).

## The fast loop — dev container + harness, don't wait for CI

**Rule learned the hard way (4 Sept, evening):** a client built against the managed SHVDN *stub* (`Shv.NET/ref`)
compiles but does not run — the stub has `InputArgument` conversions the real C++/CLI build does not have
(`PoolObject`, `Player`, … vs one `INativeValue`), so `Main`, `Controls` and `Events` die with
`MissingMethodException` and the game loads forever. `eng/dev-build-client.sh` therefore copies the real
`ScriptHookVDotNet.dll` from the install into `Shv.NET/bin/` (git-ignored) and builds against it
(`UseRealShvdn=true`), like the Windows CI job. Check the "ScriptHookVDotNet reference: real build" line.

```bash
docker compose run --rm dev eng/dev-build-client.sh --sync   # client + host build (~10 s), synced into ~/GTANetwork
docker compose run --rm dev eng/dev-test.sh                  # the Linux CI checks (server smoke + bot integration)
eng/cef-harness.sh [--build] [--install-cef]                 # the browser host under Proton, no game
eng/cef-harness.sh --in-process | --appdomain | --alone --appdomain   # the in-process diagnosis modes
~/GTANetwork/play.sh --debug                                 # the owner tests in game
```

Compose reads `GTAN_INSTALL` from the local `.env` (gitignored). CI / a release is still needed only for a
player-facing release and for changes to the C++/CLI `ScriptHookVDotNet.dll` (Windows + MSVC).

## What is next

1. **In-game verification** of the browser host by the owner (above). Then cut `v0.2.0-alpha.6` via the
   `build.yml` workflow_dispatch **only when the owner asks**.
2. **Make the harness a CI gate**: the Windows job can run `CefHarness.exe --host <package>\cef\GTANetwork.CefHost.exe`
   against the assembled package — the acceptance test for the browser without a game.
3. **Performance** (the owner's stated goal): memory first — the renderer is 750 MB RSS under Wine and the storage
   service 390 MB; look at `--js-flags`, V8 heap limits, whether the storage service can be avoided (no cookies/DOM
   storage → `CefSettings.CachePath` empty?), and Chromium's idle CPU (host ~7 %, renderer ~4 % while showing a static
   page). Then `<CefGpu>true</CefGpu>` (GL/D3D now live in the host, not next to DXVK), dirty-rectangle uploads and
   reusing bitmaps/textures in the overlay instead of a new `Bitmap` per frame, then `OnAcceleratedPaint` with a shared
   D3D11 texture (zero copies). `docs/CEF-UPGRADE.md` has the plan.
4. **Privacy switches**: Chromium 151 still contacts Google at start-up (the harness's Chromium log shows
   `clients2.google.com/time`, `accounts.google.com/ListAccounts`, `www.google.com/async/…`). Add the usual
   set (`--disable-sync --metrics-recording-only --disable-domain-reliability
   --disable-client-side-phishing-detection`, matching `--disable-features`) to `Shared/CefLaunch.cs` and
   re-run the harness.
5. **Host robustness**: if the host dies mid-session, browsers freeze until the next game session
   (`CEFManager` logs it); a restart on demand would be nicer.
6. Then the roadmap: client on modern .NET, Linux GUI launcher (Avalonia), CEF connect/loading screen.

## Hard rules (do not break)

* **Branch discipline**: commit and push only to **`claude/modernize-deps-4d8uyn`**. Never push to a
  different branch without explicit permission.
* **Releases only via the `build.yml` workflow_dispatch** with input `release_tag` (a `-` in the tag makes
  it a pre-release, e.g. `v0.2.0-alpha.6`). **Never push git tags** (403). The `CHANGELOG.md` section for
  the version becomes the release body (alphas fall back to the base version section).
* **Do not open a PR unless asked. Do not create releases the owner did not ask for.**
* **ScriptHookV is closed-source and not redistributable** — never commit it.
* **GitHub access is scoped to `sashagoncharov19/freemode`.**
* **Attribution**: use the commit trailers the current session specifies. **No model identifiers in commits,
  PR text, code or docs** — chat only.
* The owner writes in **Ukrainian**; reply in kind.

## Key files

| Area | Files |
| --- | --- |
| Browser host (Chromium process) | `Subprocess/GTANetwork.CefHost/Program.cs` |
| Protocol & frames | `Shared/Cef/CefHostProtocol.cs`, `Shared/Cef/CefFrameBuffer.cs`, `Shared/CefLaunch.cs` |
| Game side of the browser | `Client/GUI/CEFManager.cs` (host process, events, frame pump, `Browser`, `BrowserInput`, `CefController`), `Client/GUI/CefClient.cs` (`OverlayRenderHandler`) |
| Overlay (DirectX) | `Client/GUI/DirectXHook/Hook/DXHookD3D11.cs`, `DirectXHook/DX11/DXOverlayEngine.cs`, `SwapchainHooker.cs` |
| Script engine bridge | `Client/Javascript/JavascriptHook.cs` (ClearScript, `createCefBrowser`, `waitUntilCefBrowserInit`, `loadPageCefBrowser`) |
| Harness | `Tools/CefHarness/Program.cs` (in-process modes), `Tools/CefHarness/HostTest.cs` (host protocol test), `eng/cef-harness.sh` |
| Resource file download | `Shared/ResourceFiles.cs` (`TryGetLocalPath`, used by the host too), `Client/Main/Network/Download.cs` |
| Settings | `Shared/PlayerSettings.cs` (`CefGpu`, `CefInProcessGpu`, `CefFrameRate`, `CefPreload`, `CEFDevtool`, `DebugMode`) |
| Build / packaging | `Directory.Build.props` (`CefSharpVersion`, `ClearScriptVersion`), `eng/package-client.ps1`, `eng/setup-linux.sh`, `Launcher/Deployment.cs` |
| Dev loop | `.devcontainer/`, `docker-compose.yml`, `eng/dev-build-client.sh`, `eng/dev-sync-client.sh`, `eng/dev-test.sh`, `eng/cef-harness.sh` |
| Docs | `docs/ROADMAP.md`, `docs/CEF-UPGRADE.md`, `docs/DEVCONTAINER.md`, `docs/SYNC.md`, `CHANGELOG.md` |

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
  **ScriptHookVDotNet** fork (C++/CLI, hosts the desktop CLR). Scripts run on their own threads and hand
  off to the game thread; a watchdog aborts a script that blocks for seconds ("Script 'X' is not
  responding! Aborting ..."), so nothing on a script thread may block for long.
* **Browser overlay**: pages render off-screen (CEF) and are drawn into a DirectX 11 overlay hooked onto
  the game's swap chain (SharpDX, under DXVK on Proton).
* **Script engine**: client-side JavaScript on **ClearScript 7.5 (V8 12)**.
* **Server / launcher / bot**: .NET 8, Linux-native. The **headless bot** (`Tools/GTANetwork.Bot`) speaks
  the real protocol (Lidgren + protobuf) and is how we test the server without the game (CI runs it).
* **Install layout** on the owner's machine: `~/GTANetwork/` with `bin/` (SHVDN.dll, native libs,
  ClearScript native), `bin/scripts/` (managed client DLLs), `cef/` (Chromium runtime), `server/`,
  `logs/`, and `play.sh` / `setup-linux.sh`.

## Current state (September 2026)

* **`master`**: `v0.1.1` — the **old** browser stack (CefGlue, Chromium 57, single-process) plus the
  fixes that made the `auth` login form work in game. **This is the last configuration verified working
  in game**: the form appears, the cursor moves, an account was registered.
* **`claude/modernize-deps-4d8uyn`** (current working branch, where all new commits go): the
  **modernisation** — browser moved to **CefSharp.OffScreen 151 (Chromium 151, multi-process)**,
  ClearScript 5.4.9 → 7.5, packages bumped, debug mode, and now the dev container. **Compiles, CI green,
  pre-releases `v0.2.0-alpha.1 … alpha.5` published.** No PR opened yet (owner will ask when ready).
* **The blocker**: on the new stack, **Chromium 151 crashes the game during `Cef.Initialize` under
  Proton**, before any browser window appears. See below.

## THE ACTIVE PROBLEM — CEF 151 crashes the game on init (Proton)

The new CefSharp/Chromium 151 browser has never finished initialising inside `GTA5.exe`. The game reaches
the server, then dies ~5 s after `Cef.Initialize` begins. It is a hard kill (no clean `ProcessExit` in
`Runtime.log`), and no browser window is ever shown.

### What the logs show (alpha.5, the latest)

* `logs/CEF.log`: `--> Cef.Initialize (software rendering, GL disabled, GPU service in-process, …)`,
  then the full switch list, then `--> Cef.Initialize still running after 5 s`, then **nothing** — no
  `Cef.Initialize returned`, no `CEF initialised`.
* `logs/CEF-chromium.log`: Chromium starts, enumerates the display, logs `viz_main_impl.cc … VizNullHypothesis
  is disabled`, then `scheduler_loop_quarantine_config … browser/viz-compositor`, then **stops**. It dies
  right as the **viz (display) compositor** thread comes up, in-process.
* Wine log (`~/steam-271590.log`, game pid found via `err:module:hacks_init`): a managed CLR exception
  `0xe0434352` with `info[0]=…80070002` (`FileNotFound`) around the crash.

### What is ruled out

* **Not GL / ANGLE / SwiftShader / Vulkan.** alpha.5 disabled GL entirely (`--use-gl=disabled
  --disable-software-rasterizer`, display-compositor-only mode) and the crash point did **not move** — same
  `viz-compositor` line as alpha.4. So it is not the GL backend colliding with DXVK.
* **Not a missing runtime file.** The `cef/` folder ships every file Chromium 151 needs — verified by
  building the client and listing `cef/`: `libcef.dll`, `icudtl.dat`, `v8_context_snapshot.bin`, the three
  `.pak`s, `d3dcompiler_47.dll`, `libEGL/libGLESv2`, `vk_swiftshader`, `vulkan-1`, `CefSharp.*`. So the
  `0x80070002` CLR exception is almost certainly normal **first-chance assembly-probing noise** from the
  `AssemblyResolve` handler, not the cause.
* **Not the script-thread watchdog** (that was alpha.3, already fixed): browsers are now created
  asynchronously via `CEFManager.RunWhenReady`, so `Cef.Initialize` no longer blocks the game thread. The
  game now stays alive until Chromium itself dies.

### The remaining hypothesis

Chromium 151's **multi-process / in-process viz compositor bring-up** does not survive inside `GTA5.exe`
under Wine/Proton (wedged next to DXVK, ScriptHookV, SHVDN). This looks structural, not a one-flag fix.

### Next options (in order of preference)

1. **External message pump** — `MultiThreadedMessageLoop = false` and drive `Cef.DoMessageLoopWork()` from
   our CEF thread (or `CefSharpSettings` external pump). Cheapest to try; may change how the compositor
   thread is scheduled. **Try first.**
2. **Separate GPU process** — `<CefInProcessGpu>false</CefInProcessGpu>` in `settings.xml` (no rebuild
   needed; the setting already exists). Note alpha.2/3 with a separate process died *earlier* (at display
   enumeration), so not promising, but it is one command for the owner to try.
3. **Separate CEF host process + shared frame transport** — run CefSharp in **our own external `.exe`**,
   not inside the game, and hand rendered frames back over shared memory / a shared D3D11 texture. This is
   the architecturally correct design for a game overlay, is **what the owner explicitly asked for**
   ("окремо процесом, бест практіс, щоб був реальний перфоманс"), and would sidestep the in-game init crash
   entirely because Chromium would start in a normal process context. Biggest effort; likely the real answer.

`docs/CEF-UPGRADE.md` has the full CEF mapping, settings, and the performance plan (dirty rectangles,
`OnAcceleratedPaint` shared textures).

## The fast loop — use the dev container, don't wait for CI

This was just added because the CI→release→install→test round trip was too slow. The managed client builds
on Linux, so:

```bash
# in the dev container (VS Code "Reopen in Container", or: docker compose run --rm dev bash)
eng/dev-build-client.sh --sync      # rebuild GTANetwork.dll (~10 s) and drop it into ~/GTANetwork
# then, on the host:
~/GTANetwork/play.sh --debug        # test in game
```

* `eng/dev-build-client.sh [-c Debug] [--sync [DIR]] [--cef]` — build the client; `--sync` copies it into
  an install (`$GTAN_INSTALL` or `~/GTANetwork`); `--cef` also refreshes `cef/` (only when CefSharp
  changed).
* `eng/dev-sync-client.sh [DIR]` — copy an already-built client into an install (host or container).
* `eng/dev-test.sh` — the Linux CI checks locally (server smoke test + bot integration).

**This means the next CEF experiments (option 1 and 3 above) can be tried by rebuilding `GTANetwork.dll`
and syncing — no new release per attempt.** Full guide: `docs/DEVCONTAINER.md`.

CI / a release is still needed only for: a player-facing release, and any change to the C++/CLI
`ScriptHookVDotNet.dll` itself (Windows + MSVC).

## What the owner wants next (their words → roadmap)

Ordered; each is its own branch + PR, shipped as an alpha first (see `docs/ROADMAP.md` "Next updates"):

1. **Finish the dependency modernisation** — i.e. get CEF 151 working in game (the blocker above). Then,
   as a separate 1–3 week step, the **client on modern .NET** (recompile the SHVDN shell with
   `/clr:netcore`, `AssemblyLoadContext`, .NET Desktop Runtime in the prefix — removes the most fragile
   Linux install step).
2. **Debug mode** — done (one switch keeps all diagnostics in code, on/off per build: Debug builds,
   `<DebugMode>` in settings.xml, or `GTAN_DEBUG=1`; launcher `--debug` also sets `PROTON_LOG=1`).
3. **Linux GUI launcher** — a graphical shell over `GTANetwork.Launcher` (**Avalonia**, one binary for
   Linux + Windows): server list, settings, install/update progress, log viewer, Play button.
4. **CEF connect & loading screen** — the server list / connect / loading flow as a **CEF page** drawn by
   the overlay, styled like a modern launcher, shown until the server is joined. **NativeUI stays** for the
   in-game settings and other menus (owner's clarification: "настройки на nativeui, а завантаження/лоадер на
   cef"). Needs the CEF upgrade working first.
5. **Performance / best-practice CEF** — the separate-process + shared-texture design (option 3 above),
   which the owner wants both for correctness and for real performance.

Further out: master server (issue #1), sync/perf rewrite, API parity, voice — all in `docs/ROADMAP.md`.

## Hard rules (do not break)

* **Branch discipline**: commit and push only to the designated branch — currently
  **`claude/modernize-deps-4d8uyn`**. Create it from the latest default branch if missing. Never push to a
  different branch without explicit permission.
* **Releases only via the `build.yml` workflow_dispatch** with input `release_tag` (a `-` in the tag makes
  it a pre-release, e.g. `v0.2.0-alpha.6`). **Never push git tags** — tag pushes are forbidden (HTTP 403).
  The `CHANGELOG.md` section for the version becomes the release body (alphas fall back to the base version
  section via the workflow's awk step).
* **Do not open a PR unless asked.** Do not create releases the owner did not ask for (once cancelled a
  stray stable release).
* **ScriptHookV is closed-source and not redistributable** — every player downloads it themselves; never
  commit it.
* **GitHub access is scoped to `sashagoncharov19/freemode`.** GitHub MCP tools (`mcp__github__*`) can
  drop/reconnect; load them via ToolSearch (`select:…`) before use.
* **Attribution**: the session provides the exact commit trailers (a `Co-Authored-By:` line and a
  `Claude-Session:` link) and the GitHub-comment footer — use whatever the current session specifies. **Do
  not put any model identifier into commits, PR text, code, or any repo artifact** — chat replies only.
* The owner writes in **Ukrainian**; reply in kind.

## How to test in game (what to ask the owner for)

The owner runs `~/GTANetwork/play.sh --debug` and sends logs. Useful requests:

```bash
grep -n "Cef.Initialize\|CEF switches\|CEF initialised\|Browser" ~/GTANetwork/logs/CEF.log | tail -15
tail -30 ~/GTANetwork/logs/CEF-chromium.log
# game pid from the Wine log, then its tail and any SEH exceptions:
pid=$(grep "err:module:hacks_init" ~/steam-271590.log | tail -1 | cut -d: -f2)
grep -n ":$pid:" ~/steam-271590.log | tail -60
grep -n ":$pid:.*seh:dispatch_exception" ~/steam-271590.log | tail -20
```

Other logs in `~/GTANetwork/logs/`: `Runtime.log` (our runtime, `Debug mode: on/off`, `ProcessExit`),
`Error.log` (managed exceptions), the SHVDN log (script watchdog aborts, `~/GTANetwork/…` — ask the owner
for its path).

## Key files

| Area | Files |
| --- | --- |
| CEF init & browser | `Client/GUI/CEFManager.cs` (init, flags, `RunWhenReady`, `Browser`), `Client/GUI/CefClient.cs` (render handler, resource handler, bridge) |
| Overlay (DirectX) | `Client/GUI/DirectXHook/Hook/DXHookD3D11.cs`, `DirectXHook/DX11/DXOverlayEngine.cs`, `SwapchainHooker.cs` |
| Script engine bridge | `Client/Javascript/JavascriptHook.cs` (ClearScript, `createCefBrowser`, `waitUntilCefBrowserInit`) |
| Resource file download | `Shared/ResourceFiles.cs`, `Client/Main/Network/Download.cs`, `Client/Networking/DownloadManager.cs` |
| Settings | `Shared/PlayerSettings.cs` (`CefGpu`, `CefInProcessGpu`, `CefFrameRate`, `CefPreload`, `DebugMode`) |
| Build / packaging | `Directory.Build.props` (versions: `CefSharpVersion`, `ClearScriptVersion`), `Client/GTANetworkClient.csproj`, `eng/package-client.ps1`, `eng/setup-linux.sh` |
| Dev loop | `.devcontainer/`, `docker-compose.yml`, `eng/dev-build-client.sh`, `eng/dev-sync-client.sh`, `eng/dev-test.sh` |
| Docs | `docs/ROADMAP.md`, `docs/CEF-UPGRADE.md`, `docs/DEVCONTAINER.md`, `docs/SYNC.md`, `CHANGELOG.md` |

## Suggested first move in a new session

Confirm the branch (`git status`), skim `CHANGELOG.md` "[0.2.0]", then attack the CEF blocker via the dev
container: implement **option 1 (external message pump)** in `Client/GUI/CEFManager.cs`, `eng/dev-build-client.sh
--sync`, and have the owner test with `play.sh --debug`. If that does not get past the `viz-compositor`
line, move to **option 3 (separate CEF host process)** — that is very likely the real fix and matches what
the owner wants for performance.

# Handoff — start here

Read this first if you are picking up the project in a new session. It is the live state: what we are
doing, where it hurts, and what to do next. The plan is `docs/PLAN.md`, decisions are in `docs/DECISIONS.md`, work
items in `docs/tasks/`, the layout in `docs/CODEMAP.md`, the way of working in `AGENTS.md` and `docs/agents/`. This
file is the "right now".

## The project in three lines

GTA Network / **FreeMode** is a revival of the 2016–2018 GTA V multiplayer mod. Goal: a modern,
Linux-first build (server, launcher and bot are native .NET 10 on Linux; the game runs through Proton)
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
* **Server / launcher / bot**: .NET 10, Linux-native. The **headless bot** (`Tools/GTANetwork.Bot`) speaks
  the real protocol (Lidgren + protobuf) and is how we test the server without the game (CI runs it).
* **CEF harness** (`Tools/CefHarness`, `eng/cef-harness.sh`): drives the browser host under Proton in the
  game's Wine prefix, without the game: start, browser, local page, pixels, page→game bridge, resize, close.
* **Install layout** on the owner's machine: `~/GTANetwork/` with `bin/` (SHVDN.dll, native libs,
  ClearScript native), `bin/scripts/` (managed client DLLs), `cef/` (browser host + Chromium runtime),
  `server/`, `logs/`, `resources/` (downloaded resource files), and `play.sh` / `setup-linux.sh`.

## Current state (5 September 2026, morning)

* **`master`**: `v0.1.1` — the **old** browser stack (CefGlue, Chromium 57, single-process). Last release verified working in game.
* **`claude/modernize-deps-4d8uyn`** — the **integration branch**; pushed. Contains the modernisation (CefSharp 151 in its own
  process, ClearScript 7.5, .NET 10), the agent framework (`AGENTS.md`, `docs/agents/`, `docs/tasks/`, `docs/PLAN.md`,
  `docs/DECISIONS.md`, `docs/CODEMAP.md`, the code graph) and the first merged tasks: **T-001** .NET 10 (#5), **T-004**
  TypeScript typings (#6), **T-012** CEF connect loader (#7), **T-006** Bun runtime for TypeScript server resources
  (#8 bridge benchmark, #9 the runtime itself), the browser host's idle timer counted from readiness (#10). Tasks run as
  `task/T-NNN-*` branches with one PR each (D-11); the owner asked the agent to merge PRs whose CI is green (5 Sept).
  No PR to `master` yet; no alpha.6 yet.
* **The owner's install (`~/GTANetwork`)** holds the integration build: .NET 10 launcher and server (the local server runs on
  it), the client + browser host with the texture ring, hitch diagnostics, idle exit and the connect loader, `ui/loader`.
* **Awaiting the owner in game** (`play.sh --debug`): T-000 (texture ring: typing reacts at once; `[HITCH]` lines vs
  `hitch-monitor.log`; the host stops 60 s after the last browser and returns for the next page) and T-012 (the loader shows
  about a second after "connect" and fades before the `auth` form; `Runtime.log`: `loader: shown …` / `loader: hidden after N ms`).
* **Merged 5 Sept**: T-008 typed RPC (#11): `API.rpc.call` on the client, `API.registerRpc` / `gtan.rpc.register` on the server,
  `gtan.rpc.call` in CEF pages, `API.callClient`; the `auth` form and `freeroam` use it; bot round trips in `eng/integration-test.sh`.
  The owner's install was synced afterwards (client, browser host, `ui/`, server with `runtime/`, bot); the running local server
  must be restarted to speak RPC.
* **In PR**: T-005 client scripts in TypeScript (`task/T-005-client-typescript`): `Server/Managers/TypeScriptBundler.cs` bundles
  `client/index.ts` with Bun at resource start (IIFE, hash cache, optional `tsc`); freeroam's client is TypeScript now. The owner's
  server needs Bun for it: the deploy copies the container's Bun 1.4.1 into `~/GTANetwork/server/runtime/bun/bun`.
* **Merged 5 Sept**: T-013 the main menu on CEF (#12, `ui/menu`, `<CefMenu>` default true): servers (favourites, recent, LAN, master
  list), direct connect, settings, quit; NativeUI stays on the pause key and is the fallback. Synced into the owner's install; the
  in-game check is pending (task status "needs owner").
* The owner's 5 Sept session lagged because the machine swapped (monitor: 90–133 MB/s out, up to 6.5 s of memory stall per
  second, Chromium took 42 s to start); the loader did not show for that reason and the idle timer then stopped the host as
  soon as it came up (fixed in #10). Test on a quiet machine (Firefox and the desktop app closed). **Open decisions**: Q-07
  hosting of the master list (blocks T-011), Q-03, Q-05, Q-06, Q-08, Q-10, Q-11, Q-13.
* Everything except the game is verified by `eng/dev-test.sh` (CI checks) and `eng/cef-harness.sh` (browser host, both frame
  modes, latency, loader page); the bridge numbers are in `docs/PLAN.md` E-04.

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

**Sixth step (4 Sept, night): memory.** The in-game run with shared textures worked but the machine swapped again
(4 GB more swapped out during the session; `full avg60=12`). Measured with the harness: Chromium was 1.5 GB PSS
(software) / 1.77 GB (GPU) for three processes because **Wine read every 512-byte-aligned DLL into anonymous memory
per process** (`libcef.dll` alone: 272 MB × 3, resident, unshared). `eng/pe-realign.py` page-aligns the PE files;
now **0.86 GB / 1.12 GB**; wired into build, sync, packaging and the Linux installer. The renderer's remaining ~300 MB
zero-filled region is an open question (details and everything ruled out: `docs/CEF-UPGRADE.md`, "Memory under Wine").
On this 15 GB machine with Firefox and Claude Desktop open, GTA V + Chromium is still close to the edge: if it swaps
again, `<CefGpu>false</CefGpu>` saves ~270 MB.

**Fifth step (4 Sept, night): shared textures.** Committed the above (three commits), then built the zero-copy path:
with the GPU on, browsers are created with shared textures; the host duplicates Chromium's D3D11 texture handles into
the game process and sends `texture` events; the overlay opens each handle once (`OpenSharedResource1` on the game's
DXVK device), `CopyResource`s into its persistent texture and draws it; stale handles are released; a failure to open
falls back to CPU frames for the session. Verified with the harness (`--shared-texture`: open + read-back across
processes OK; `--shared-texture --bench 15`: 60 GPU copies/s at 0.027 ms, no CPU). **Ran in game** — see the seventh step.

**Seventh step (4 Sept, night): "the inputs lag" — the shared-texture lifetime.** The owner's in-game run with the
shared textures worked but typing and hovering reacted late, "as if CEF lags". `CEF-host.log` (20:48) showed eleven
"shared texture … -> game handle" lines for one static login page and, 45 s in, the game failing to open handle
`0x2418` with `E_INVALIDARG` and falling back to CPU frames. Cause: CefSharp documents that the handle
`OnAcceleratedPaint` passes is **only valid inside the callback** — it is a fresh duplicate per paint of one texture
of a pool Chromium recycles, and the buffer is rewritten after the callback returns. The fifth step cached those
handles by value on both sides: a recycled handle value pointed at another pool texture (stale frame → a keystroke
appeared with some later paint), and the game closed handles it thought unused while the host still mapped them
(→ `E_INVALIDARG`). Fix (`Subprocess/GTANetwork.CefHost/TextureRelay.cs`): the host owns a **ring of 4 shared
textures per browser** on a D3D11 device of its own (SharpDX, DXVK under Proton), copies each paint into the next
slot inside the callback, and a publisher thread announces the slot (`texture` event) only once an event query says
the GPU copy has executed; the ring's handles are duplicated into the game once and announced by a new `textures`
event (protocol version 2; re-announced after a resize, and sent without handles when the host cannot relay — then
the game re-creates the browser with CPU frames). The game (`SharedTextureSurface`, `OverlayRenderHandler`,
`DXOverlayEngine`) opens each slot once and closes handles only when a `textures` event replaces them or the browser
goes; the time-based eviction is gone. Harness: `--shared-texture` passes (4 stable handles, read-back OK),
`--shared-texture --bench 8 --size 1280x720`: 60 texture events/s, 60 copies/s at 0.020 ms, host 5 % CPU; the
harness now measures **latency** from an `eval` to the frame showing it: 8.2–8.7 ms median over the ring, 3.1 ms
over shared memory (software) for a 420x480 page. Host process: 210 MB PSS with the extra device. Ran in game (21:19–21:23):
`CEF.log` "Texture ring 420x480: 0x248C, …", no fallback; the owner: "все працює адекватно".

**Eighth step (4 Sept, night): micro-freezes — whose?** The owner saw occasional micro-freezes while playing and wants
to know whether they are ours. The logs of that session (`--debug`) say no: after the connect burst (resource start,
JS engine, host start: Main 118 ms, MessagePump 178 ms, JavascriptHook 338 ms — one-off, at 21:20:59–21:21:00) no
script tick exceeded 17.5 ms in 2.5 minutes of play (SHVDN logs every tick ≥ 20 ms as "held the game thread"); the
overlay took ≤ 0.9 ms per frame; the browser was closed after login (21:21:19), so Chromium sat idle for the whole
gameplay; no managed exception reached `Error.log`; DXVK's "Failed to open shared NT handle" warnings in the Wine log
sit next to D3D11 *device creation* lines of GTA5.exe and Rockstar's launcher (a DXVK 3.0 init probe), not near our
texture opens. What did move: the machine swapped — 457 MB swapped *in* and 1.7 GB out between 20:55 and 21:30 with 4 GB
in swap, memory-pressure stall time growing (`/proc/pressure/memory`); Firefox (2.5 GB), Claude Desktop (1.1 GB) and
Steam (0.6 GB) hold the rest of the 15 GB next to GTA V. Swap-ins during play are the classic micro-freeze; DXVK
pipeline compilation (DXVK 3.0.2, "Found cache file") and the game's streaming are the other usual suspects. Since a
log can only *exonerate* per hitch, the tooling now does that: the Present hook logs every frame over 50 ms as
`[HITCH]` in `Runtime.log` (millisecond timestamp, our overlay's share, GC gen0/1/2 counts, browser frames in the last
second, browsers open; the debug-mode 10 s profile counts frames over 33/50/100 ms), and the launcher's `--debug` on
Linux runs `Launcher/HitchMonitor.cs`: one line per second in `logs/hitch-monitor.log` with swap in/out, memory-stall
ms, MemAvailable, GTA5.exe RSS/swapped/major faults, Chromium RSS, CPU busy/MHz/°C, GPU MHz/°C/busy/throttle reasons.
Reading a hitch: `[HITCH] 21:22:14.302: frame took 180 ms (our overlay 0.10 ms …)` + the monitor's 21:22:14 line
with `swap in 40000 KB/s` or `mem stall 300 ms` = the machine; a GPU clock drop = thermals; nothing moved and SHVDN
has no "held the game thread" at that second = the game (shaders, streaming). Also done, as the memory mitigation
that is ours to make: `<CefIdleExitSeconds>` (default 60) — the game stops the browser host when no browser has
existed for a minute (Chromium's ~0.9 GB go back), and the next browser starts a fresh host (~1 s); a host that dies
mid-session is replaced too: the open browsers are created again on a new host with their pages (three restarts per
session at most — plan item "host robustness", done). `CEF.log` shows "No browser for 60 s: stopping the browser host …" and later
"Starting the browser host" again when a page opens.

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

0. **Tasks, in order** (`docs/PLAN.md` §4, D-12): merge T-005 when green → **T-007** freeroam in TypeScript (server side; the
   client side is done by T-005) → **T-009** session encryption → **T-010** launcher GUI → **T-011**
   master list (needs Q-07; the CEF menu then gets ping and player counts from it). Each on its own `task/T-NNN-*` branch from the
   integration branch, one PR, `eng/dev-test.sh` green.
1. **In-game run by the owner** (quiet machine, `play.sh --debug`): (a) hitches — read `[HITCH]` lines in `Runtime.log`
   against `hitch-monitor.log` and SHVDN's log, as described above; (b) the idle exit — after login the host must stop a
   minute after the last browser closed ("No browser for 60 s" in `CEF.log`, counted from `CEF initialised`) and a later
   page must start it again and show; (c) the connect loader (`loader: shown` / `loader: hidden after N ms` in `Runtime.log`);
   (d) RPC (merged, synced): a wrong password in the `auth` form shows "Wrong name or password." in the form;
   (e) after T-013 is merged and synced: the CEF main menu at game start (`menu: shown` / `menu: page ready after N ms` in
   `Runtime.log`), the local server under LAN, connect from it, the menu back after a disconnect, ★ persisted in `settings.xml`.
   If the GPU path misbehaves, `<CefGpu>false</CefGpu>` is the safe setting. Then cut `v0.2.0-alpha.6` via the `build.yml`
   workflow_dispatch **only when the owner asks**.
2. **Make the harness a CI gate**: the Windows job can run `CefHarness.exe --host <package>\cef\GTANetwork.CefHost.exe`
   against the assembled package — the acceptance test for the browser without a game.
3. **Performance** (the owner's stated goal) — done so far: dirty-rectangle frames, 60 fps, GPU in the host, the
   shared-texture ring (zero CPU per frame), page-aligned PE files (Chromium 2.9 GB → 0.86/1.12 GB). Open: the
   renderer's ~300 MB zero-filled region (`docs/CEF-UPGRADE.md`, "Memory under Wine"), the storage-service process
   (117 MB; Chromium 151 has no in-process option), and whether `<CefGpu>true</CefGpu>` should become the default
   (decided by the owner's in-game runs: it costs ~270 MB and a few ms of latency on small static pages, wins on
   big/animated ones).
4. **Privacy switches**: Chromium 151 still contacts Google at start-up (the harness's Chromium log shows
   `clients2.google.com/time`, `accounts.google.com/ListAccounts`, `www.google.com/async/…`). Add the usual
   set (`--disable-sync --metrics-recording-only --disable-domain-reliability
   --disable-client-side-phishing-detection`, matching `--disable-features`) to `Shared/CefLaunch.cs` and
   re-run the harness.
5. ~~**Host robustness**~~ — done (eighth step): a dead host is replaced and its browsers re-created, three times per
   session at most; the idle exit reuses the same path.
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
| Browser host (Chromium process) | `Subprocess/GTANetwork.CefHost/Program.cs`, `TextureRelay.cs` (the shared-texture ring) |
| Protocol & frames | `Shared/Cef/CefHostProtocol.cs`, `Shared/Cef/CefFrameBuffer.cs`, `Shared/CefLaunch.cs` |
| Game side of the browser | `Client/GUI/CEFManager.cs` (host process, events, frame pump, `Browser`, `BrowserInput`, `CefController`), `Client/GUI/CefClient.cs` (`OverlayRenderHandler`) |
| Overlay (DirectX) | `Client/GUI/DirectXHook/Hook/DXHookD3D11.cs`, `DirectXHook/DX11/DXOverlayEngine.cs` (incl. the shared-texture copy), `DirectXHook/Hook/Common/SharedTextureSurface.cs`, `SwapchainHooker.cs` |
| Script engine bridge | `Client/Javascript/JavascriptHook.cs` (ClearScript, `createCefBrowser`, `waitUntilCefBrowserInit`, `loadPageCefBrowser`) |
| Harness | `Tools/CefHarness/Program.cs` (in-process modes), `Tools/CefHarness/HostTest.cs` (host protocol test), `eng/cef-harness.sh` |
| Resource file download | `Shared/ResourceFiles.cs` (`TryGetLocalPath`, used by the host too), `Client/Main/Network/Download.cs` |
| Settings | `Shared/PlayerSettings.cs` (`CefGpu`, `CefSharedTexture`, `CefInProcessGpu`, `CefFrameRate`, `CefPreload`, `CefIdleExitSeconds`, `CefLoader`, `CEFDevtool`, `DebugMode`) |
| Hitch diagnostics | `Client/GUI/DirectXHook/Hook/DXHookD3D11.cs` (`RecordPresentCost`: `[HITCH]` lines), `Launcher/HitchMonitor.cs` (`--debug` system monitor) |
| Build / packaging | `Directory.Build.props` (`CefSharpVersion`, `ClearScriptVersion`), `eng/package-client.ps1`, `eng/setup-linux.sh`, `Launcher/Deployment.cs` |
| Dev loop | `.devcontainer/`, `docker-compose.yml`, `eng/dev-build-client.sh`, `eng/dev-sync-client.sh`, `eng/dev-test.sh`, `eng/cef-harness.sh` |
| Docs | `docs/ROADMAP.md`, `docs/CEF-UPGRADE.md`, `docs/DEVCONTAINER.md`, `docs/SYNC.md`, `CHANGELOG.md` |

# The embedded browser: from CEF 3.2987 to CefSharp 151 in its own process

## Status (September 2026)

Done in code on the `claude/modernize-deps-4d8uyn` branch, verified outside the game with the harness, waiting
for the in-game run:

* **Before**: `libs/Xilium.CefGlue.dll` + `libs/cef/` = CEF 3.2987.1590 (Chromium 57, March 2017), started
  in *single-process mode* inside GTA5.exe (`--disable-gpu`), pages talking to the client script through a V8
  handler in the same process.
* **Now**: `CefSharp.OffScreen` 151.3.240 (Chromium 151) from NuGet, running in **its own process,
  `cef\GTANetwork.CefHost.exe`** (`Subprocess/GTANetwork.CefHost`). The game starts the host, talks to it over
  its stdin/stdout (`Shared/Cef/CefHostProtocol.cs`) and reads the browsers' pixels from shared memory
  (`Shared/Cef/CefFrameBuffer.cs`). Renderer and GPU work run in `CefSharp.BrowserSubprocess.exe` processes
  of the host. The whole runtime lives in `<install>\cef` (~350 MB unpacked: the output folder of the host
  project). Nothing of CefSharp or libcef is loaded into GTA5.exe.
* **JavaScript runtime** alongside: ClearScript 5.4.9 → 7.5.1 (V8 12), also from NuGet
  (`ClearScriptV8.win-x64.dll` next to the client assemblies; `HostSettings.AuxiliarySearchPath` points at the
  scripts folder because ScriptHookVDotNet shadow-copies assemblies).

## Why a separate process (the AppDomain finding)

Pre-releases `0.2.0-alpha.1 … alpha.5` ran CefSharp inside the game and died during `Cef.Initialize` under
Proton, before any browser appeared, whatever the switches. The cause is structural, not a flag:
**CefSharp is C++/CLI and only works in the default AppDomain, while ScriptHookVDotNet runs the client in a
second AppDomain.** Chromium's threads have no managed context; when one of them enters CefSharp's managed
code the CLR picks the default domain, where our assemblies are neither loaded nor resolvable, and the
resulting managed exception on a Chromium thread (`0xe0434352` in the Wine log) is unhandled and kills the
process. `Tools/CefHarness` reproduces it in a second (`eng/cef-harness.sh --alone --appdomain`: `Cef.Initialize`
never returns) and shows the same Chromium starting fine in a plain process (`--in-process`). The old CefGlue
binding was pure P/Invoke with domain-bound delegates, which is why it worked in the script domain.

A separate browser process is the fix and the architecture we wanted anyway: Chromium cannot take the game
down, its GPU/GL work never shares a process with DXVK, and the frame transport is the basis for the
shared-texture step below.

## Why CefSharp and not CefGlue

OutSystems' CefGlue (the maintained CefGlue) ships no .NET Framework assets (`CefGlue.Common` 120.x resolves
for net48 with no compile items), and the in-game client has to stay .NET Framework 4.8 as long as
ScriptHookVDotNet hosts the desktop CLR. CefSharp targets net462+ and ships the off-screen mode, the subprocess
and the redistributable as packages. It is also what the 2016 GTA Network started with (`ScreenshotOrNull`
remnants in the old code).

## How the pieces map

| Old (CefGlue, in the game) | New (CefSharp, in the host) | Where |
| --- | --- | --- |
| `CefRuntime.Load/Initialize`, `SingleProcess = true` | The game starts `cef\GTANetwork.CefHost.exe`; the host calls `Cef.Initialize(CefSettings)` (MultiThreadedMessageLoop) and answers `ready` | `CEFManager.StartHost`, `Program.InitializeCef` |
| `CefRenderHandler.OnPaint` (pointer wrapped) | `IRenderHandler.OnPaint` in the host copies the BGRA buffer into a shared-memory `CefFrameBuffer`; the game's frame pump copies new frames into the overlay bitmap | `HostedBrowser.FrameWriter`, `OverlayRenderHandler.Pump` |
| `CefSchemeHandlerFactory` for `https://<resource>/` | `RequestHandler` → `ResourceRequestHandler` → `ResourceHandler.FromFilePath/FromByteArray` in the host, paths checked with `ResourceFileDownloader.TryGetLocalPath` against `--resource-root`; served HTML gets the bridge shim as its first script | `LocalResourceRequestHandler` (host) |
| `CefRenderProcessHandler.OnContextCreated` registering `resourceCall` as a V8 function | The shim (`resourceCall` posts `CefSharp.PostMessage({type, name, args})`) injected into served HTML, from `OnContextCreated` and from `FrameLoadStart`; the host receives `JavascriptMessageReceived` and forwards a `jsMessage` event; the game runs the client script function on the script thread | `ResourceBridgeInjector`, `HostedBrowser.OnJavascriptMessage`, `Browser.OnHostEvent`, `BrowserJavascriptCallback.Invoke` |
| `browser.GetHost().SendMouse*/SendKeyEvent` | `BrowserInput` (same method names) sends `mouseMove`/`mouseClick`/`mouseWheel`/`key` commands; the host maps them onto `IBrowserHost` | `CefController`, `Program.Dispatch` |
| `CefSettings.IgnoreCertificateErrors = true` | off (local pages do not need it) | |

**Behaviour change for page authors**: `resourceCall` used to return the value of the client function; pages
now live in another process, so it returns nothing (fire and forget). `resourceEval` likewise. `browser.call()`
and `browser.eval()` from the client script are unchanged (`ExecuteScriptAsync`). New pages can also use
`gtan.call(...)` / `gtan.eval(...)`.

**Settings** (`settings.xml`, passed to the host as command-line options): `<CefGpu>true</CefGpu>` lets Chromium
use the GPU (default off = Chromium's display-compositor-only mode, `--disable-gpu --disable-gpu-compositing
--use-gl=disabled --disable-software-rasterizer`; off-screen pages are composited in software anyway — now that
Chromium is out of the game process this is worth revisiting), `<CefInProcessGpu>false</CefInProcessGpu>` moves
the GPU service out of the host into a `CefSharp.BrowserSubprocess.exe --type=gpu-process` (default: a thread
inside the host), `<CefFrameRate>30</CefFrameRate>` paints per second, `<CEFDevtool>true</CEFDevtool>` opens the
remote debugger on port 9222 (http://localhost:9222), `<CefPreload>` starts the host at game start, `<DebugMode>`
turns on the verbose logs and the V8 inspector of ClearScript. The switch list itself is `Shared/CefLaunch.cs`; every
feature and switch name in it was checked against the strings of `libcef.dll` (Chromium ignores unknown names
silently). Footprint with it: host ~460 MB, one renderer ~760 MB, storage service ~390 MB RSS under Wine.

**Logs**: `logs\CEF.log` (the game side: host start and arguments, `CEF initialised` with the versions, browser
creation, page loads, page console output, `resourceCall`s, the first lines of the host's stderr),
`logs\CEF-host.log` (the host: `Cef.Initialize`, switches, browsers, frame buffers, local file serving),
`logs\CEF-chromium.log` (Chromium's own log, verbose in debug mode).

## Packaging

The CefSharp NuGet targets copy the Chromium runtime, `CefSharp.BrowserSubprocess.exe` and
`CefSharp.Core.Runtime.dll` next to `GTANetwork.CefHost.exe` in `Subprocess\GTANetwork.CefHost\bin\<cfg>\net48\`;
`eng/package-client.ps1` ships that folder as `cef\` without CefSharp's `.pdb`/`.xml` and with a subset of
locales (en-US, uk, ru, pl, de, fr, es, pt-BR, tr, it, nl, cs, ro, hu). The in-game client in `bin\scripts`
has no CefSharp dependency any more; `eng/dev-sync-client.sh` syncs the host's managed files into an install's
`cef\` (and the whole runtime with `--cef`). `libs/cef` and `libs/Xilium.CefGlue.dll` are gone from the
repository (144 MB less).

Requirements in the Proton prefix: .NET Framework 4.8 (the host and the subprocess are .NET Framework
executables) and the VC++ 2022 x64 runtime (C++/CLI parts) — both already installed by `setup-linux.sh`.

## Performance path

1. ~~Software paint copy with a bitmap per frame~~ → **done (4 Sept)**: the host writes only the dirty rectangle of each
   paint into the shared frame buffer (the buffer always holds a complete frame; the rectangle travels in the header);
   the game's frame pump (every 4 ms) copies only that rectangle into a staging image (`CefFrameStager`, an
   `IDynamicSurface`), and the overlay uploads the accumulated rectangle into a **persistent** `Default`-usage texture
   with `UpdateSubresource` on the immediate context inside Present (`DXImage.UpdateRegion`). No bitmaps, no texture
   re-creation, a frame reaches the screen within a few milliseconds. Missed or torn frames fall back to a full copy.
   `<CefFrameRate>` defaults to 60 now. `eng/cef-harness.sh --bench 15 --size 1280x720` measures frames/s delivered,
   copy cost and CPU of the host and its subprocesses on an animated page.
2. **GPU in the host — works** (measured 4 Sept with `eng/cef-harness.sh --bench 15 --size 1280x720`, animated page,
   Proton Experimental, NVIDIA): software rendering 59.2 frames/s delivered, 0.39 ms per copy, host 4 % CPU,
   subprocesses 8 %, longest gap 230 ms; `--gpu` 60.1 frames/s, same CPU, longest gap 20 ms, ANGLE on D3D11 (DXVK)
   initialised, canvas accelerated. `<CefGpu>true</CefGpu>` is therefore worth using; in-game verification by the owner
   decides whether it becomes the default.
3. **Shared textures — done (4 Sept)**: with `<CefGpu>true</CefGpu>` (and `<CefSharedTexture>true</CefSharedTexture>`,
   the default) browsers are created with `WindowInfo.SharedTextureEnabled`; Chromium renders into D3D11 textures and
   `IRenderHandler.OnAcceleratedPaint` gives the host their NT handles. The host duplicates each handle into the game
   process (`DuplicateHandle`, once per texture of Chromium's pool) and sends a `texture` event per frame; the overlay
   opens each handle once on the game's device (`Device1.OpenSharedResource1`), copies the texture GPU-side into the
   element's persistent texture (`CopyResource`, 0.027 ms measured) and draws that. **No CPU work per frame at all.**
   Handles Chromium stops using are released after a few seconds (it re-creates its pool now and then: 14 textures in
   a 15 s benchmark); on a browser's close all its handles are closed. If a handle cannot be opened, the browser is
   created again with CPU frames and no later browser asks for shared textures (`CEF.log`: "shared textures
   unavailable"). Measured with `eng/cef-harness.sh --shared-texture --bench 15 --size 1280x720` on Proton
   Experimental / DXVK / RTX 4050: 60 texture events/s, 60 GPU copies/s, host 4 % CPU, Chromium 9 % — the textures
   are `B8G8R8A8_UNorm`, `Shared | SharedNthandle`, no keyed mutex, and open fine across processes (both sides DXVK).
4. `WindowlessFrameRate` per browser (`API.setCefFramerate`), 60 default.
5. Next: what is left is Chromium's own cost (renderer ~9 % CPU for an animated 720p page) and, for 3D browsers,
   drawing the texture with a world transform (below).

## Memory under Wine (measured 4 Sept, PSS of an idle 420x480 page, Proton Experimental)

| Chromium processes | before | after |
| --- | --- | --- |
| 8 processes (two renderers, network, storage and metrics utilities) | 2.9 GB RSS | — |
| 3 processes after the switch list (`Shared/CefLaunch.cs`) | 1.51 GB PSS (software), 1.77 GB (GPU) | — |
| … and page-aligned PE files (`eng/pe-realign.py`) | — | **0.86 GB** (software), **1.12 GB** (GPU) |

**Why the alignment matters.** Wine maps a PE image from disk only when its sections sit at page-aligned file
offsets. Chromium's DLLs are linked with `FileAlignment 512`, so Wine read `libcef.dll` (272 MB) into anonymous memory
in *every* process — fully resident, nothing shared, nothing paged on demand. `eng/pe-realign.py` rewrites the file
layout to a 4096-byte alignment (sections keep their virtual addresses and bytes; the checksum is cleared and an
Authenticode signature dropped); Windows loads such files just as well. It runs in `eng/package-client.ps1` (release
packages), `eng/dev-build-client.sh` (the host's build output, so the harness measures what the game gets),
`eng/dev-sync-client.sh` (the install) and, for installs of older packages, on update in `eng/setup-linux.sh`.

**GPU mode** costs ~270 MB in the host (the DXVK/ANGLE device); the shared-texture path itself is free.

**Open question.** The renderer is still ~500 MB PSS for `about:blank`, ~300 MB of which is one anonymous region of
zero-filled, resident pages in the Unix mmap area (0x7f…), present 4 s after start and stable. Ruled out with the
harness: transparent huge pages (`prctl(PR_SET_THP_DISABLE)` on the whole tree: same size, `AnonHugePages 0`), system
fonts (a minimal fontconfig: same), the .NET GC (`COMPlus_GCgen0size`: same), V8 (`js-flags=--jitless`, small heaps:
same), PartitionAlloc BRP/thread cache features (same). The utility process of the same exe (CLR + libcef, no Blink/V8)
is 117 MB, so the ~390 MB delta is Blink/V8 start-up plus this region. `eng/cef-harness.sh --host-switch k=v` passes
extra Chromium switches to the host for further experiments; Wine's `+virtual` channel (`GTAN_CEF_WINEDEBUG`) shows
no single large commit, so it is written in small pieces, or from the Unix side.

## Input

The game gets key events from ScriptHookVDotNet as key codes; `CefController` sends CEF a `RawKeyDown` (WM_KEYDOWN)
and, for keys that type something, a `Char` (WM_CHAR) translated with `ToUnicodeEx` and the real keyboard state
(Shift, AltGr, and Caps Lock as its toggle bit — the old code upper-cased by hand, and sent a character for Caps Lock
itself). Modifier, lock, function and navigation keys and Ctrl+letter shortcuts send no `Char`.

## 3D browsers (later)

"CEF in the world" — a page on a TV screen, a billboard, a UI attached to an entity. Two routes:

* **Own quad in the D3D11 hook** (the RAGE Multiplayer way): the browser texture already lives in our overlay; draw
  it as a textured quad with a world→view→projection transform from the game camera (`GET_GAMEPLAY_CAM_COORD/ROT`,
  FOV), and depth-test it against the game's depth buffer so it hides behind geometry. Needs the depth buffer: hook
  `OMSetRenderTargets`/`ClearDepthStencilView` to catch the main depth-stencil view before Present. Builds on the
  existing overlay and on the shared texture step above (the same texture, one more shader).
* **Game render targets** (`REGISTER_NAMED_RENDERTARGET`, `LINK_NAMED_RENDERTARGET`, `SET_TEXT_RENDER_ID`,
  `DRAW_SPRITE`): the game draws our image onto props that expose a script render target (TVs, monitors). Vanilla
  natives cannot create a texture dictionary at runtime; that needs the grcTexture factory (what FiveM's
  `CREATE_RUNTIME_TXD` does) — reverse engineering of the current build's texture code.

A cheap stepping stone that needs neither: "3D-projected 2D" — `WORLD3D_TO_SCREEN2D` of an anchor, the browser
placed and scaled there each frame by the client script (`setCefBrowserPosition/Size`), no occlusion.

## Verification

* **Harness** (`Tools/CefHarness`, `eng/cef-harness.sh`): starts the host under Proton in the game's prefix (or
  on Windows directly), creates a local-mode browser, serves `https://harness/ui/index.html` from a resources
  folder, reads the pixels from the shared frame buffer, waits for the page's `resourceCall`, resizes, closes.
  `--install-cef` tests the host of an installed game. `--in-process`, `--appdomain` and `--alone --appdomain`
  reproduce the in-game failure of the in-process design.
* CI: the Linux job compiles everything; the Windows job builds the package and fails if
  `cef\GTANetwork.CefHost.exe`, `cef\libcef.dll`, `CefSharp.BrowserSubprocess.exe` or `CefSharp.Core.Runtime.dll`
  are missing. Running the harness against the assembled package on the Windows job is the next step.
* In game (the acceptance test): join a server with the `auth` resource, the login form appears, typing and
  clicking work, `CEF.log` shows `Browser host started`, `CEF initialised: Chromium 151...`, `Browser 1 created!`,
  `End: https://auth/ui/index.html, 200`, and `pgrep -fa GTANetwork.CefHost` shows the host and its
  `CefSharp.BrowserSubprocess.exe` processes.

## Risks

* The host must stay a Windows-subsystem exe (`WinExe`): as a console exe, Wine's `conhost` opens a visible
  console window for it, which takes the foreground from the game (system cursor, GTA V's background frame limiter).

* Chromium 151 still contacts Google at start-up (time source, account consistency): add the usual privacy
  switches to `Shared/CefLaunch.cs` and re-run the harness.
* If the host dies mid-session the browsers freeze until the next game session (the game logs it); a restart on
  demand is not implemented yet.
* The x86 runtime package is downloaded by NuGet as a dependency of `CefSharp.Common` although nothing uses it.
* `EnableDebugging` of ClearScript and CEF's remote debugging both default to port 9222; only one should be on.

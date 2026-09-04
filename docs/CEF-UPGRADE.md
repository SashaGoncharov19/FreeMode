# The embedded browser: from CEF 3.2987 to CefSharp 151

## Status (September 2026)

Done in code on the `claude/modernize-deps-4d8uyn` branch, waiting for the in-game run:

* **Before**: `libs/Xilium.CefGlue.dll` + `libs/cef/` = CEF 3.2987.1590 (Chromium 57, March 2017), started
  in *single-process mode* inside GTA5.exe (`--disable-gpu`), pages talking to the client script through a V8
  handler in the same process.
* **Now**: `CefSharp.OffScreen` 151.3.240 (Chromium 151) from NuGet. GTA5.exe is the browser process; renderer
  and GPU run in `CefSharp.BrowserSubprocess.exe` processes. The whole runtime lives in `<install>\cef`
  (~350 MB unpacked, copied there by the client build via `CefSharpTargetDir`).
* **JavaScript runtime** alongside: ClearScript 5.4.9 → 7.5.1 (V8 12), also from NuGet
  (`ClearScriptV8.win-x64.dll` next to the client assemblies; `HostSettings.AuxiliarySearchPath` points at the
  scripts folder because ScriptHookVDotNet shadow-copies assemblies).

## Why CefSharp and not CefGlue

OutSystems' CefGlue (the maintained CefGlue) ships no .NET Framework assets (`CefGlue.Common` 120.x resolves
for net48 with no compile items), and the in-game client has to stay .NET Framework 4.8 as long as
ScriptHookVDotNet hosts the desktop CLR. CefSharp targets net462+ and ships the off-screen mode, the subprocess
and the redistributable as packages. It is also what the 2016 GTA Network started with (`ScreenshotOrNull`
remnants in the old code).

## How the pieces map

| Old (CefGlue) | New (CefSharp) | Where |
| --- | --- | --- |
| `CefRuntime.Load/Initialize`, `SingleProcess = true` | `Cef.Initialize(CefSettings)` on a dedicated thread, `BrowserSubprocessPath`, `CefLibraryHandle` pre-loads `libcef.dll` from `cef\`, `SetDllDirectory(cef)`, `AssemblyResolve` for `CefSharp.Core.Runtime.dll` | `CEFManager.InitializeCef` |
| `CefRenderHandler.OnPaint` (pointer wrapped) | `IRenderHandler.OnPaint` copies the BGRA buffer into the overlay bitmap | `OverlayRenderHandler` |
| `CefSchemeHandlerFactory` for `https://<resource>/` | `RequestHandler.GetResourceRequestHandler` → `ResourceRequestHandler.GetResourceHandler` → `ResourceHandler.FromFilePath`; path checked with `ResourceFileDownloader.TryGetLocalPath` | `LocalResourceRequestHandler` |
| `CefRenderProcessHandler.OnContextCreated` registering `resourceCall` as a V8 function | `IRenderProcessMessageHandler.OnContextCreated` + `FrameLoadStart` inject a shim: `resourceCall` posts `CefSharp.PostMessage({type, name, args})`; the browser process receives `JavascriptMessageReceived` and runs the client script function on the script thread | `ResourceBridgeInjector`, `Browser.OnJavascriptMessage`, `BrowserJavascriptCallback.Invoke` |
| `browser.GetHost().SendMouse*/SendKeyEvent` | same names on `IBrowserHost`, `MouseEvent`/`KeyEvent` structs | `CefController` |
| `CefSettings.IgnoreCertificateErrors = true` | off (local pages do not need it) | |

**Behaviour change for page authors**: `resourceCall` used to return the value of the client function; pages
now live in another process, so it returns nothing (fire and forget). `resourceEval` likewise. `browser.call()`
and `browser.eval()` from the client script are unchanged (`ExecuteScriptAsync`). New pages can also use
`gtan.call(...)` / `gtan.eval(...)`.

**Settings** (`settings.xml`): `<CefGpu>true</CefGpu>` lets Chromium use the GPU (default off = Chromium's
display-compositor-only mode, `--disable-gpu --disable-gpu-compositing --use-gl=disabled
--disable-software-rasterizer`: no ANGLE, D3D11, SwiftShader or Vulkan is initialised inside the game, which
already runs DXVK on the same GPU; off-screen pages are composited in software anyway),
`<CefInProcessGpu>false</CefInProcessGpu>` moves the GPU service out of the game into a
`CefSharp.BrowserSubprocess.exe --type=gpu-process` (default: a thread inside the game, no extra process to launch
under Wine), `<CefFrameRate>30</CefFrameRate>` paints per second, `<CEFDevtool>true</CEFDevtool>` opens the remote
debugger on port 9222 (http://localhost:9222), `<DebugMode>` also enables the V8 inspector of ClearScript.

**Logs**: `logs\CEF.log` (our side: initialisation lines with the Chromium/CEF/CefSharp versions and the exact
Chromium switches, a line every 5 s while `Cef.Initialize` runs, browser creation, page loads, page console
output, `resourceCall`s), `logs\CEF-chromium.log` (Chromium's own log, verbose in debug mode).

## Packaging

The CefSharp NuGet targets copy the Chromium runtime, `CefSharp.BrowserSubprocess.exe` and
`CefSharp.Core.Runtime.dll` into `Client\bin\<cfg>\net48\cef\`; `eng/package-client.ps1` ships that folder as
`cef\` without `.pdb`/`.xml` and with a subset of locales (en-US, uk, ru, pl, de, fr, es, pt-BR, tr, it, nl, cs,
ro, hu). The managed `CefSharp.dll`, `CefSharp.Core.dll`, `CefSharp.OffScreen.dll` go to `bin\scripts` with the
client. `libs/cef` and `libs/Xilium.CefGlue.dll` are gone from the repository (144 MB less).

Requirements in the Proton prefix: .NET Framework 4.8 (the subprocess is a .NET Framework executable) and the
VC++ 2022 x64 runtime (C++/CLI parts) — both already installed by `setup-linux.sh`.

## Performance path (next)

1. **Software paint copy** (today): BGRA copy per paint into a `Bitmap`, one texture upload per paint in the
   overlay. Fine for a login form, not for a full-screen HUD at 60 fps.
2. **Dirty rectangles**: upload only `dirtyRect` (`OnPaint` provides it) into a persistent texture with
   `UpdateSubresource` instead of re-creating the texture.
3. **Shared textures**: `IRenderHandler.OnAcceleratedPaint` hands over a D3D11 shared handle when the GPU
   process is on (`CefGpu`) and `--shared-texture-enabled`; the overlay opens it with `OpenSharedResource` and
   draws it directly: zero copies, no CPU work per frame. This is the "real performance" step.
4. `WindowlessFrameRate` up to 60 for HUD-style pages (`API.setCefFramerate`), 30 default.

## Verification

* CI: the Linux job compiles the client against the packages; the Windows job builds the package and fails
  if `cef\libcef.dll`, `CefSharp.BrowserSubprocess.exe` or `CefSharp.Core.Runtime.dll` are missing.
* In game (the acceptance test): join a server with the `auth` resource, the login form appears, typing and
  clicking work, `CEF.log` shows `CEF initialised: Chromium 151...`, `Browser created!`, the page loads with
  `200`, `resourceCall authSubmit` when the form is submitted, and Task Manager (or `ps` in the prefix) shows
  `CefSharp.BrowserSubprocess.exe` processes.
* If Chromium refuses to start under Wine: `CEF.log` shows how far `Cef.Initialize` got (heartbeat lines) and
  `logs\CEF-chromium.log` where Chromium stopped; try `<CefInProcessGpu>false</CefInProcessGpu>` (separate GPU
  process) and the Wine log of the game process (`play.sh --debug`, `~/steam-271590.log`, `seh:dispatch_exception`
  lines of the game's pid). `--no-sandbox` is already implied by CefSharp.

## Risks

* Chromium 151 under Wine/Proton is untested here; Electron-class apps run under Wine 9+, usually with software
  rendering.
* The x86 runtime package is downloaded by NuGet as a dependency of `CefSharp.Common` although nothing uses it.
* `EnableDebugging` of ClearScript and CEF's remote debugging both default to port 9222; only one should be on.

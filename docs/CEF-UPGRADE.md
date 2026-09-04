# Upgrading the embedded browser (CEF)

## Where we are

* `libs/Xilium.CefGlue.dll` and `libs/cef/` are **CEF 3.2987.1590** (Chromium 57, March 2017), not "CEF 85"
  as older notes said. The runtime folder still has `natives_blob.bin`, `ffmpegsumo.dll` and
  `widevinecdmadapter.dll`, all gone from modern CEF.
* The client uses CefGlue directly: `Client/GUI/CEFManager.cs` (initialisation with
  `MultiThreadedMessageLoop = true`, windowless rendering, the `Browser` wrapper, `BrowserJavascriptCallback`),
  `Client/GUI/CefClient.cs` (`CefRenderHandler.OnPaint` copies the frame into the DirectX overlay,
  `CefRenderProcessHandler.OnContextCreated` injects `resourceCall`/`resourceEval`, a scheme handler serves
  `https://<resource>/...` from the downloaded resource files) and the browser subprocess
  `Subprocess/GTANSubprocess` (must link the same CefGlue build).
* Packaging: `eng/package-client.ps1` copies `libs/cef/*` into `cef/`; the NSIS installer and the Linux
  installer ship the folder as is.

## Why upgrade

Chromium 57 renders modern CSS/JS badly (no CSS grid gaps, no optional chaining, no ES2018+), has no
security fixes since 2017, and the V8 inside it is the same generation as ClearScript's. Every UI written for
the mod today has to target a 2017 browser.

## Options

1. **CefGlue, current build** (the same binding family, maintained by OutSystems on GitLab and NuGet,
   `Xilium.CefGlue` namespace kept). Least code churn: the client and the subprocess keep their structure.
   Requires the matching `cef.redist.x64` binaries of the same CEF version. Targets `netstandard2.0`, so it
   loads into the .NET Framework 4.8 client.
2. **CefSharp.OffScreen**. A different API (`ChromiumWebBrowser`, `IRenderHandler`, `IJavascriptCallback`);
   everything in `Client/GUI/Cef*.cs` and the subprocess would be rewritten. Better documented, larger
   community, but the offscreen path depends on `CefSharp.BrowserSubprocess.exe` and its own
   `CefSharp.Core.Runtime` native assembly.

Option 1 is the recommended path.

## Plan (option 1)

1. **Inventory the API surface** the client uses (done): `CefRuntime.Load/Initialize/Shutdown`,
   `CefSettings`, `CefWindowInfo.SetAsWindowless`, `CefBrowserSettings`, `CefBrowserHost.CreateBrowser`,
   `CefClient` overrides (`CefRenderHandler`: `GetViewRect`, `OnPaint`, `GetScreenPoint`; `CefLoadHandler`;
   `CefLifeSpanHandler`; `CefRequestHandler`), `CefV8Value/CefV8Context/CefV8Handler` for the page bridge,
   `CefSchemeHandlerFactory` + `CefResourceHandler` for the resource scheme, `CefBrowser.GetMainFrame()
   .ExecuteJavaScript/LoadUrl/LoadString`, `SendKeyEvent/SendMouseMoveEvent/SendMouseClickEvent/
   SendMouseWheelEvent`.
2. **API changes to expect** between CEF 57 and current CEF: `OnPaint` takes `CefPaintElementType` plus
   dirty rects and a `IntPtr buffer` (same shape); `GetViewRect` returns `void` with an out rect and must
   always succeed; `OnBeforeNavigation` is gone (use `OnBeforeBrowse` in `CefRequestHandler`);
   `CefResourceHandler` was split (`Open/Read/Skip`); `CefSettings.SingleProcess` is unsupported (the
   subprocess is mandatory); `natives_blob.bin` no longer exists; `CefSettings.NoSandbox` must be set for the
   in-game host; `MultiThreadedMessageLoop` is Windows-only but still supported.
3. **Rendering path**: keep the BGRA copy into the overlay texture but honour the dirty rects (today the
   whole frame is copied every paint) and switch `Browser.Size` changes to `WasResized()`.
4. **Subprocess**: rebuild `GTANSubprocess` against the same CefGlue, register the same
   `CefRenderProcessHandler` (the `resourceCall` bridge lives in the render process).
5. **Packaging**: replace `libs/cef` with the new redistributable (about 200 MB unpacked; consider
   downloading it in the installer instead of shipping it in the client zip), update
   `eng/package-client.ps1`, the NSIS script and `setup-linux.sh` (Proton runs CEF fine, but the sandbox
   must stay off).
6. **Verification without the game**: a small console harness that initialises CEF offscreen, loads
   `resources/auth/ui/index.html`, paints once and checks `resourceCall` is bound; run it on the Windows CI
   job. In game: the `auth` login form is the acceptance test.

## Risks

* Chromium binaries need the VC++ 2019+ runtime in the prefix (already installed by the Linux installer).
* GPU process under Proton: start with `CefSettings.CommandLineArgsDisabled = false` and
  `--disable-gpu --disable-gpu-compositing` (software rendering, as today), enable GPU later.
* A CEF upgrade is a separate pull request: it touches binaries the DirectX hook was tuned for.

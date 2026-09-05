using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CefSharp;
using CefSharp.Enums;
using CefSharp.Handler;
using CefSharp.OffScreen;
using CefSharp.Structs;
using GTANetworkShared;
using GTANetworkShared.Cef;
using Range = CefSharp.Structs.Range;
using Rect = CefSharp.Structs.Rect;

namespace GTANetwork.CefHarness
{
    /// <summary>
    /// Starts Chromium like Client/GUI/CEFManager.cs does (same switches via <see cref="CefLaunch"/>, same cef\ layout,
    /// libcef pre-load and CefSharp.Core.Runtime resolver, a dedicated STA thread) and reports how far it gets:
    /// Cef.Initialize returned, browser created, page loaded, first paint. Exit code 0 = painted, 2 = Initialize
    /// failed, 3 = no paint in time, 4 = exception, 5 = the CEF thread never finished (Chromium hung or died).
    /// </summary>
    internal static class Program
    {
        private static string _cefDir;
        private static string _logDir;
        private static string _cachePath;
        private static string _url;
        private static string _uiRoot;
        private static bool _gpu;
        private static bool _inProcessGpu = true;
        private static bool _externalPump;
        private static bool _sta = true;
        private static bool _verbose = true;
        private static int _timeoutSec = 60;
        private static int _stackMb = 8;
        private static int _holdSec;
        private static int _benchSec;
        private static bool _sharedTexture;
        internal static readonly List<string> HostSwitches = new List<string>();
        private static int _benchW = 1280;
        private static int _benchH = 720;
        private static bool _appDomain;
        private static bool _inProcess;
        private static string _hostExe;
        private static string _depsDir;
        private static string _appDomainBase;
        private static readonly List<KeyValuePair<string, string>> ExtraSwitches = new List<KeyValuePair<string, string>>();
        private static readonly List<string> RemovedSwitches = new List<string>();

        private static StreamWriter _log;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly ManualResetEvent Done = new ManualResetEvent(false);
        private static readonly ManualResetEvent Painted = new ManualResetEvent(false);
        private static readonly ManualResetEvent Loaded = new ManualResetEvent(false);
        private static int _exitCode = 4;
        private static string _outcome = "no result";
        private static int _paints;
        private static string _firstPaint;

        // external message pump state (same scheme as CEFManager.PumpUntilShutdown)
        private static readonly AutoResetEvent PumpSignal = new AutoResetEvent(false);
        private static long _pumpDueAt = -1;

        private static int Main(string[] args)
        {
            // The capture test (T-016) must not touch CefSharp: jitting a method that references CefSharp.Core loads libcef.dll,
            // and without the runtime next to the exe that fails before any line runs (under Wine as a dialog nobody closes).
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--capture-test") return CaptureTest.Run(int.Parse(args[i + 1], CultureInfo.InvariantCulture));
            }
            return RunHarness(args);
        }

        private static int RunHarness(string[] args)
        {
            // The exe's own folder, also inside a second AppDomain whose base is elsewhere.
            var exeDir = Path.GetDirectoryName(new Uri(typeof(Program).Assembly.CodeBase).LocalPath);
            _cefDir = Path.Combine(exeDir, "cef");
            _logDir = exeDir;

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("missing value after " + args[i - 1]);
                switch (args[i])
                {
                    case "--cef-dir": _cefDir = Next(); break;
                    case "--log-dir": _logDir = Next(); break;
                    case "--cache": _cachePath = Next(); break;
                    case "--url": _url = Next(); break;
                    case "--ui-root": _uiRoot = Next(); break; // the client's pages (repo ui/ or <install>\ui): the loader page is tested
                    case "--gpu": _gpu = true; break;
                    case "--gpu-process": _inProcessGpu = false; break;
                    case "--external-pump": _externalPump = true; break;
                    case "--mta": _sta = false; break;
                    case "--quiet-chromium": _verbose = false; break;
                    case "--timeout": _timeoutSec = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--stack": _stackMb = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--appdomain": _appDomain = true; _inProcess = true; break;
                    case "--in-process": _inProcess = true; break;
                    case "--host": _hostExe = Next(); break;
                    case "--hold": _holdSec = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--bench": _benchSec = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--shared-texture": _sharedTexture = true; _gpu = true; break;
                    case "--host-switch": HostSwitches.Add(Next()); break;
                    case "--size":
                    {
                        var parts = Next().Split('x');
                        _benchW = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        _benchH = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    }
                    case "--appdomain-base": _appDomainBase = Next(); break;
                    case "--deps-dir": _depsDir = Next(); break;
                    case "--switch":
                    {
                        var kv = Next();
                        var eq = kv.IndexOf('=');
                        ExtraSwitches.Add(eq < 0 ? new KeyValuePair<string, string>(kv.TrimStart('-'), "") : new KeyValuePair<string, string>(kv.Substring(0, eq).TrimStart('-'), kv.Substring(eq + 1)));
                        break;
                    }
                    case "--no-switch": RemovedSwitches.Add(Next().TrimStart('-')); break;
                    case "-h":
                    case "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown option " + args[i]);
                        Console.Error.WriteLine(Usage);
                        return 64;
                }
            }

            Directory.CreateDirectory(_logDir);
            _log = new StreamWriter(new FileStream(Path.Combine(_logDir, "harness.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

            Log("==== CefHarness start, pid " + Process.GetCurrentProcess().Id + ", AppDomain '" + AppDomain.CurrentDomain.FriendlyName + "'" +
                (AppDomain.CurrentDomain.IsDefaultAppDomain() ? " (default)" : " (NOT the default domain, base " + AppDomain.CurrentDomain.BaseDirectory + ")") + " ====");
            Log("args: " + string.Join(" ", args));
            Log("OS " + Environment.OSVersion + ", 64-bit " + Environment.Is64BitProcess + ", CLR " + Environment.Version + ", Wine " + (WineVersion() ?? "no (real Windows)"));
            Log("cef dir " + _cefDir + ", log dir " + _logDir);
            Log("mode: " + (_externalPump ? "external message pump" : "Chromium's own UI thread (MultiThreadedMessageLoop)") + ", " +
                (_gpu ? "GPU rendering" : "software rendering, GL disabled") + ", " + (_inProcessGpu ? "GPU service in-process" : "GPU process") +
                ", CEF thread " + (_sta ? "STA" : "MTA") + " with " + _stackMb + " MB stack, timeout " + _timeoutSec + " s");

            if (_appDomain && AppDomain.CurrentDomain.IsDefaultAppDomain())
            {
                // Like ScriptHookVDotNet: the code runs in a second AppDomain whose ApplicationBase has none of our
                // assemblies (the game's default domain probes the game folder). Chromium's own threads have no managed
                // context; when CefSharp's C++/CLI code is entered from one of them the CLR picks the default domain.
                var domainBase = _appDomainBase ?? Path.Combine(_logDir, "domain-base");
                Directory.CreateDirectory(domainBase);
                var setup = new AppDomainSetup { ApplicationBase = domainBase, ApplicationName = "ScriptDomain_harness" };
                var domain = AppDomain.CreateDomain("ScriptDomain_harness", null, setup);
                Log("created AppDomain '" + domain.FriendlyName + "' with base " + domainBase + "; running there");
                var runner = (Runner)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(Runner).FullName);
                var rc = runner.Run(args.Where(a => a != "--appdomain").ToArray());
                Log("second AppDomain finished with exit code " + rc);
                _log.Flush();
                return rc;
            }

            if (!_inProcess)
            {
                var rc = HostTest.Run(_hostExe ?? Path.Combine(exeDir, "GTANetwork.CefHost.exe"), _logDir, _timeoutSec, _gpu, _inProcessGpu, _verbose, _url, _holdSec, _benchSec, _benchW, _benchH, _sharedTexture, _uiRoot);
                _log.Flush();
                return rc;
            }

            if (!File.Exists(Path.Combine(_cefDir, "libcef.dll")) || !File.Exists(Path.Combine(_cefDir, "CefSharp.BrowserSubprocess.exe")))
            {
                Log("RESULT: libcef.dll or CefSharp.BrowserSubprocess.exe missing in " + _cefDir + " (exit code 2)");
                return 2;
            }

            RegisterAssemblyResolver();

            var thread = new Thread(CefThread, _stackMb * 1024 * 1024) { Name = "CEF", IsBackground = true };
            if (_sta) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!Done.WaitOne(TimeSpan.FromSeconds(_timeoutSec + 30)))
            {
                _outcome = "the CEF thread did not finish within " + (_timeoutSec + 30) + " s (Chromium hung)";
                _exitCode = 5;
            }

            Log("RESULT: " + _outcome + " (exit code " + _exitCode + ")");
            _log.Flush();
            return _exitCode;
        }

        /// <summary>Entry point inside the second AppDomain (--appdomain).</summary>
        public sealed class Runner : MarshalByRefObject
        {
            public int Run(string[] args)
            {
                return Main(args);
            }
        }

        private const string Usage =
            "CefHarness [options]\n" +
            "  default: drive GTANetwork.CefHost.exe (--host <exe>, default: next to the harness) over its stdin/stdout protocol,\n" +
            "           open the shared-memory frame, wait for pixels and for a resourceCall from the page\n" +
            "  --in-process         instead start Chromium inside this process, the way the client did before the host existed\n" +
            "  --host <exe>         path of GTANetwork.CefHost.exe for the default mode\n" +
            "  --hold <s>           keep the browser open that many seconds after the checks (to watch windows/focus)\n" +
            "  --shared-texture     frames as D3D11 shared textures (implies --gpu): open them on our own device and read back\n" +
            "  --host-switch <k=v>  extra Chromium switch for the host (repeatable), e.g. js-flags=--jitless\n" +
            "  --bench <s>          then run an animated page in a --size WxH browser (default 1280x720) for that many seconds:\n" +
            "                       frames/s delivered to shared memory, copy cost, CPU of the host and its subprocesses\n" +
            "  --cef-dir <dir>      Chromium runtime folder (default: cef\\ next to the exe; e.g. Z:\\home\\me\\GTANetwork\\cef)\n" +
            "  --log-dir <dir>      where harness.log, harness-chromium.log and cache\\ go (default: the exe folder)\n" +
            "  --cache <dir>        Chromium cache path (default: <log dir>\\cache)\n" +
            "  --url <url>          page to load (default: a built-in data: page)\n" +
            "  --gpu                let Chromium use the GPU (default: software rendering, GL disabled)\n" +
            "  --gpu-process        GPU service in a subprocess instead of in-process\n" +
            "  --external-pump      external message pump instead of MultiThreadedMessageLoop\n" +
            "  --mta                MTA CEF thread (default STA, as in the game)\n" +
            "  --stack <MB>         CEF thread stack size (default 8)\n" +
            "  --appdomain          run everything in a second AppDomain, like ScriptHookVDotNet runs the client\n" +
            "  --appdomain-base <d> ApplicationBase of that domain (default: an empty folder, so nothing probes there)\n" +
            "  --deps-dir <dir>     resolve managed assemblies (CefSharp.dll, GTANetworkShared.dll, ...) from there, like\n" +
            "                       ScriptHookVDotNet resolves the client's assemblies from bin\\scripts\n" +
            "  --timeout <s>        seconds to wait for the first paint (default 60)\n" +
            "  --switch <k[=v]>     add/override a Chromium switch (repeatable)\n" +
            "  --no-switch <k>      drop one of the default switches (repeatable)\n" +
            "  --quiet-chromium     Chromium log at Info instead of Verbose\n";

        internal static void Log(string text)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " +" + Clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s] " + text;
            Console.WriteLine(line);
            try { _log?.WriteLine(line); } catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WineGetVersion();

        private static string WineVersion()
        {
            try
            {
                var ntdll = GetModuleHandle("ntdll.dll");
                var fn = ntdll == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(ntdll, "wine_get_version");
                if (fn == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<WineGetVersion>(fn)());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>CefSharp.Core.Runtime.dll (C++/CLI) lives in cef\ like in the game install; same resolver as CEFManager.</summary>
        private static void RegisterAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name;
                    if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
                    var candidate = name.StartsWith("CefSharp", StringComparison.OrdinalIgnoreCase) ? Path.Combine(_cefDir, name + ".dll") : null;
                    if ((candidate == null || !File.Exists(candidate)) && _depsDir != null) candidate = Path.Combine(_depsDir, name + ".dll");
                    if (candidate == null || !File.Exists(candidate)) return null;
                    Log("resolving " + name + " from " + candidate + " (in AppDomain '" + AppDomain.CurrentDomain.FriendlyName + "')");
                    return Assembly.LoadFrom(candidate);
                }
                catch (Exception ex)
                {
                    Log("assembly resolve failed: " + ex);
                    return null;
                }
            };
        }

        private static void CefThread()
        {
            try
            {
                RunCef();
            }
            catch (Exception ex)
            {
                Log("EXCEPTION on the CEF thread: " + ex);
                _outcome = "exception: " + ex.GetType().Name + ": " + ex.Message;
                _exitCode = 4;
            }
            finally
            {
                Done.Set();
            }
        }

        // Kept apart from CefThread so that the JIT touches CefSharp types only after the resolver is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunCef()
        {
            var libcef = new CefLibraryHandle(Path.Combine(_cefDir, "libcef.dll"));
            Log(libcef.IsInvalid ? "libcef.dll pre-load FAILED (error " + Marshal.GetLastWin32Error() + ")" : "libcef.dll pre-loaded");
            Log("CefSharp " + Cef.CefSharpVersion + " / CEF " + Cef.CefVersion + " / Chromium " + Cef.ChromiumVersion);

            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
            CefSharpSettings.ShutdownOnExit = false;
            CefSharpSettings.RuntimeStyle = CefRuntimeStyle.Alloy;

            var settings = new CefSettings
            {
                BrowserSubprocessPath = Path.Combine(_cefDir, "CefSharp.BrowserSubprocess.exe"),
                CachePath = _cachePath ?? Path.Combine(_logDir, "cache"),
                LocalesDirPath = Path.Combine(_cefDir, "locales"),
                ResourcesDirPath = _cefDir,
                LogFile = Path.Combine(_logDir, "harness-chromium.log"),
                LogSeverity = _verbose ? LogSeverity.Verbose : LogSeverity.Info,
                MultiThreadedMessageLoop = !_externalPump,
                ExternalMessagePump = _externalPump,
                WindowlessRenderingEnabled = true,
                BackgroundColor = 0,
                IgnoreCertificateErrors = false,
            };

            var switches = CefLaunch.Switches(_gpu, _inProcessGpu, false).Where(kv => !RemovedSwitches.Contains(kv.Key)).ToList();
            foreach (var extra in ExtraSwitches)
            {
                switches.RemoveAll(kv => kv.Key == extra.Key);
                switches.Add(extra);
            }
            foreach (var kv in switches) settings.CefCommandLineArgs.Add(kv.Key, kv.Value);
            Log("switches: " + CefLaunch.Describe(settings.CefCommandLineArgs));

            var started = Stopwatch.StartNew();
            var returned = false;
            bool ok;
            using (new Timer(_ =>
            {
                if (!returned) Log("Cef.Initialize still running after " + started.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            }, null, 1000, 1000))
            {
                Log("--> Cef.Initialize");
                ok = Cef.Initialize(settings, false, _externalPump ? new PumpScheduler() : (IBrowserProcessHandler)null);
                returned = true;
            }
            Log("<-- Cef.Initialize returned " + ok + " after " + started.ElapsedMilliseconds + " ms");

            if (!ok)
            {
                _outcome = "Cef.Initialize returned false (see harness-chromium.log)";
                _exitCode = 2;
                return;
            }

            var url = _url ?? "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(TestPage));
            var render = new PaintRecorder();
            var browserSettings = new BrowserSettings(true)
            {
                WindowlessFrameRate = 30,
                BackgroundColor = 0,
                JavascriptCloseWindows = CefState.Disabled,
            };
            var browser = new ChromiumWebBrowser(string.Empty, browserSettings, null, false, null, false)
            {
                Size = new System.Drawing.Size(420, 480),
                RenderHandler = render,
            };
            browser.BrowserInitialized += (s, e) =>
            {
                Log("browser created, loading " + (url.Length > 80 ? url.Substring(0, 80) + "..." : url));
                browser.Load(url);
            };
            browser.LoadingStateChanged += (s, e) => Log("loading state: " + (e.IsLoading ? "loading" : "idle"));
            browser.FrameLoadEnd += (s, e) =>
            {
                if (e.Frame == null || !e.Frame.IsMain) return;
                Log("page loaded: HTTP " + e.HttpStatusCode + " " + e.Url);
                Loaded.Set();
            };
            browser.LoadError += (s, e) => Log("load error " + e.ErrorCode + " (" + e.ErrorText + ") for " + e.FailedUrl);
            browser.ConsoleMessage += (s, e) => Log("page console [" + e.Level + "] " + e.Message + " (" + e.Source + ":" + e.Line + ")");

            Log("--> CreateBrowser");
            browser.CreateBrowser(null, browserSettings);

            var painted = WaitFor(() => Painted.WaitOne(0), _timeoutSec);
            if (painted)
            {
                _outcome = "OK: first paint " + _firstPaint + ", " + _paints + " paint(s), Cef.Initialize took " + started.ElapsedMilliseconds + " ms";
                _exitCode = 0;

                if (WaitFor(() => Loaded.WaitOne(0) && browser.CanExecuteJavascriptInMainFrame, 10))
                {
                    var eval = browser.EvaluateScriptAsync("document.title + ' | ' + navigator.userAgent");
                    if (WaitFor(() => eval.IsCompleted, 5) && eval.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        Log("page says: " + (eval.Result.Success ? Convert.ToString(eval.Result.Result) : "script error " + eval.Result.Message));
                    else
                        Log("EvaluateScriptAsync did not complete");
                }
                else Log("no V8 context in the main frame within 10 s; skipping the script check");
            }
            else
            {
                _outcome = "no paint within " + _timeoutSec + " s (Initialize ok, " + (Loaded.WaitOne(0) ? "page loaded" : "page not loaded") + ")";
                _exitCode = 3;
            }

            Log("--> dispose browser");
            browser.Dispose();
            if (_externalPump) WaitFor(() => false, 0.3);

            Log("--> Cef.Shutdown");
            Cef.Shutdown();
            Log("<-- Cef.Shutdown returned");
        }

        /// <summary>Waits for a condition; with the external pump this thread keeps pumping Chromium while it waits.</summary>
        private static bool WaitFor(Func<bool> condition, double seconds)
        {
            var deadline = Clock.Elapsed + TimeSpan.FromSeconds(seconds);
            var handles = new WaitHandle[] { PumpSignal };
            while (!condition())
            {
                var left = deadline - Clock.Elapsed;
                if (left <= TimeSpan.Zero) return condition();

                if (!_externalPump)
                {
                    Thread.Sleep((int)Math.Min(50, left.TotalMilliseconds));
                    continue;
                }

                var wait = (int)Math.Min(30, left.TotalMilliseconds);
                var due = Interlocked.Read(ref _pumpDueAt);
                if (due >= 0)
                {
                    var ms = due == 0 ? 0 : (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    wait = (int)Math.Max(0, Math.Min(ms, wait));
                }
                if (WaitHandle.WaitAny(handles, wait) == 0) continue; // new schedule: recompute

                Interlocked.Exchange(ref _pumpDueAt, -1);
                Cef.DoMessageLoopWork();
            }
            return true;
        }

        private sealed class PumpScheduler : BrowserProcessHandler
        {
            private int _logged;

            protected override void OnScheduleMessagePumpWork(long delay)
            {
                if (_logged++ < 3) Log("OnScheduleMessagePumpWork(" + delay + ")");
                var dueAt = delay <= 0 ? 0 : Stopwatch.GetTimestamp() + delay * Stopwatch.Frequency / 1000;
                Interlocked.Exchange(ref _pumpDueAt, dueAt);
                PumpSignal.Set();
            }

            protected override void OnContextInitialized()
            {
                Log("OnContextInitialized");
            }
        }

        private const string TestPage =
            "<!doctype html><html><head><meta charset='utf-8'><title>gtan harness</title></head>" +
            "<body style='margin:0;background:#205080;color:#fff;font:24px sans-serif;padding:20px'>" +
            "<h1>GTA Network CEF harness</h1><p id='t'>no script</p>" +
            "<script>document.getElementById('t').textContent = 'JS ok ' + new Date().toISOString(); console.log('harness page script ran');</script>" +
            "</body></html>";

        /// <summary>Same shape as the game's OverlayRenderHandler, minus the overlay: counts paints.</summary>
        private sealed class PaintRecorder : IRenderHandler
        {
            public ScreenInfo? GetScreenInfo() => null;
            public Rect GetViewRect() => new Rect(0, 0, 420, 480);

            public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
            {
                screenX = viewX;
                screenY = viewY;
                return true;
            }

            public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
            {
                Log("OnAcceleratedPaint " + type);
            }

            public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
            {
                if (type != PaintElementType.View) return;
                var n = Interlocked.Increment(ref _paints);
                if (n == 1)
                {
                    _firstPaint = width + "x" + height + " (dirty " + dirtyRect.Width + "x" + dirtyRect.Height + " at " + dirtyRect.X + "," + dirtyRect.Y + ")";
                    Log("first paint " + _firstPaint);
                    Painted.Set();
                }
                else if (n <= 3) Log("paint #" + n + " " + width + "x" + height);
            }

            public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) { }
            public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;
            public void UpdateDragCursor(DragOperationsMask operation) { }
            public void OnPopupShow(bool show) { }
            public void OnPopupSize(Rect rect) { }
            public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds) { }
            public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }
            public void Dispose() { }
        }
    }
}

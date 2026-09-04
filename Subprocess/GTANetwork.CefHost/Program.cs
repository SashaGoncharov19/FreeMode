using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
using Size = System.Drawing.Size;

namespace GTANetwork.CefHost
{
    /// <summary>
    /// Chromium for the GTA Network client, in its own process. Commands arrive on stdin, events leave on stdout
    /// (<see cref="CefHostChannel"/>), pixels go through <see cref="CefFrameBuffer"/>s. Exits when the game closes
    /// the pipe, when the parent process dies or on a "shutdown" command.
    /// </summary>
    internal static class Program
    {
        private static string _logPath;
        private static string _chromiumLog;
        private static string _cachePath;
        private static string _resourceRoot;
        private static bool _gpu;
        private static bool _inProcessGpu = true;
        private static bool _mediaStream;
        private static bool _verbose;
        private static int _devtoolsPort;
        private static int _parentPid;

        private static CefHostChannel _channel;
        private static StreamWriter _log;
        private static readonly object LogLock = new object();
        private static readonly Dictionary<int, HostedBrowser> Browsers = new Dictionary<int, HostedBrowser>();
        private static readonly object BrowsersLock = new object();

        [STAThread]
        private static int Main(string[] args)
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            _logPath = Path.Combine(exeDir, "GTANetwork.CefHost.log");
            _chromiumLog = Path.Combine(exeDir, "GTANetwork.CefHost-chromium.log");
            _cachePath = Path.Combine(exeDir, "cache");

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("missing value after " + args[i - 1]);
                switch (args[i])
                {
                    case "--parent": _parentPid = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--log": _logPath = Next(); break;
                    case "--chromium-log": _chromiumLog = Next(); break;
                    case "--cache": _cachePath = Next(); break;
                    case "--resource-root": _resourceRoot = Next(); break;
                    case "--gpu": _gpu = true; break;
                    case "--gpu-process": _inProcessGpu = false; break;
                    case "--media-stream": _mediaStream = true; break;
                    case "--verbose": _verbose = true; break;
                    case "--devtools": _devtoolsPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default:
                        Console.Error.WriteLine("GTANetwork.CefHost: unknown option " + args[i]);
                        return 64;
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? exeDir);
                _log = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("GTANetwork.CefHost: cannot open the log " + _logPath + ": " + ex.Message);
            }

            // stdout is the channel: nothing else may ever write to it.
            _channel = new CefHostChannel(Console.OpenStandardInput(), Console.OpenStandardOutput());
            Console.SetOut(TextWriter.Null);

            Log("==== GTANetwork.CefHost start, pid " + Process.GetCurrentProcess().Id + ", parent " + _parentPid + ", protocol " + CefHostProtocol.Version +
                ", Wine " + (WineVersion() ?? "no") + " ====");
            Log("args: " + string.Join(" ", args));

            if (_parentPid > 0)
            {
                new Thread(() =>
                {
                    try { Process.GetProcessById(_parentPid).WaitForExit(); }
                    catch (Exception ex) { Log("parent watch: " + ex.Message); }
                    Log("the parent process is gone; exiting");
                    Environment.Exit(3);
                }) { IsBackground = true, Name = "parent watch" }.Start();
            }

            var exit = 4;
            try
            {
                if (!InitializeCef()) return 2;
                exit = ServeCommands();
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                TrySend(new CefHostMessage(CefHostProtocol.Log) { Text = "CEF host failed: " + ex.Message });
            }
            finally
            {
                Cleanup();
            }
            Log("==== exit " + exit + " ====");
            return exit;
        }

        private static bool InitializeCef()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
            CefSharpSettings.ShutdownOnExit = false;
            CefSharpSettings.RuntimeStyle = CefRuntimeStyle.Alloy;

            var settings = new CefSettings
            {
                BrowserSubprocessPath = Path.Combine(exeDir, "CefSharp.BrowserSubprocess.exe"),
                CachePath = _cachePath,
                LocalesDirPath = Path.Combine(exeDir, "locales"),
                ResourcesDirPath = exeDir,
                LogFile = _chromiumLog,
                LogSeverity = _verbose ? LogSeverity.Verbose : LogSeverity.Warning,
                MultiThreadedMessageLoop = true,
                WindowlessRenderingEnabled = true,
                BackgroundColor = 0,
                IgnoreCertificateErrors = false,
            };
            if (_devtoolsPort > 0) settings.RemoteDebuggingPort = _devtoolsPort;
            foreach (var kv in CefLaunch.Switches(_gpu, _inProcessGpu, _mediaStream)) settings.CefCommandLineArgs.Add(kv.Key, kv.Value);

            var how = (_gpu ? "GPU rendering" : "software rendering, GL disabled") + ", " + (_inProcessGpu ? "GPU service in-process" : "GPU process");
            Log("Cef.Initialize (" + how + ", cache " + _cachePath + ")");
            Log("switches: " + CefLaunch.Describe(settings.CefCommandLineArgs));

            var started = Stopwatch.StartNew();
            bool ok;
            try
            {
                ok = Cef.Initialize(settings, false, (IBrowserProcessHandler)null);
            }
            catch (Exception ex)
            {
                Log("Cef.Initialize threw: " + ex);
                TrySend(new CefHostMessage(CefHostProtocol.InitFailed) { Text = ex.GetType().Name + ": " + ex.Message });
                return false;
            }
            Log("Cef.Initialize returned " + ok + " after " + started.ElapsedMilliseconds + " ms");

            if (!ok)
            {
                TrySend(new CefHostMessage(CefHostProtocol.InitFailed) { Text = "Cef.Initialize returned false (see " + _chromiumLog + ")" });
                return false;
            }

            TrySend(new CefHostMessage(CefHostProtocol.Ready)
            {
                Chromium = Cef.ChromiumVersion,
                CefVersion = Cef.CefVersion,
                CefSharp = Cef.CefSharpVersion,
                Text = how + ", " + started.ElapsedMilliseconds + " ms",
            });
            return true;
        }

        private static int ServeCommands()
        {
            while (true)
            {
                CefHostMessage msg;
                try
                {
                    msg = _channel.Receive();
                }
                catch (Exception ex)
                {
                    Log("channel read failed: " + ex.Message);
                    return 5;
                }
                if (msg == null)
                {
                    Log("the game closed the channel");
                    return 0;
                }
                if (msg.Type == CefHostProtocol.Shutdown)
                {
                    Log("shutdown requested");
                    return 0;
                }

                try
                {
                    Dispatch(msg);
                }
                catch (Exception ex)
                {
                    Log("command " + msg + " failed: " + ex);
                }
            }
        }

        private static void Dispatch(CefHostMessage m)
        {
            if (m.Type == CefHostProtocol.Create)
            {
                HostedBrowser previous;
                lock (BrowsersLock) Browsers.TryGetValue(m.Id, out previous);
                if (previous != null)
                {
                    // The game creates a browser again under the same id when it changes how frames travel.
                    Log("browser " + m.Id + ": replaced");
                    lock (BrowsersLock) Browsers.Remove(m.Id);
                    previous.Dispose();
                }
                var browser = new HostedBrowser(m.Id, Math.Max(1, m.W), Math.Max(1, m.H), m.Local, m.Fps > 0 ? Math.Min(60, m.Fps) : 30, m.Shared && _gpu);
                lock (BrowsersLock) Browsers[m.Id] = browser;
                browser.Start();
                return;
            }

            HostedBrowser b;
            lock (BrowsersLock) Browsers.TryGetValue(m.Id, out b);
            if (b == null)
            {
                if (m.Type != CefHostProtocol.Close) Log("command " + m + " for an unknown browser");
                return;
            }

            switch (m.Type)
            {
                case CefHostProtocol.Load: b.Load(m.Url); break;
                case CefHostProtocol.LoadHtml:
                    b.Load("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(ResourceBridgeInjector.InjectIntoHtml(m.Html ?? string.Empty))));
                    break;
                case CefHostProtocol.Eval: b.Eval(m.Code); break;
                case CefHostProtocol.Back: b.Back(); break;
                case CefHostProtocol.Resize: b.Resize(m.W, m.H); break;
                case CefHostProtocol.Focus: b.Host?.SetFocus(m.On); break;
                case CefHostProtocol.FrameRate: if (b.Host != null) b.Host.WindowlessFrameRate = Math.Max(1, Math.Min(60, m.Fps)); break;
                case CefHostProtocol.MouseMove: b.Host?.SendMouseMoveEvent(new MouseEvent(m.X, m.Y, (CefEventFlags)m.Mods), m.On); break;
                case CefHostProtocol.MouseClick: b.Host?.SendMouseClickEvent(new MouseEvent(m.X, m.Y, (CefEventFlags)m.Mods), (MouseButtonType)m.Button, m.On, Math.Max(1, m.Clicks)); break;
                case CefHostProtocol.MouseWheel: b.Host?.SendMouseWheelEvent(new MouseEvent(m.X, m.Y, (CefEventFlags)m.Mods), m.Dx, m.Dy); break;
                case CefHostProtocol.Key:
                    b.Host?.SendKeyEvent(new KeyEvent
                    {
                        Type = (KeyEventType)m.KeyType,
                        WindowsKeyCode = m.KeyCode,
                        NativeKeyCode = m.NativeKeyCode,
                        Modifiers = (CefEventFlags)m.Mods,
                    });
                    break;
                case CefHostProtocol.Close:
                    lock (BrowsersLock) Browsers.Remove(m.Id);
                    b.Dispose();
                    TrySend(new CefHostMessage(CefHostProtocol.Closed, m.Id));
                    break;
                default:
                    Log("unknown command " + m.Type);
                    break;
            }
        }

        private static void Cleanup()
        {
            HostedBrowser[] all;
            lock (BrowsersLock)
            {
                all = Browsers.Values.ToArray();
                Browsers.Clear();
            }
            foreach (var b in all)
            {
                try { b.Dispose(); } catch (Exception ex) { Log("closing browser " + b.Id + ": " + ex.Message); }
            }
            try
            {
                if (Cef.IsInitialized == true) Cef.Shutdown();
            }
            catch (Exception ex)
            {
                Log("Cef.Shutdown: " + ex.Message);
            }
        }

        internal static void Log(string text)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + text;
            lock (LogLock)
            {
                try { _log?.WriteLine(line); } catch { }
            }
        }

        /// <summary>A line for both logs: the host's own and, through a "log" event, the game's CEF.log.</summary>
        internal static void Notify(string text)
        {
            Log(text);
            TrySend(new CefHostMessage(CefHostProtocol.Log) { Text = text });
        }

        internal static void TrySend(CefHostMessage message)
        {
            try
            {
                _channel.Send(message);
            }
            catch (Exception ex)
            {
                Log("send " + message.Type + " failed: " + ex.Message);
            }
        }

        internal static string ResourceRoot => _resourceRoot;
        internal static bool Verbose => _verbose;
        internal static int ParentPid => _parentPid;

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
                return fn == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<WineGetVersion>(fn)());
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>One off-screen browser and the shared-memory buffer its frames go to.</summary>
    internal sealed class HostedBrowser : IDisposable
    {
        public readonly int Id;
        private readonly bool _local;
        private readonly int _fps;
        private readonly bool _shared;
        private ChromiumWebBrowser _browser;
        private FrameWriter _render;
        private string _pendingUrl;
        private volatile bool _initialized;
        private int _width;
        private int _height;

        public HostedBrowser(int id, int width, int height, bool local, int fps, bool shared)
        {
            Id = id;
            _width = width;
            _height = height;
            _local = local;
            _fps = fps;
            _shared = shared;
        }

        public IBrowserHost Host
        {
            get
            {
                try
                {
                    var b = _browser;
                    return b != null && !b.IsDisposed && _initialized ? b.GetBrowserHost() : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Start()
        {
            Program.Log("browser " + Id + ": create " + _width + "x" + _height + (_local ? " local" : " remote") + ", " + _fps + " fps" + (_shared ? ", shared textures" : ""));
            _render = new FrameWriter(this);

            var settings = new BrowserSettings(true)
            {
                WindowlessFrameRate = _fps,
                BackgroundColor = 0,
                JavascriptCloseWindows = CefState.Disabled,
            };

            _browser = new ChromiumWebBrowser(string.Empty, settings, null, false, null, false)
            {
                Size = new Size(_width, _height),
                RenderHandler = _render,
                RequestHandler = new LocalResourceRequestHandler(_local),
                LifeSpanHandler = new PopupToMainFrameLifeSpanHandler(),
                MenuHandler = new NoContextMenuHandler(),
                RenderProcessMessageHandler = new ResourceBridgeInjector(this),
            };

            _browser.BrowserInitialized += (s, e) =>
            {
                _initialized = true;
                Program.Notify("Browser " + Id + " created");
                Program.TrySend(new CefHostMessage(CefHostProtocol.Created, Id));
                var pending = _pendingUrl;
                _pendingUrl = null;
                if (pending != null) Load(pending);
            };
            _browser.FrameLoadStart += (s, e) =>
            {
                if (e.Frame != null && e.Frame.IsMain) Program.TrySend(new CefHostMessage(CefHostProtocol.LoadStart, Id) { Url = e.Url });
                // The bridge is also injected from OnContextCreated; this covers pages whose scripts run before that message arrives.
                e.Frame?.ExecuteJavaScriptAsync(ResourceBridgeInjector.Shim, "gtan://bridge", 0);
            };
            _browser.FrameLoadEnd += (s, e) =>
            {
                if (e.Frame == null || !e.Frame.IsMain) return;
                Program.Log("browser " + Id + ": loaded " + e.Url + " (" + e.HttpStatusCode + ")");
                Program.TrySend(new CefHostMessage(CefHostProtocol.LoadEnd, Id) { Url = e.Url, Status = e.HttpStatusCode });
            };
            _browser.LoadError += (s, e) =>
            {
                Program.Log("browser " + Id + ": load error " + e.ErrorCode + " (" + e.ErrorText + ") for " + e.FailedUrl);
                Program.TrySend(new CefHostMessage(CefHostProtocol.LoadError, Id) { Status = (int)e.ErrorCode, Text = e.ErrorText, Url = e.FailedUrl });
            };
            _browser.LoadingStateChanged += (s, e) => Program.TrySend(new CefHostMessage(CefHostProtocol.Loading, Id) { IsLoading = e.IsLoading });
            _browser.ConsoleMessage += (s, e) =>
                Program.TrySend(new CefHostMessage(CefHostProtocol.Console, Id) { Level = (int)e.Level, Text = e.Message, Source = e.Source, Line = e.Line });
            _browser.JavascriptMessageReceived += OnJavascriptMessage;

            // Shared textures: Chromium renders into D3D11 textures and hands us their handles (OnAcceleratedPaint)
            // instead of CPU buffers (OnPaint); the game opens the same textures on its device.
            var windowInfo = new WindowInfo();
            windowInfo.SetAsWindowless(IntPtr.Zero);
            windowInfo.SharedTextureEnabled = _shared;
            _browser.CreateBrowser(windowInfo, settings);
        }

        public void Load(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var b = _browser;
            if (b == null || b.IsDisposed) return;
            if (!_initialized)
            {
                _pendingUrl = url;
                Program.Log("browser " + Id + ": " + Describe(url) + " queued until the browser exists");
                return;
            }
            Program.Log("browser " + Id + ": load " + Describe(url));
            b.Load(url);
        }

        public void Eval(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            var b = _browser;
            if (b == null || b.IsDisposed || !_initialized) return;
            // The frame-level call does not insist on a V8 context being there already (the page may still be loading).
            b.GetBrowser()?.MainFrame?.ExecuteJavaScriptAsync(code, "gtan://eval", 0);
        }

        public void Back()
        {
            var b = _browser;
            if (b != null && !b.IsDisposed && _initialized && b.CanGoBack) b.Back();
        }

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            _width = width;
            _height = height;
            var b = _browser;
            if (b != null && !b.IsDisposed) b.Size = new Size(width, height);
        }

        public int Width => _width;
        public int Height => _height;

        private void OnJavascriptMessage(object sender, JavascriptMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.Message as IDictionary<string, object>;
                if (message == null) return;

                object typeObj;
                message.TryGetValue("type", out typeObj);
                var type = typeObj as string;

                if (type == "resourceCall")
                {
                    object nameObj, argsObj;
                    message.TryGetValue("name", out nameObj);
                    message.TryGetValue("args", out argsObj);
                    var name = nameObj as string;
                    if (string.IsNullOrEmpty(name)) return;
                    var args = (argsObj as IEnumerable<object>)?.ToArray() ?? new object[0];
                    if (Program.Verbose) Program.Log("browser " + Id + ": resourceCall " + name + " (" + args.Length + " argument(s))");
                    Program.TrySend(new CefHostMessage(CefHostProtocol.JsMessage, Id) { Name = name, Args = args });
                }
                else if (type == "resourceEval")
                {
                    object codeObj;
                    message.TryGetValue("code", out codeObj);
                    var code = codeObj as string;
                    if (!string.IsNullOrEmpty(code)) Program.TrySend(new CefHostMessage(CefHostProtocol.JsMessage, Id) { Code = code });
                }
            }
            catch (Exception ex)
            {
                Program.Log("browser " + Id + ": page message failed: " + ex);
            }
        }

        internal void OnRenderProcessTerminated(CefTerminationStatus status, int errorCode, string errorMessage)
        {
            Program.Notify("Browser " + Id + ": render process terminated: " + status + " (" + errorCode + ") " + errorMessage);
            Program.TrySend(new CefHostMessage(CefHostProtocol.RenderTerminated, Id) { Status = (int)status, Text = status + " (" + errorCode + ") " + errorMessage });
        }

        private static string Describe(string url)
        {
            return url.Length > 96 ? url.Substring(0, 96) + "..." : url;
        }

        public void Dispose()
        {
            var b = _browser;
            _browser = null;
            try { b?.Dispose(); } catch (Exception ex) { Program.Log("browser " + Id + ": dispose: " + ex.Message); }
            _render?.Dispose();
            _render = null;
            Program.Log("browser " + Id + ": closed");
        }

        /// <summary>CEF's paints, copied into the shared frame buffer; a new buffer whenever the size changes.</summary>
        private sealed class FrameWriter : IRenderHandler
        {
            private readonly HostedBrowser _owner;
            private CefFrameBuffer _frame;
            private int _generation;
            private int _paints;

            public FrameWriter(HostedBrowser owner)
            {
                _owner = owner;
            }

            public ScreenInfo? GetScreenInfo() => null;
            public Rect GetViewRect() => new Rect(0, 0, _owner.Width, _owner.Height);

            public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
            {
                screenX = viewX;
                screenY = viewY;
                return true;
            }

            private readonly Dictionary<IntPtr, IntPtr> _sharedHandles = new Dictionary<IntPtr, IntPtr>();
            private IntPtr _parentProcess;
            private int _texturePaints;

            /// <summary>
            /// Chromium painted into one of its shared D3D11 textures. The handle is ours; the game needs its own, so
            /// it is duplicated into the game process once per texture (Chromium cycles through a small pool) and the
            /// game is told which texture holds this frame.
            /// </summary>
            public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
            {
                try
                {
                    if (type != PaintElementType.View) return;
                    var source = acceleratedPaintInfo.SharedTextureHandle;
                    if (source == IntPtr.Zero) return;

                    IntPtr forGame;
                    if (!_sharedHandles.TryGetValue(source, out forGame))
                    {
                        if (_parentProcess == IntPtr.Zero)
                        {
                            _parentProcess = Program.ParentPid > 0 ? NativeMethods.OpenProcess(NativeMethods.ProcessDupHandle, false, Program.ParentPid) : IntPtr.Zero;
                            if (_parentProcess == IntPtr.Zero)
                            {
                                Program.Notify("Browser " + _owner.Id + ": cannot open the game process for handle duplication (error " + Marshal.GetLastWin32Error() + "); shared textures unavailable");
                                _parentProcess = new IntPtr(-1);
                            }
                        }
                        if (_parentProcess == new IntPtr(-1)) return;

                        if (!NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), source, _parentProcess, out forGame, 0, false, NativeMethods.DuplicateSameAccess))
                        {
                            Program.Notify("Browser " + _owner.Id + ": DuplicateHandle failed (error " + Marshal.GetLastWin32Error() + ")");
                            return;
                        }
                        _sharedHandles[source] = forGame;
                        Program.Log("browser " + _owner.Id + ": shared texture " + source.ToString("X") + " -> game handle " + forGame.ToString("X") + " (" + _owner.Width + "x" + _owner.Height + ")");
                    }

                    if (++_texturePaints <= 3 && Program.Verbose)
                        Program.Log("browser " + _owner.Id + ": texture paint " + _owner.Width + "x" + _owner.Height + " (dirty " + dirtyRect.Width + "x" + dirtyRect.Height + " at " + dirtyRect.X + "," + dirtyRect.Y + ")");

                    Program.TrySend(new CefHostMessage(CefHostProtocol.Texture, _owner.Id)
                    {
                        Handle = forGame.ToInt64(), W = _owner.Width, H = _owner.Height,
                        X = dirtyRect.X, Y = dirtyRect.Y, Dx = dirtyRect.Width, Dy = dirtyRect.Height, Gen = _sharedHandles.Count,
                    });
                }
                catch (Exception ex)
                {
                    Program.Log("browser " + _owner.Id + ": accelerated paint failed: " + ex);
                }
            }

            public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
            {
                try
                {
                    if (type != PaintElementType.View || width <= 0 || height <= 0 || buffer == IntPtr.Zero) return;

                    var frame = _frame;
                    if (frame == null || frame.Width != width || frame.Height != height)
                    {
                        var name = CefHostProtocol.FrameBufferName(Process.GetCurrentProcess().Id, _owner.Id, ++_generation);
                        var fresh = CefFrameBuffer.Create(name, width, height);
                        fresh.Write(buffer, width * 4, width, height, 0, 0, width, height);
                        _frame = fresh;
                        frame?.Dispose(); // the game keeps its own handle until it switches
                        Program.Log("browser " + _owner.Id + ": frame buffer " + name + " (" + width + "x" + height + ")");
                        Program.TrySend(new CefHostMessage(CefHostProtocol.Frame, _owner.Id) { FrameName = name, W = width, H = height, Stride = fresh.Stride, Gen = _generation });
                    }
                    else
                    {
                        frame.Write(buffer, width * 4, width, height, dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
                    }

                    if (++_paints <= 3 && Program.Verbose)
                        Program.Log("browser " + _owner.Id + ": paint " + width + "x" + height + " (dirty " + dirtyRect.Width + "x" + dirtyRect.Height + " at " + dirtyRect.X + "," + dirtyRect.Y + ")");
                }
                catch (Exception ex)
                {
                    Program.Log("browser " + _owner.Id + ": paint failed: " + ex);
                }
            }

            public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) { }
            public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;
            public void UpdateDragCursor(DragOperationsMask operation) { }
            public void OnPopupShow(bool show) { }
            public void OnPopupSize(Rect rect) { }
            public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds) { }
            public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }

            public void Dispose()
            {
                _frame?.Dispose();
                _frame = null;
            }
        }
    }

    internal static class NativeMethods
    {
        public const uint ProcessDupHandle = 0x0040;
        public const uint DuplicateSameAccess = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);
    }

    /// <summary>
    /// Local-mode browsers (the ones resources create for their UI) only see the files of the resources:
    /// <c>https://&lt;resource&gt;/&lt;path&gt;</c> is served from the download folder, everything else is refused.
    /// Remote browsers keep the normal network stack.
    /// </summary>
    internal sealed class LocalResourceRequestHandler : RequestHandler
    {
        private readonly bool _localMode;
        private readonly LocalResourceHandlerFactory _factory = new LocalResourceHandlerFactory();

        public LocalResourceRequestHandler(bool localMode)
        {
            _localMode = localMode;
        }

        protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        {
            return _localMode ? _factory : null;
        }

        protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status, int errorCode, string errorMessage)
        {
            Program.Log("render process terminated: " + status + " (" + errorCode + ") " + errorMessage);
        }
    }

    internal sealed class LocalResourceHandlerFactory : ResourceRequestHandler
    {
        protected override IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
        {
            try
            {
                var url = request.Url ?? string.Empty;

                // data:/about: pages (loadHtml, blank pages) do not touch the disk.
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;

                if (Program.Verbose) Program.Log("[local] " + url);

                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                {
                    Program.Log("refused (not https://<resource>/<file>): " + url);
                    return ResourceHandler.ForErrorMessage("Only https://<resource>/<file> is allowed here", HttpStatusCode.Forbidden);
                }

                var root = Program.ResourceRoot;
                string file;
                if (string.IsNullOrEmpty(root) || !ResourceFileDownloader.TryGetLocalPath(root, uri.Host, Uri.UnescapeDataString(uri.AbsolutePath), out file))
                {
                    Program.Log("refused (bad path): " + url);
                    return ResourceHandler.ForErrorMessage("Bad path", HttpStatusCode.Forbidden);
                }

                if (!File.Exists(file))
                {
                    if (!file.EndsWith("favicon.ico", StringComparison.OrdinalIgnoreCase)) Program.Log("not found: " + file);
                    return ResourceHandler.ForErrorMessage("File not found: " + uri.Host + uri.AbsolutePath, HttpStatusCode.NotFound);
                }

                var extension = Path.GetExtension(file).TrimStart('.');
                var mime = Cef.GetMimeType(extension);
                if (string.IsNullOrEmpty(mime)) mime = "application/octet-stream";

                if (mime == "text/html" || extension.Equals("html", StringComparison.OrdinalIgnoreCase) || extension.Equals("htm", StringComparison.OrdinalIgnoreCase))
                {
                    // The page gets the resourceCall bridge as its first script, so scripts that run while the document
                    // loads can already call the client.
                    return ResourceHandler.FromByteArray(ResourceBridgeInjector.InjectIntoHtml(File.ReadAllBytes(file)), "text/html", "utf-8");
                }
                return ResourceHandler.FromFilePath(file, mime, true);
            }
            catch (Exception ex)
            {
                Program.Log("resource handler failed: " + ex);
                return ResourceHandler.ForErrorMessage("error", HttpStatusCode.InternalServerError);
            }
        }
    }

    /// <summary>Pop-ups (target=_blank, window.open) navigate the browser itself instead of opening a window.</summary>
    internal sealed class PopupToMainFrameLifeSpanHandler : LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName,
            WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null;
            if (!string.IsNullOrEmpty(targetUrl)) chromiumWebBrowser.Load(targetUrl);
            return true;
        }
    }

    internal sealed class NoContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
        {
            model.Clear();
        }
    }

    /// <summary>
    /// Defines <c>resourceCall(name, ...args)</c> and <c>resourceEval(code)</c> in every page. Pages run in Chromium's
    /// render process, so the functions post a message that this host forwards to the game as a "jsMessage" event;
    /// they return nothing. <c>gtan.call/gtan.eval</c> are the same functions under a namespace for new pages.
    /// </summary>
    internal sealed class ResourceBridgeInjector : IRenderProcessMessageHandler
    {
        internal const string Shim =
            "(function(){" +
            " if (window.resourceCall && window.gtan) return;" +
            " var post = function(m){ if (window.CefSharp && CefSharp.PostMessage) CefSharp.PostMessage(m); else if (window.cefSharp && cefSharp.postMessage) cefSharp.postMessage(m); };" +
            " window.resourceCall = function(name){ post({ type: 'resourceCall', name: String(name), args: Array.prototype.slice.call(arguments, 1) }); };" +
            " window.resourceEval = function(code){ post({ type: 'resourceEval', code: String(code) }); };" +
            " window.gtan = { call: window.resourceCall, eval: window.resourceEval };" +
            "})();";

        /// <summary>
        /// The shim as the first script of an HTML document. Pages of local-mode browsers are served by this host
        /// (LocalResourceHandlerFactory) and pages given as text (loadHtml) pass through here too, so in both cases
        /// resourceCall exists before the page's own scripts run; the asynchronous injections below only cover the rest.
        /// </summary>
        internal static byte[] InjectIntoHtml(byte[] html)
        {
            var text = Encoding.UTF8.GetString(html);
            return Encoding.UTF8.GetBytes(InjectIntoHtml(text));
        }

        internal static string InjectIntoHtml(string html)
        {
            if (string.IsNullOrEmpty(html) || html.IndexOf("resourceCall = function", StringComparison.Ordinal) >= 0) return html ?? string.Empty;

            var tag = "<script>" + Shim + "</script>";
            var at = IndexOfTagEnd(html, "<head");
            if (at < 0) at = IndexOfTagEnd(html, "<html");
            if (at < 0)
            {
                // No <head>/<html>: after a leading <!doctype ...>, else at the very start.
                var doctype = html.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
                at = doctype >= 0 ? html.IndexOf('>', doctype) + 1 : 0;
                if (at <= 0) return tag + html;
            }
            return html.Substring(0, at) + tag + html.Substring(at);
        }

        /// <summary>Index just after the closing '>' of the first <paramref name="tag"/> (e.g. "&lt;head"), or -1.</summary>
        private static int IndexOfTagEnd(string html, string tag)
        {
            var start = 0;
            while (true)
            {
                var i = html.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                var after = i + tag.Length;
                // "<head" must not match "<header"
                if (after < html.Length && (html[after] == '>' || char.IsWhiteSpace(html[after]) || html[after] == '/'))
                {
                    var close = html.IndexOf('>', after);
                    return close < 0 ? -1 : close + 1;
                }
                start = after;
            }
        }

        private readonly HostedBrowser _owner;

        public ResourceBridgeInjector(HostedBrowser owner)
        {
            _owner = owner;
        }

        public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
        {
            frame?.ExecuteJavaScriptAsync(Shim, "gtan://bridge", 0);
        }

        public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }
        public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node) { }

        public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception)
        {
            Program.Notify("Browser " + _owner.Id + ": page exception in " + (frame != null ? frame.Url : "?") + ": " + exception.Message);
        }
    }
}

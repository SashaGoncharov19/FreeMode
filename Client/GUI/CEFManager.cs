using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using CefSharp;
using CefSharp.OffScreen;
using GTA;
using GTA.Native;
using GTANetwork.GUI.DirectXHook.Hook;
using GTANetwork.GUI.DirectXHook.Hook.Common;
using GTANetwork.Javascript;
using GTANetwork.Util;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using SharpDX;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace GTANetwork.GUI
{
    /// <summary>Feeds mouse and keyboard input of the game to the browsers while the cursor is shown.</summary>
    public class CefController : Script
    {
        private static bool _showCursor;

        public static bool ShowCursor
        {
            get => _showCursor;
            set
            {
                if (!_showCursor && value)
                {
                    _justShownCursor = true;
                    _lastShownCursor = Util.Util.TickCount;
                }
                _showCursor = value;

                CEFManager.SetMouseHidden(!value);
            }
        }

        private static bool _justShownCursor;
        private static long _lastShownCursor = 0;
        public static PointF _lastMousePoint;
        private Keys _lastKey;

        public static CefEventFlags GetMouseModifiers(bool leftbutton, bool rightButton)
        {
            CefEventFlags mod = CefEventFlags.None;

            if (leftbutton) mod |= CefEventFlags.LeftMouseButton;
            if (rightButton) mod |= CefEventFlags.RightMouseButton;

            return mod;
        }

        private static Browser[] SnapshotBrowsers()
        {
            lock (CEFManager.Browsers) return CEFManager.Browsers.ToArray();
        }

        public CefController()
        {
            Tick += (sender, args) =>
            {
                if (!CefUtil.DISABLE_CEF && ShowCursor)
                {
                    Game.DisableAllControlsThisFrame();

                    var res = Main.screen;
                    var mouseX = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.CursorX) * res.Width;
                    var mouseY = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.CursorY) * res.Height;

                    _lastMousePoint = new PointF(mouseX, mouseY);

                    if (CEFManager._cursor != null)
                    {
                        CEFManager._cursor.Location = new Point((int)mouseX, (int)mouseY);
                    }

                    var mouseDown = Game.IsDisabledControlJustPressed(GTA.Control.CursorAccept);
                    var mouseDownRN = Game.IsDisabledControlPressed(GTA.Control.CursorAccept);
                    var mouseUp = Game.IsDisabledControlJustReleased(GTA.Control.CursorAccept);

                    var rmouseDown = Game.IsDisabledControlJustPressed(GTA.Control.CursorCancel);
                    var rmouseDownRN = Game.IsDisabledControlPressed(GTA.Control.CursorCancel);
                    var rmouseUp = Game.IsDisabledControlJustReleased(GTA.Control.CursorCancel);

                    var wumouseDown = Game.IsDisabledControlPressed(GTA.Control.CursorScrollUp);
                    var wdmouseDown = Game.IsDisabledControlPressed(GTA.Control.CursorScrollDown);

                    foreach (var browser in SnapshotBrowsers())
                    {
                        var host = browser.Host;
                        if (host == null) continue;

                        if (!browser._hasFocused)
                        {
                            host.SetFocus(true);
                            browser._hasFocused = true;
                        }

                        if (mouseX > browser.Position.X && mouseY > browser.Position.Y &&
                            mouseX < browser.Position.X + browser.Size.Width &&
                            mouseY < browser.Position.Y + browser.Size.Height)
                        {
                            var ev = new MouseEvent((int)(mouseX - browser.Position.X), (int)(mouseY - browser.Position.Y),
                                GetMouseModifiers(mouseDownRN, rmouseDownRN));

                            host.SendMouseMoveEvent(ev, false);

                            if (mouseDown) host.SendMouseClickEvent(ev, MouseButtonType.Left, false, 1);
                            if (mouseUp) host.SendMouseClickEvent(ev, MouseButtonType.Left, true, 1);
                            if (rmouseDown) host.SendMouseClickEvent(ev, MouseButtonType.Right, false, 1);
                            if (rmouseUp) host.SendMouseClickEvent(ev, MouseButtonType.Right, true, 1);
                            if (wdmouseDown) host.SendMouseWheelEvent(ev, 0, -30);
                            if (wumouseDown) host.SendMouseWheelEvent(ev, 0, 30);
                        }
                    }
                }
                else if (ShowCursor)
                {
                    Function.Call(Hash._SHOW_CURSOR_THIS_FRAME);
                }
            };

            KeyDown += (sender, args) =>
            {
                if (!ShowCursor) return;

                if (_justShownCursor && Util.Util.TickCount - _lastShownCursor < 500)
                {
                    _justShownCursor = false;
                    return;
                }

                if (CefUtil.DISABLE_CEF) return;

                foreach (var browser in SnapshotBrowsers())
                {
                    var host = browser.Host;
                    if (host == null) continue;

                    CefEventFlags mod = CefEventFlags.None;
                    if (args.Control) mod |= CefEventFlags.ControlDown;
                    if (args.Shift) mod |= CefEventFlags.ShiftDown;
                    if (args.Alt) mod |= CefEventFlags.AltDown;

                    host.SendKeyEvent(new KeyEvent
                    {
                        Type = KeyEventType.KeyDown,
                        Modifiers = mod,
                        WindowsKeyCode = (int)args.KeyCode,
                        NativeKeyCode = (int)args.KeyValue,
                    });

                    var key = args.KeyCode;

                    if ((key == Keys.ShiftKey && _lastKey == Keys.Menu) ||
                        (key == Keys.Menu && _lastKey == Keys.ShiftKey))
                    {
                        ClassicChat.ActivateKeyboardLayout(1, 0);
                        return;
                    }

                    _lastKey = key;

                    if (key == Keys.Escape)
                    {
                        return;
                    }

                    var keyChar = ClassicChat.GetCharFromKey(key, Game.IsKeyPressed(Keys.ShiftKey), Game.IsKeyPressed(Keys.Menu) && Game.IsKeyPressed(Keys.ControlKey));

                    if (keyChar.Length == 0 || keyChar[0] == 27) return;

                    host.SendKeyEvent(new KeyEvent
                    {
                        Type = KeyEventType.Char,
                        Modifiers = mod,
                        WindowsKeyCode = keyChar[0],
                    });
                }
            };

            KeyUp += (sender, args) =>
            {
                if (CefUtil.DISABLE_CEF || !ShowCursor) return;

                foreach (var browser in SnapshotBrowsers())
                {
                    var host = browser.Host;
                    if (host == null) continue;

                    host.SendKeyEvent(new KeyEvent
                    {
                        Type = KeyEventType.KeyUp,
                        WindowsKeyCode = (int)args.KeyCode,
                    });
                }
            };
        }
    }

    /// <summary>
    /// Chromium Embedded Framework through CefSharp (off-screen rendering). The game is the browser process; page
    /// rendering and the GPU run in CefSharp.BrowserSubprocess.exe processes. All CEF files live in
    /// &lt;install dir&gt;\cef, which is why the assembly resolver and the DLL directory are set up before anything
    /// from CefSharp is touched.
    /// </summary>
    internal static class CEFManager
    {
        private static Thread _cefThread;
        private static readonly ManualResetEvent CefReady = new ManualResetEvent(false);
        private static readonly ManualResetEvent CefShutdown = new ManualResetEvent(false);
        private static bool _cefInitialised;
        private static bool _resolverRegistered;
        private static string _cefDirectory;
        private static object _libcefHandle; // CefLibraryHandle, kept alive for the life of the process

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        internal static string CefDirectory
        {
            get
            {
                if (_cefDirectory == null)
                {
                    _cefDirectory = Path.Combine(Main.GTANInstallDir.TrimEnd('\\', '/'), "cef");
                }
                return _cefDirectory;
            }
        }

        /// <summary>
        /// CefSharp.Core.Runtime.dll (C++/CLI) and the browser subprocess are in the cef folder, not next to
        /// GTANetwork.dll, so the CLR needs help finding them. Call before the first method that uses CefSharp runs.
        /// </summary>
        internal static void RegisterAssemblyResolver()
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var name = new AssemblyName(args.Name).Name;
                    if (!name.StartsWith("CefSharp", StringComparison.OrdinalIgnoreCase)) return null;

                    var candidate = Path.Combine(CefDirectory, name + ".dll");
                    if (!File.Exists(candidate)) return null;

                    LogManager.CefLog("-> Resolving " + name + " from " + candidate);
                    return Assembly.LoadFrom(candidate);
                }
                catch (Exception ex)
                {
                    LogManager.CefLog(ex, "CEF ASSEMBLY RESOLVE");
                    return null;
                }
            };
        }

        internal static void InitializeCef()
        {
            if (CefUtil.DISABLE_CEF) return;

            RegisterAssemblyResolver();

            _cefThread = new Thread(CefThread) { IsBackground = true, Name = "GTAN CEF" };
            _cefThread.SetApartmentState(ApartmentState.STA);
            _cefThread.Start();
        }

        /// <summary>True once CEF is up (or has definitely failed); browsers wait for it.</summary>
        internal static bool WaitUntilReady(int timeoutMs)
        {
            return CefReady.WaitOne(timeoutMs) && _cefInitialised;
        }

        private static void CefThread()
        {
            try
            {
                var cefDir = CefDirectory;
                LogManager.CefLog("--> InitializeCef: CEF directory " + cefDir);

                if (!File.Exists(Path.Combine(cefDir, "libcef.dll")) || !File.Exists(Path.Combine(cefDir, "CefSharp.BrowserSubprocess.exe")))
                {
                    LogManager.CefLog("libcef.dll or CefSharp.BrowserSubprocess.exe is missing in " + cefDir + "; CEF stays disabled");
                    CefUtil.DISABLE_CEF = true;
                    return;
                }

                // libcef.dll is imported by CefSharp.Core.Runtime.dll; the loader only looks next to GTA5.exe and on PATH.
                if (!SetDllDirectory(cefDir))
                {
                    LogManager.CefLog("SetDllDirectory failed: " + Marshal.GetLastWin32Error());
                }

                InitializeCefRuntime(cefDir);
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "cef initialization");
                CefUtil.DISABLE_CEF = true;
            }
            finally
            {
                CefReady.Set();
            }

            // Shutdown must run on the thread that called Initialize.
            CefShutdown.WaitOne();

            try
            {
                if (_cefInitialised) ShutdownCefRuntime();
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "cef shutdown");
            }
        }

        // Kept out of CefThread so that the JIT only touches CefSharp types after the resolver and DLL directory are set.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitializeCefRuntime(string cefDir)
        {
            var libcef = new CefLibraryHandle(Path.Combine(cefDir, "libcef.dll"));
            if (libcef.IsInvalid)
            {
                LogManager.CefLog("libcef.dll could not be pre-loaded (error " + Marshal.GetLastWin32Error() + ")");
            }
            _libcefHandle = libcef;

            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
            CefSharpSettings.ShutdownOnExit = false;

            var playerSettings = Main.PlayerSettings;
            var gpu = playerSettings != null && playerSettings.CefGpu;

            var settings = new CefSettings
            {
                BrowserSubprocessPath = Path.Combine(cefDir, "CefSharp.BrowserSubprocess.exe"),
                CachePath = Path.Combine(cefDir, "cache"),
                LocalesDirPath = Path.Combine(cefDir, "locales"),
                ResourcesDirPath = cefDir,
                LogFile = Path.Combine(LogManager.LogDirectory, "CEF-chromium.log"),
                LogSeverity = LogSeverity.Warning,
                MultiThreadedMessageLoop = true,
                WindowlessRenderingEnabled = true,
                BackgroundColor = 0,
                IgnoreCertificateErrors = false,
            };

            if (playerSettings != null && playerSettings.CEFDevtool) settings.RemoteDebuggingPort = 9222;

            // Software rendering by default, exactly what the old single-process browser did; <cefgpu>true</cefgpu>
            // in settings.xml keeps the GPU process (worth trying, it is a separate process now).
            if (!gpu)
            {
                settings.CefCommandLineArgs.Add("disable-gpu");
                settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            }
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            if (Main.EnableMediaStream) settings.CefCommandLineArgs.Add("enable-media-stream");

            var ok = Cef.Initialize(settings, false, (IBrowserProcessHandler)null);
            _cefInitialised = ok;

            if (ok)
            {
                LogManager.CefLog("CEF initialised: Chromium " + Cef.ChromiumVersion + ", CEF " + Cef.CefVersion + ", CefSharp " + Cef.CefSharpVersion +
                                  ", " + (gpu ? "GPU process on" : "software rendering") + ", subprocess " + settings.BrowserSubprocessPath);
            }
            else
            {
                LogManager.CefLog("CEF FAILED to initialise, see logs\\CEF-chromium.log");
                CefUtil.DISABLE_CEF = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ShutdownCefRuntime()
        {
            Cef.Shutdown();
        }

        internal static void DisposeCef()
        {
            if (CefUtil.DISABLE_CEF && !_cefInitialised) return;

            CefShutdown.Set();
            _cefThread?.Join(3000);
        }

        internal static void Dispose()
        {
            _cursor?.Dispose();
            _cursor = null;

            DirectXHook?.Dispose();
            DirectXHook = null;
        }

        internal static void SetMouseHidden(bool hidden)
        {
            if (DirectXHook == null) return;

            if (_cursor == null)
            {
                var cursorPic = new Bitmap(Main.GTANInstallDir + "images\\cef\\cursor.png");
                _cursor = new ImageElement(null, true);
                _cursor.SetBitmap(cursorPic);
                _cursor.Hidden = true;
                DirectXHook.AddImage(_cursor, 1);
            }

            _cursor.Hidden = hidden;
        }

        internal static void Initialize(Size screenSize)
        {
            ScreenSize = screenSize;
            if (CefUtil.DISABLE_CEF || DirectXHook != null) return;

            // SharpDX debugging aids, both off on purpose: object tracking records a stack trace for every COM
            // wrapper (one per frame in the overlay), and release-on-finalizer turns every wrapper built from a raw
            // pointer we do not own (the game's swap chain) into a Release() of somebody else's reference.
            Configuration.EnableObjectTracking = false;
            Configuration.EnableReleaseOnFinalizer = false;
            Configuration.EnableTrackingReleaseOnFinalizer = false;

            try
            {
                LogManager.CefLog("--> Initiatlize: Creating device");
                DirectXHook = new DXHookD3D11(screenSize.Width, screenSize.Height);
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "DIRECTX START");
            }
        }

        internal static readonly List<Browser> Browsers = new List<Browser>();
        internal static Size ScreenSize;
        internal static ImageElement _cursor;
        internal static bool Draw = false;

        internal static DXHookD3D11 DirectXHook;
    }

    /// <summary>
    /// The page side calls the client script through <c>resourceCall(name, ...args)</c>; this runs the call on the
    /// script thread. Calls are one-way: the page gets no return value (CEF pages live in another process).
    /// </summary>
    public class BrowserJavascriptCallback
    {
        private static readonly Regex FunctionName = new Regex(@"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.Compiled);

        private readonly V8ScriptEngine _parent;
        private readonly Browser _wrapper;

        public BrowserJavascriptCallback(V8ScriptEngine parent, Browser wrapper)
        {
            _parent = parent;
            _wrapper = wrapper;
        }

        public BrowserJavascriptCallback() { }

        internal void Invoke(string functionName, object[] arguments)
        {
            if (_parent == null || _wrapper == null || !_wrapper._localMode) return;

            if (string.IsNullOrEmpty(functionName) || !FunctionName.IsMatch(functionName))
            {
                LogManager.CefLog("-> resourceCall refused: '" + functionName + "' is not a function name");
                return;
            }

            var call = new StringBuilder(functionName).Append('(');
            if (arguments != null)
            {
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i > 0) call.Append(", ");
                    call.Append(ToLiteral(arguments[i]));
                }
            }
            call.Append(");");

            var code = call.ToString();
            Queue(() => _parent.Evaluate(code));
        }

        internal void Run(string code)
        {
            if (_parent == null || _wrapper == null || !_wrapper._localMode || string.IsNullOrEmpty(code)) return;
            Queue(() => _parent.Evaluate(code));
        }

        private static void Queue(Action action)
        {
            lock (JavascriptHook.ThreadJumper)
            {
                JavascriptHook.ThreadJumper.Add(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        LogManager.LogException(ex, "PAGE -> CLIENT SCRIPT");
                    }
                });
            }
        }

        internal static string ToLiteral(object value)
        {
            if (value == null) return "null";
            if (value is string) return System.Web.HttpUtility.JavaScriptStringEncode((string)value, true);
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong || value is float || value is double || value is decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is System.Collections.IEnumerable) return JsonConvert.SerializeObject(value);
            return System.Web.HttpUtility.JavaScriptStringEncode(value.ToString(), true);
        }

        public object call(string functionName, params object[] arguments)
        {
            Invoke(functionName, arguments);
            return null;
        }

        public object eval(string code)
        {
            Run(code);
            return null;
        }

        public void addEventHandler(string eventName, Action<object[]> action)
        {
            if (_wrapper == null || !_wrapper._localMode) return;
            _eventHandlers.Add(new Tuple<string, Action<object[]>>(eventName, action));
        }

        internal void TriggerEvent(string eventName, params object[] arguments)
        {
            foreach (var handler in _eventHandlers)
            {
                if (handler.Item1 == eventName)
                    handler.Item2.Invoke(arguments);
            }
        }

        private readonly List<Tuple<string, Action<object[]>>> _eventHandlers = new List<Tuple<string, Action<object[]>>>();
    }

    /// <summary>One off-screen browser: a CefSharp control whose frames land in the DirectX overlay.</summary>
    public class Browser : IDisposable
    {
        internal ChromiumWebBrowser _browser;
        internal OverlayRenderHandler _render;
        internal BrowserJavascriptCallback _callback;

        internal readonly bool _localMode;
        internal bool _hasFocused;
        private int _messagesLogged;

        private bool _headless;
        private Point _position;
        private Size _size;

        /// <summary>The CEF browser host for input, or null until the browser exists.</summary>
        public IBrowserHost Host
        {
            get
            {
                try
                {
                    var b = _browser;
                    return b != null && !b.IsDisposed && b.IsBrowserInitialized ? b.GetBrowserHost() : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public IBrowserHost GetHost()
        {
            return Host;
        }

        public bool Headless
        {
            get { return _headless; }
            set
            {
                _headless = value;
                _render?.SetHidden(value);
            }
        }

        public Point Position
        {
            get { return _position; }
            set
            {
                _position = value;
                _render?.SetPosition(value.X, value.Y);
            }
        }

        public PointF[] Pinned { get; set; }

        public Size Size
        {
            get { return _size; }
            set
            {
                _size = value;
                _render?.SetSize(value.Width, value.Height);
                var b = _browser;
                if (b != null && !b.IsDisposed) b.Size = value;
            }
        }

        internal Browser(V8ScriptEngine father, Size browserSize, bool localMode)
        {
            _localMode = localMode;
            _size = browserSize;

            if (CefUtil.DISABLE_CEF) return;

            LogManager.CefLog("--> Browser: Start (" + browserSize.Width + "x" + browserSize.Height + ", " + (localMode ? "local" : "remote") + ")");

            if (!CEFManager.WaitUntilReady(15000))
            {
                LogManager.CefLog("--> Browser: CEF is not initialised, no browser created");
                return;
            }

            _callback = new BrowserJavascriptCallback(father, this);
            _render = new OverlayRenderHandler(browserSize.Width, browserSize.Height);

            try
            {
                CreateBrowser(browserSize, localMode);
                LogManager.CefLog("--> Browser: End");
            }
            catch (Exception e)
            {
                LogManager.CefLog(e, "CreateBrowser");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateBrowser(Size browserSize, bool localMode)
        {
            var frameRate = Main.PlayerSettings != null && Main.PlayerSettings.CefFrameRate > 0 ? Math.Min(60, Main.PlayerSettings.CefFrameRate) : 30;

            var settings = new BrowserSettings(true)
            {
                WindowlessFrameRate = frameRate,
                BackgroundColor = 0,
                JavascriptCloseWindows = CefState.Disabled,
            };

            _browser = new ChromiumWebBrowser(string.Empty, settings, null, false, null, false)
            {
                Size = browserSize,
                RenderHandler = _render,
                RequestHandler = new LocalResourceRequestHandler(localMode),
                LifeSpanHandler = new PopupToMainFrameLifeSpanHandler(),
                MenuHandler = new NoContextMenuHandler(),
                RenderProcessMessageHandler = new ResourceBridgeInjector(),
            };

            _browser.BrowserInitialized += (sender, args) => LogManager.CefLog("-> Browser created!");
            _browser.FrameLoadStart += (sender, args) =>
            {
                if (args.Frame != null && args.Frame.IsMain) LogManager.CefLog("-> Start: " + args.Url);
                // The bridge is also injected from OnContextCreated; this covers pages whose scripts run before that message arrives.
                args.Frame?.ExecuteJavaScriptAsync(ResourceBridgeInjector.Shim, "gtan://bridge", 0);
            };
            _browser.FrameLoadEnd += (sender, args) =>
            {
                if (args.Frame != null && args.Frame.IsMain) LogManager.CefLog("-> End: " + args.Url + ", " + args.HttpStatusCode);
            };
            _browser.LoadError += (sender, args) => LogManager.CefLog("-> Load error " + args.ErrorCode + " (" + args.ErrorText + ") for " + args.FailedUrl);
            _browser.ConsoleMessage += (sender, args) =>
            {
                // Errors and warnings of the page always, everything else in debug mode.
                var text = "-> Page console [" + args.Level + "] " + args.Message + " (" + args.Source + ":" + args.Line + ")";
                if (args.Level == LogSeverity.Error || args.Level == LogSeverity.Fatal || args.Level == LogSeverity.Warning) LogManager.CefLog(text);
                else LogManager.VerboseCefLog(text);
            };
            _browser.JavascriptMessageReceived += OnJavascriptMessage;

            LogManager.CefLog("--> Browser: Creating Browser");
            _browser.CreateBrowser(null, settings);
        }

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

                    if (_messagesLogged < 5 && LogManager.Verbose)
                    {
                        _messagesLogged++;
                        LogManager.CefLog("-> resourceCall " + name + " (" + args.Length + " argument(s))");
                    }

                    _callback?.Invoke(name, args);
                }
                else if (type == "resourceEval")
                {
                    object codeObj;
                    message.TryGetValue("code", out codeObj);
                    var code = codeObj as string;
                    if (!string.IsNullOrEmpty(code)) _callback?.Run(code);
                }
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "PAGE MESSAGE");
            }
        }

        public void eval(string code)
        {
            if (!_localMode || CefUtil.DISABLE_CEF) return;

            var b = _browser;
            if (b == null || b.IsDisposed || !b.IsBrowserInitialized) return;
            b.ExecuteScriptAsync(code);
        }

        public void call(string method, params object[] arguments)
        {
            if (!_localMode || CefUtil.DISABLE_CEF) return;

            var callString = new StringBuilder(method).Append('(');
            if (arguments != null)
            {
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i > 0) callString.Append(", ");
                    callString.Append(BrowserJavascriptCallback.ToLiteral(arguments[i]));
                }
            }
            callString.Append(");");

            eval(callString.ToString());
        }

        internal void GoToPage(string page)
        {
            if (CefUtil.DISABLE_CEF) return;

            var b = _browser;
            if (b == null || b.IsDisposed) return;

            LogManager.CefLog("Trying to load page " + page + "...");
            b.Load(page);
        }

        internal void GoBack()
        {
            if (CefUtil.DISABLE_CEF) return;

            var b = _browser;
            if (b == null || b.IsDisposed || !b.CanGoBack) return;

            LogManager.CefLog("Trying to go back a page...");
            b.Back();
        }

        internal void Close()
        {
            if (CefUtil.DISABLE_CEF) return;

            _render?.Dispose();
            _render = null;

            var b = _browser;
            _browser = null;
            b?.Dispose();
        }

        internal void LoadHtml(string html)
        {
            if (CefUtil.DISABLE_CEF) return;

            var b = _browser;
            if (b == null || b.IsDisposed) return;

            b.Load("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html ?? string.Empty)));
        }

        internal string GetAddress()
        {
            if (CefUtil.DISABLE_CEF) return null;

            var b = _browser;
            return b == null || b.IsDisposed ? null : b.Address;
        }

        internal bool IsLoading()
        {
            if (CefUtil.DISABLE_CEF) return false;

            var b = _browser;
            return b != null && !b.IsDisposed && b.IsLoading;
        }

        internal bool IsInitialized()
        {
            if (CefUtil.DISABLE_CEF) return true;

            var b = _browser;
            return b != null && !b.IsDisposed && b.IsBrowserInitialized;
        }

        internal void SetFocus(bool focus)
        {
            var host = Host;
            if (host == null) return;

            host.SetFocus(focus);
            _hasFocused = focus;
        }

        internal void SetFrameRate(int fps)
        {
            var host = Host;
            if (host != null) host.WindowlessFrameRate = Math.Max(1, Math.Min(60, fps));
        }

        public void Dispose()
        {
            _browser = null;
        }
    }
}

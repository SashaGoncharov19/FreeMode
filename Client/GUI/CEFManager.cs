using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTANetwork.GUI.DirectXHook.Hook;
using GTANetwork.GUI.DirectXHook.Hook.Common;
using GTANetwork.Javascript;
using GTANetwork.Streamer;
using GTANetwork.Util;
using GTANetworkShared.Cef;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using SharpDX;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace GTANetwork.GUI
{
    // Input types with the names and values of CefSharp's (cef_event_flags_t & co.), so the browser host maps them 1:1.
    [Flags]
    public enum CefEventFlags
    {
        None = 0, CapsLockOn = 1, ShiftDown = 2, ControlDown = 4, AltDown = 8, LeftMouseButton = 16, MiddleMouseButton = 32,
        RightMouseButton = 64, CommandDown = 128, NumLockOn = 256, IsKeyPad = 512, IsLeft = 1024, IsRight = 2048,
    }

    public enum MouseButtonType { Left = 0, Middle = 1, Right = 2 }

    public enum KeyEventType { RawKeyDown = 0, KeyDown = 1, KeyUp = 2, Char = 3 }

    public struct MouseEvent
    {
        public int X;
        public int Y;
        public CefEventFlags Modifiers;

        public MouseEvent(int x, int y, CefEventFlags modifiers)
        {
            X = x;
            Y = y;
            Modifiers = modifiers;
        }
    }

    public struct KeyEvent
    {
        public KeyEventType Type;
        public CefEventFlags Modifiers;
        public int WindowsKeyCode;
        public int NativeKeyCode;
    }

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

        /// <summary>Keys that can type a character (letters, digits, punctuation, space, Enter, Backspace, Tab, numpad).</summary>
        internal static bool ProducesCharacter(Keys key)
        {
            switch (key)
            {
                case Keys.Back:
                case Keys.Tab:
                case Keys.Return:
                case Keys.Space:
                    return true;
            }
            if (key >= Keys.D0 && key <= Keys.Z) return true;                      // digits and letters
            if (key >= Keys.NumPad0 && key <= Keys.Divide) return true;             // numpad digits and operators
            if (key >= Keys.Oem1 && key <= Keys.Oem102) return true;                // punctuation, brackets, quotes
            if (key == Keys.OemSemicolon || key == Keys.Oemplus || key == Keys.Oemcomma || key == Keys.OemMinus || key == Keys.OemPeriod || key == Keys.OemQuestion || key == Keys.Oemtilde) return true;
            return false;
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

                    // WM_KEYDOWN; the character, if the key produces one, follows as a separate Char event (WM_CHAR).
                    host.SendKeyEvent(new KeyEvent
                    {
                        Type = KeyEventType.RawKeyDown,
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

                    // Modifier, lock, function and navigation keys never type anything (Caps Lock used to put a
                    // character into the page). Ctrl+letter shortcuts are handled from the key-down event too.
                    if (!ProducesCharacter(key) || Game.IsKeyPressed(Keys.ControlKey) && !Game.IsKeyPressed(Keys.Menu)) return;

                    var keyChar = ClassicChat.GetCharFromKey(key, Game.IsKeyPressed(Keys.ShiftKey), Game.IsKeyPressed(Keys.Menu) && Game.IsKeyPressed(Keys.ControlKey));

                    if (keyChar.Length == 0) return;
                    var c = keyChar[0];
                    if (c < 0x20 && c != '\b' && c != '\r' && c != '\t') return; // control characters, Escape, ...

                    host.SendKeyEvent(new KeyEvent
                    {
                        Type = KeyEventType.Char,
                        Modifiers = mod,
                        WindowsKeyCode = c,
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
    /// The browser side of the game. Chromium runs in its own process, cef\GTANetwork.CefHost.exe (CefSharp cannot live
    /// inside GTA5.exe: ScriptHookVDotNet runs this client in a second AppDomain and CefSharp's C++/CLI callbacks from
    /// Chromium's threads land in the default one). This class starts the host, speaks the protocol of
    /// GTANetworkShared.Cef with it (commands down its stdin, events up its stdout) and pumps every browser's frames
    /// from shared memory into the DirectX overlay.
    /// </summary>
    internal static class CEFManager
    {
        private static Process _host;
        private static CefHostChannel _channel;
        private static Thread _reader;
        private static Thread _stderr;
        private static Thread _framePump;
        private static readonly ManualResetEvent CefReady = new ManualResetEvent(false);
        private static readonly ManualResetEvent StopPump = new ManualResetEvent(false);
        private static bool _cefInitialised;
        private static bool _startAttempted;
        private static string _cefDirectory;
        private static int _nextBrowserId;
        private static int _sendErrorsLogged;

        /// <summary>Set once a shared texture could not be opened on the game's device: every later browser uses CPU frames.</summary>
        internal static bool SharedTexturesBroken;

        /// <summary>Frames as D3D11 shared textures: GPU on, the setting on, and no failure so far this session.</summary>
        internal static bool WantSharedTextures
        {
            get
            {
                var s = Main.PlayerSettings;
                return s != null && s.CefGpu && s.CefSharedTexture && !SharedTexturesBroken;
            }
        }
        private static readonly Dictionary<int, Browser> ById = new Dictionary<int, Browser>();
        private static readonly object InitLock = new object();
        private static readonly List<Action> WhenReady = new List<Action>();

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

        internal static string HostExecutable => Path.Combine(CefDirectory, "GTANetwork.CefHost.exe");

        /// <summary>
        /// Starts the browser host (and with it Chromium) in the background. Called at game start only with
        /// &lt;CefPreload&gt;true&lt;/CefPreload&gt;; otherwise the first browser a resource creates starts it (a second
        /// or two once per game session), so a player on servers without browser UIs never runs Chromium at all.
        /// </summary>
        internal static void InitializeCef()
        {
            if (CefUtil.DISABLE_CEF) return;

            lock (InitLock)
            {
                if (_startAttempted) return;
                _startAttempted = true;

                try
                {
                    StartHost();
                }
                catch (Exception ex)
                {
                    LogManager.CefLog(ex, "CEF HOST START");
                    Fail("the browser host could not be started: " + ex.Message);
                }
            }
        }

        private static void StartHost()
        {
            var cefDir = CefDirectory;
            var exe = HostExecutable;
            LogManager.CefLog("--> Starting the browser host " + exe);

            if (!File.Exists(exe) || !File.Exists(Path.Combine(cefDir, "libcef.dll")))
            {
                Fail("GTANetwork.CefHost.exe or libcef.dll is missing in " + cefDir + "; CEF stays disabled");
                return;
            }

            var settings = Main.PlayerSettings;
            var args = new StringBuilder();
            Argument(args, "--parent", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            Argument(args, "--log", Path.Combine(LogManager.LogDirectory, "CEF-host.log"));
            Argument(args, "--chromium-log", Path.Combine(LogManager.LogDirectory, "CEF-chromium.log"));
            Argument(args, "--cache", Path.Combine(cefDir, "cache"));
            Argument(args, "--resource-root", FileTransferId._DOWNLOADFOLDER_.TrimEnd('\\', '/'));
            if (settings != null && settings.CefGpu) args.Append(" --gpu");
            if (settings != null && !settings.CefInProcessGpu) args.Append(" --gpu-process");
            if (Main.EnableMediaStream) args.Append(" --media-stream");
            if (LogManager.Verbose) args.Append(" --verbose");
            if (settings != null && settings.CEFDevtool) Argument(args, "--devtools", "9222");

            var psi = new ProcessStartInfo(exe, args.ToString().Trim())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cefDir,
            };
            // Under Wine/Proton the game may run with heavy Wine tracing (play.sh --debug: PROTON_LOG=1). Chromium's
            // processes must not inherit it: a dozen processes doing stack walks and exceptions through the tracer
            // write hundreds of MB of Wine log and crawl. The host keeps its own logs. GTAN_CEF_WINEDEBUG overrides.
            psi.Environment["WINEDEBUG"] = Environment.GetEnvironmentVariable("GTAN_CEF_WINEDEBUG") ?? "-all";
            LogManager.CefLog("--> Host arguments: " + psi.Arguments + " (WINEDEBUG=" + psi.Environment["WINEDEBUG"] + ")");

            var host = Process.Start(psi);
            if (host == null) throw new InvalidOperationException("Process.Start returned null");
            _host = host;
            host.EnableRaisingEvents = true;
            host.Exited += (sender, e) => OnHostExited(host);

            _channel = new CefHostChannel(host.StandardOutput.BaseStream, host.StandardInput.BaseStream);
            _reader = new Thread(ReadEvents) { IsBackground = true, Name = "GTAN CEF host events" };
            _reader.Start();
            _stderr = new Thread(() => ReadStderr(host)) { IsBackground = true, Name = "GTAN CEF host stderr" };
            _stderr.Start();
            LogManager.CefLog("--> Browser host started, pid " + host.Id);
        }

        private static void Argument(StringBuilder args, string name, string value)
        {
            args.Append(' ').Append(name).Append(' ');
            // Quote the way CommandLineToArgvW un-quotes; our paths never end in a backslash.
            if (value.Length == 0 || value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0) args.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
            else args.Append(value);
        }

        private static void ReadEvents()
        {
            try
            {
                CefHostMessage message;
                while ((message = _channel.Receive()) != null)
                {
                    try
                    {
                        Dispatch(message);
                    }
                    catch (Exception ex)
                    {
                        LogManager.CefLog(ex, "CEF HOST EVENT " + message.Type);
                    }
                }
                LogManager.CefLog("--> The browser host closed its channel");
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF HOST CHANNEL");
            }
            finally
            {
                HostGone();
            }
        }

        private static void ReadStderr(Process host)
        {
            try
            {
                string line;
                var logged = 0;
                while ((line = host.StandardError.ReadLine()) != null)
                {
                    // Chromium's own stderr (Wine stubs, GPU fallbacks): the first lines are worth keeping, the rest is noise.
                    if (logged++ < 20 || LogManager.Verbose) LogManager.CefLog("[host stderr] " + line);
                }
            }
            catch
            {
            }
        }

        private static void Dispatch(CefHostMessage m)
        {
            switch (m.Type)
            {
                case CefHostProtocol.Ready:
                    LogManager.CefLog("CEF initialised: Chromium " + m.Chromium + ", CEF " + m.CefVersion + ", CefSharp " + m.CefSharp + " (" + m.Text + ") in " + HostExecutable);
                    _cefInitialised = true;
                    StartFramePump();
                    SignalReady();
                    return;
                case CefHostProtocol.InitFailed:
                    LogManager.CefLog("CEF FAILED to initialise: " + m.Text + " (see logs\\CEF-host.log and logs\\CEF-chromium.log)");
                    Fail(m.Text);
                    return;
                case CefHostProtocol.Log:
                    LogManager.CefLog("[host] " + m.Text);
                    return;
            }

            Browser browser;
            lock (ById) ById.TryGetValue(m.Id, out browser);
            if (browser != null) browser.OnHostEvent(m);
            else if (m.Type != CefHostProtocol.Closed && m.Type != CefHostProtocol.Loading) LogManager.VerboseCefLog("-> Event " + m.Type + " for unknown browser " + m.Id);
        }

        private static void Fail(string reason)
        {
            LogManager.CefLog("--> CEF is not available: " + reason);
            _cefInitialised = false;
            CefUtil.DISABLE_CEF = true;
            SignalReady();
        }

        private static void SignalReady()
        {
            Action[] pending;
            lock (InitLock)
            {
                CefReady.Set();
                pending = WhenReady.ToArray();
                WhenReady.Clear();
            }
            foreach (var action in pending) RunReadyAction(action);
        }

        private static void OnHostExited(Process host)
        {
            int code;
            try { code = host.ExitCode; } catch { code = -1; }
            LogManager.CefLog("--> The browser host exited with code " + code);
        }

        private static void HostGone()
        {
            var wasUp = _cefInitialised;
            _cefInitialised = false;
            StopPump.Set();
            if (wasUp) LogManager.CefLog("--> The browser host is gone; browsers are frozen until the next game session");
            SignalReady();
        }

        /// <summary>Sends a command to the host; silently dropped when the host is not there (CEF disabled or gone).</summary>
        internal static void Send(CefHostMessage message)
        {
            var channel = _channel;
            if (channel == null || !_cefInitialised) return;
            try
            {
                channel.Send(message);
            }
            catch (Exception ex)
            {
                if (_sendErrorsLogged++ < 5) LogManager.CefLog(ex, "CEF HOST SEND " + message.Type);
            }
        }

        internal static int NextBrowserId()
        {
            return Interlocked.Increment(ref _nextBrowserId);
        }

        internal static void Register(Browser browser)
        {
            lock (ById) ById[browser.Id] = browser;
        }

        internal static void Unregister(Browser browser)
        {
            lock (ById) ById.Remove(browser.Id);
        }

        /// <summary>Starts CEF if needed and waits until it is up (or has definitely failed).</summary>
        internal static bool WaitUntilReady(int timeoutMs)
        {
            InitializeCef();
            return CefReady.WaitOne(timeoutMs) && _cefInitialised;
        }

        /// <summary>
        /// Runs <paramref name="action"/> once the host reported Chromium up: right away on the calling thread when it
        /// already has, otherwise on the event thread when it does. Nothing blocks the script (and with it the game)
        /// while Chromium starts.
        /// </summary>
        internal static void RunWhenReady(Action action)
        {
            InitializeCef();

            lock (InitLock)
            {
                if (!CefReady.WaitOne(0))
                {
                    WhenReady.Add(action);
                    return;
                }
            }

            RunReadyAction(action);
        }

        private static void RunReadyAction(Action action)
        {
            if (!_cefInitialised)
            {
                LogManager.CefLog("--> CEF is not available; the browser is not created");
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF READY ACTION");
            }
        }

        /// <summary>Stages new frames of every browser from shared memory for the overlay; polls every 4 ms (one shared
        /// read per browser when nothing changed), so a frame reaches the screen within a few milliseconds.</summary>
        private static void StartFramePump()
        {
            lock (InitLock)
            {
                if (_framePump != null) return;
                StopPump.Reset();
                _framePump = new Thread(FramePump) { IsBackground = true, Name = "GTAN CEF frames" };
                _framePump.Start();
            }
        }

        private static void FramePump()
        {
            var errorsLogged = 0;
            while (!StopPump.WaitOne(4))
            {
                Browser[] snapshot;
                lock (Browsers) snapshot = Browsers.ToArray();
                foreach (var browser in snapshot)
                {
                    try
                    {
                        browser?._render?.Pump();
                    }
                    catch (Exception ex)
                    {
                        if (errorsLogged++ < 5) LogManager.CefLog(ex, "CEF FRAME PUMP");
                    }
                }
            }
        }

        /// <summary>Stops the browser host (game exit).</summary>
        internal static void DisposeCef()
        {
            Process host;
            CefHostChannel channel;
            lock (InitLock)
            {
                host = _host;
                channel = _channel;
                _host = null;
                _channel = null;
                _startAttempted = false;
                _cefInitialised = false;
                _framePump = null;
                CefReady.Reset();
            }
            StopPump.Set();
            if (host == null) return;

            try
            {
                if (channel != null)
                {
                    try { channel.Send(new CefHostMessage(CefHostProtocol.Shutdown)); } catch { }
                }
                if (!host.WaitForExit(3000))
                {
                    LogManager.CefLog("--> The browser host did not exit in 3 s; killing it");
                    host.Kill();
                }
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF HOST SHUTDOWN");
            }
            finally
            {
                channel?.Dispose();
                lock (ById) ById.Clear();
            }
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

    /// <summary>One off-screen browser, living in the browser host; its frames land in the DirectX overlay.</summary>
    public class Browser : IDisposable
    {
        internal readonly int Id;
        internal OverlayRenderHandler _render;
        internal BrowserJavascriptCallback _callback;
        internal readonly bool _localMode;
        internal bool _hasFocused;

        private readonly BrowserInput _input;
        private volatile bool _created;
        private volatile bool _closed;
        private volatile bool _loading;
        private volatile string _address;
        private volatile string _lastUrl;
        private bool _fellBack;
        private int _messagesLogged;
        private bool _headless;
        private Point _position;
        private Size _size;

        /// <summary>Input and focus of the browser, or null until it exists in the host.</summary>
        public BrowserInput Host => _created && !_closed ? _input : null;

        public BrowserInput GetHost()
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
                if (_created && !_closed) CEFManager.Send(new CefHostMessage(CefHostProtocol.Resize, Id) { W = value.Width, H = value.Height });
            }
        }

        internal Browser(V8ScriptEngine father, Size browserSize, bool localMode)
        {
            _localMode = localMode;
            _size = browserSize;
            Id = CEFManager.NextBrowserId();

            if (CefUtil.DISABLE_CEF) return;

            LogManager.CefLog("--> Browser " + Id + ": Start (" + browserSize.Width + "x" + browserSize.Height + ", " + (localMode ? "local" : "remote") + ")");

            _callback = new BrowserJavascriptCallback(father, this);
            _render = new OverlayRenderHandler(browserSize.Width, browserSize.Height) { SharedTextureFailed = OnSharedTexturesFailed };
            _input = new BrowserInput(this);
            CEFManager.Register(this);

            // Chromium may still be starting (first browser of the session): the create command goes out once the
            // host is ready. Until the host answers "created", IsInitialized() is false and API.waitUntilCefBrowserInit
            // yields; a page requested meanwhile is queued by the host and loaded as soon as the browser exists.
            CEFManager.RunWhenReady(() =>
            {
                if (_closed) return;
                var fps = Main.PlayerSettings != null && Main.PlayerSettings.CefFrameRate > 0 ? Math.Min(60, Main.PlayerSettings.CefFrameRate) : 60;
                var shared = CEFManager.WantSharedTextures;
                LogManager.CefLog("--> Browser " + Id + ": Creating Browser" + (shared ? " (shared textures)" : ""));
                CEFManager.Send(new CefHostMessage(CefHostProtocol.Create, Id) { W = _size.Width, H = _size.Height, Local = _localMode, Fps = fps, Shared = shared });
            });
        }

        /// <summary>
        /// The overlay could not open one of the host's shared textures on the game's device: this browser is created
        /// again with CPU frames (same id, the host replaces it), and no later browser asks for shared textures.
        /// </summary>
        private void OnSharedTexturesFailed(string error)
        {
            if (_fellBack || _closed) return;
            _fellBack = true;
            CEFManager.SharedTexturesBroken = true;
            LogManager.CefLog("-> Browser " + Id + ": shared textures unavailable (" + error + "); falling back to CPU frames");

            _render?.DropSharedTexture();
            _created = false;
            var fps = Main.PlayerSettings != null && Main.PlayerSettings.CefFrameRate > 0 ? Math.Min(60, Main.PlayerSettings.CefFrameRate) : 60;
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Create, Id) { W = _size.Width, H = _size.Height, Local = _localMode, Fps = fps, Shared = false });
            var url = _lastUrl;
            if (url != null) CEFManager.Send(new CefHostMessage(CefHostProtocol.Load, Id) { Url = url });
        }

        internal void OnHostEvent(CefHostMessage m)
        {
            switch (m.Type)
            {
                case CefHostProtocol.Created:
                    _created = true;
                    LogManager.CefLog("-> Browser " + Id + " created!");
                    break;
                case CefHostProtocol.Frame:
                    _render?.AttachFrame(m.FrameName, m.W, m.H, m.Stride);
                    break;
                case CefHostProtocol.Textures:
                    _render?.AttachTextures(m.Handles, m.W, m.H, m.Text);
                    break;
                case CefHostProtocol.Texture:
                    _render?.AttachTexture(m.Handle, m.W, m.H);
                    break;
                case CefHostProtocol.Loading:
                    _loading = m.IsLoading;
                    break;
                case CefHostProtocol.LoadStart:
                    LogManager.CefLog("-> Start: " + m.Url);
                    break;
                case CefHostProtocol.LoadEnd:
                    _address = m.Url;
                    LogManager.CefLog("-> End: " + m.Url + ", " + m.Status);
                    break;
                case CefHostProtocol.LoadError:
                    LogManager.CefLog("-> Load error " + m.Status + " (" + m.Text + ") for " + m.Url);
                    break;
                case CefHostProtocol.Console:
                {
                    // CEF log severities: 2 info, 3 warning, 4 error, 99 fatal. Errors and warnings always, the rest in debug mode.
                    var text = "-> Page console [" + m.Level + "] " + m.Text + " (" + m.Source + ":" + m.Line + ")";
                    if (m.Level >= 3) LogManager.CefLog(text);
                    else LogManager.VerboseCefLog(text);
                    break;
                }
                case CefHostProtocol.JsMessage:
                    if (m.Name != null)
                    {
                        var args = m.Args ?? new object[0];
                        if (_messagesLogged < 5 && LogManager.Verbose)
                        {
                            _messagesLogged++;
                            LogManager.CefLog("-> resourceCall " + m.Name + " (" + args.Length + " argument(s))");
                        }
                        _callback?.Invoke(m.Name, args);
                    }
                    else if (m.Code != null)
                    {
                        _callback?.Run(m.Code);
                    }
                    break;
                case CefHostProtocol.RenderTerminated:
                    LogManager.CefLog("-> Browser " + Id + ": render process terminated: " + m.Text);
                    break;
                case CefHostProtocol.Closed:
                    _closed = true;
                    break;
            }
        }

        public void eval(string code)
        {
            if (!_localMode || CefUtil.DISABLE_CEF || !_created || _closed || string.IsNullOrEmpty(code)) return;
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Eval, Id) { Code = code });
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
            if (CefUtil.DISABLE_CEF || _closed) return;

            _lastUrl = page;
            LogManager.CefLog("Trying to load page " + page + "..." + (_created ? "" : " (queued until the browser exists)"));
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Load, Id) { Url = page });
        }

        internal void GoBack()
        {
            if (CefUtil.DISABLE_CEF || !_created || _closed) return;

            LogManager.CefLog("Trying to go back a page...");
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Back, Id));
        }

        internal void Close()
        {
            if (CefUtil.DISABLE_CEF || _closed) return;
            _closed = true;

            _render?.Dispose();
            _render = null;
            CEFManager.Unregister(this);
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Close, Id));
        }

        internal void LoadHtml(string html)
        {
            if (CefUtil.DISABLE_CEF || _closed) return;
            CEFManager.Send(new CefHostMessage(CefHostProtocol.LoadHtml, Id) { Html = html ?? string.Empty });
        }

        internal string GetAddress()
        {
            return CefUtil.DISABLE_CEF ? null : _address;
        }

        internal bool IsLoading()
        {
            return !CefUtil.DISABLE_CEF && _loading;
        }

        internal bool IsInitialized()
        {
            if (CefUtil.DISABLE_CEF) return true;
            return _created && !_closed;
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
        }
    }

    /// <summary>Input, focus and frame rate of one browser; the same method names as CefSharp's IBrowserHost.</summary>
    public sealed class BrowserInput
    {
        private readonly Browser _browser;

        internal BrowserInput(Browser browser)
        {
            _browser = browser;
        }

        public void SetFocus(bool focus)
        {
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Focus, _browser.Id) { On = focus });
        }

        public void SendMouseMoveEvent(MouseEvent mouseEvent, bool mouseLeave)
        {
            CEFManager.Send(new CefHostMessage(CefHostProtocol.MouseMove, _browser.Id) { X = mouseEvent.X, Y = mouseEvent.Y, Mods = (int)mouseEvent.Modifiers, On = mouseLeave });
        }

        public void SendMouseClickEvent(MouseEvent mouseEvent, MouseButtonType button, bool mouseUp, int clickCount)
        {
            CEFManager.Send(new CefHostMessage(CefHostProtocol.MouseClick, _browser.Id)
            {
                X = mouseEvent.X, Y = mouseEvent.Y, Mods = (int)mouseEvent.Modifiers, Button = (int)button, On = mouseUp, Clicks = clickCount,
            });
        }

        public void SendMouseWheelEvent(MouseEvent mouseEvent, int deltaX, int deltaY)
        {
            CEFManager.Send(new CefHostMessage(CefHostProtocol.MouseWheel, _browser.Id) { X = mouseEvent.X, Y = mouseEvent.Y, Mods = (int)mouseEvent.Modifiers, Dx = deltaX, Dy = deltaY });
        }

        public void SendKeyEvent(KeyEvent keyEvent)
        {
            CEFManager.Send(new CefHostMessage(CefHostProtocol.Key, _browser.Id)
            {
                KeyType = (int)keyEvent.Type, KeyCode = keyEvent.WindowsKeyCode, NativeKeyCode = keyEvent.NativeKeyCode, Mods = (int)keyEvent.Modifiers,
            });
        }

        public int WindowlessFrameRate
        {
            set { CEFManager.Send(new CefHostMessage(CefHostProtocol.FrameRate, _browser.Id) { Fps = value }); }
        }
    }
}

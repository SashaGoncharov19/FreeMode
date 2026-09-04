using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using GTANetwork.Util;
using Newtonsoft.Json;

namespace GTANetwork.GUI
{
    /// <summary>
    /// The loading screen between "connect" and the start of the server's client scripts: a full-screen browser the
    /// client itself owns (no script engine behind it) showing ui/loader/index.html from the install, fed with the
    /// connection state, the file download progress and the loading-prompt texts. Shown on InitiatedConnect (the browser
    /// host is starting at the same moment, so the page appears about a second later), hidden when the resources are
    /// downloaded (InvokeFinishedDownload) or the connection ends. Off with &lt;CefLoader&gt;false&lt;/CefLoader&gt;.
    /// Threads: the connect flow and the loading prompt run on script threads, the download progress on the download
    /// thread; every entry point takes the lock and only sends messages to the host, which is thread-safe.
    /// </summary>
    internal static class ConnectLoader
    {
        private const string Page = "https://gtan/loader/index.html";
        private static readonly object Lock = new object();
        private static Browser _browser;
        private static Stopwatch _clock;
        private static Timer _closeTimer;
        private static string _server, _stage, _detail, _label;
        private static int _index, _total;
        private static bool _pageReady;

        private static bool Enabled => !CefUtil.DISABLE_CEF && (Main.PlayerSettings == null || Main.PlayerSettings.CefLoader);

        internal static bool Visible
        {
            get { lock (Lock) return _browser != null; }
        }

        /// <summary>InitiatedConnect: create the browser (the host is starting in parallel) and load the page.</summary>
        internal static void Show(string server)
        {
            if (!Enabled) return;
            Hide("replaced");
            lock (Lock)
            {
                _clock = Stopwatch.StartNew();
                _server = server;
                _stage = "connecting";
                _detail = "Connecting to " + server;
                _label = null;
                _index = _total = 0;
                _pageReady = false;
                var size = Main.screen.Width > 0 && Main.screen.Height > 0 ? Main.screen : new Size(1920, 1080);
                var browser = new Browser(null, size, true) { Position = new Point(0, 0) };
                browser.PageMessage = OnPageMessage;
                browser.PageLoaded = (url, status) => { if (url != null && url.StartsWith(Page, StringComparison.OrdinalIgnoreCase)) PageReady(); };
                browser.GoToPage(Page);
                _browser = browser;
            }
            LogManager.RuntimeLog("loader: shown for " + server + " (" + Main.screen.Width + "x" + Main.screen.Height + ")");
        }

        /// <summary>The connection reached a new phase: connecting → connected → downloading → starting.</summary>
        internal static void Stage(string stage, string detail)
        {
            lock (Lock)
            {
                if (_browser == null) return;
                _stage = stage;
                _detail = detail;
                if (stage != "downloading") { _label = null; _index = _total = 0; }
            }
            Push();
        }

        /// <summary>HTTP file download: file index of total.</summary>
        internal static void Progress(string label, int index, int total)
        {
            lock (Lock)
            {
                if (_browser == null) return;
                _stage = "downloading";
                _label = label;
                _index = index;
                _total = total;
                _detail = "Downloading " + label + " (" + index + "/" + total + ")";
            }
            Push();
        }

        /// <summary>The game's loading-prompt text (UDP transfer progress, "Loading"): shown as the detail line.</summary>
        internal static void Detail(string text)
        {
            lock (Lock)
            {
                if (_browser == null || string.IsNullOrEmpty(text) || text == _detail) return;
                _detail = text;
            }
            Push();
        }

        /// <summary>Resources are in and the scripts start, or the connection ended: fade the page out and close the browser.</summary>
        internal static void Hide(string reason)
        {
            Browser browser;
            long elapsed;
            lock (Lock)
            {
                browser = _browser;
                _browser = null;
                elapsed = _clock?.ElapsedMilliseconds ?? 0;
                _closeTimer?.Dispose();
                _closeTimer = null;
            }
            if (browser == null) return;
            LogManager.RuntimeLog("loader: hidden after " + elapsed + " ms (" + reason + ")");
            try
            {
                browser.eval("window.gtanLoader && gtanLoader.hide()");
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "LOADER HIDE");
            }
            // 350 ms for the CSS fade, then the browser goes; a Show() in between just creates the next one.
            var timer = new Timer(_ => { try { browser.Close(); } catch (Exception ex) { LogManager.CefLog(ex, "LOADER CLOSE"); } }, null, 350, Timeout.Infinite);
            lock (Lock) _closeTimer = timer;
        }

        private static void PageReady()
        {
            lock (Lock) _pageReady = true;
            Push();
        }

        private static void OnPageMessage(string name, object[] args)
        {
            if (name == "loader:ready") PageReady();
        }

        /// <summary>Send the whole state to the page (idempotent; the page renders whatever it gets).</summary>
        private static void Push()
        {
            Browser browser;
            string json;
            lock (Lock)
            {
                browser = _browser;
                if (browser == null || !_pageReady) return;
                json = JsonConvert.SerializeObject(new
                {
                    server = _server,
                    stage = _stage,
                    detail = _detail,
                    label = _label,
                    index = _index,
                    total = _total,
                    elapsed = _clock?.ElapsedMilliseconds ?? 0,
                });
            }
            try
            {
                browser.eval("window.gtanLoader && gtanLoader.update(" + json + ")");
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "LOADER UPDATE");
            }
        }
    }
}

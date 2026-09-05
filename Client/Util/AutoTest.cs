using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using GTANetwork.GUI;
using GTANetwork.Javascript;
using Microsoft.ClearScript.V8;

namespace GTANetwork.Util
{
    /// <summary>
    /// An in-game smoke test without a person at the keyboard (GTAN_AUTOTEST=host:port[#serverkey][;password] in the game's
    /// environment; the launcher passes it through). Once the game is ready it connects to that server, waits for the client
    /// scripts, then checks the RPC path twice - from a client script (API.rpc.call) and from a CEF page (gtan.rpc.call through
    /// the owning script) - and writes every step as "autotest: ..." to Runtime.log. GTAN_AUTOTEST_QUIT=0 keeps the game
    /// running afterwards; by default it quits so a script can wait for the launcher to return and read the log.
    /// </summary>
    internal static class AutoTest
    {
        private static readonly string Target = Environment.GetEnvironmentVariable("GTAN_AUTOTEST");
        private static readonly bool QuitWhenDone = Environment.GetEnvironmentVariable("GTAN_AUTOTEST_QUIT") != "0";
        /// <summary>GTAN_AUTOTEST_STAY=N keeps the game on the server N seconds after the checks (the [SYNC] summaries need time, T-018).</summary>
        private static readonly int StaySeconds = int.TryParse(Environment.GetEnvironmentVariable("GTAN_AUTOTEST_STAY"), out var stay) ? stay : 0;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _stage;
        private static long _stageStart;
        private static Browser _page;
        private static string _scriptResult, _pageResult;

        internal static bool Enabled => !string.IsNullOrWhiteSpace(Target);

        /// <summary>Main's script thread, every tick.</summary>
        internal static void Tick(Main main)
        {
            if (!Enabled || _stage >= 5) return;
            try
            {
                switch (_stage)
                {
                    case 0:
                        if (!main.Initialised) return;
                        Log("game ready after " + Clock.ElapsedMilliseconds + " ms (menu " + (CefMenu.Visible ? "visible" : "not visible") + "); connecting to " + Target + " in 3 s");
                        Next(1);
                        return;
                    case 1:
                        if (Clock.ElapsedMilliseconds - _stageStart < 3000) return;
                        Connect(main);
                        Next(2);
                        return;
                    case 2:
                        if (Main.IsOnServer() && !ConnectLoader.Visible && JavascriptHook.ScriptEngines.Count > 0)
                        {
                            Log("connected, " + JavascriptHook.ScriptEngines.Count + " client script(s) running, session " + (Main.Session != null ? "encrypted (" + Main.Session.PeerFingerprint + ")" : "plaintext") + " after " + Clock.ElapsedMilliseconds + " ms");
                            StartChecks();
                            Next(3);
                            return;
                        }
                        if (Clock.ElapsedMilliseconds - _stageStart > 120000) { Log("FAILED: not connected with scripts after 120 s (on server " + Main.IsOnServer() + ", loader " + ConnectLoader.Visible + ", scripts " + JavascriptHook.ScriptEngines.Count + ")"); Finish(); }
                        return;
                    case 3:
                        if ((_scriptResult != null && _pageResult != null) || Clock.ElapsedMilliseconds - _stageStart > 60000)
                        {
                            Log("script rpc: " + (_scriptResult ?? "NO RESULT in 60 s"));
                            Log("page rpc: " + (_pageResult ?? "NO RESULT in 60 s"));
                            var ok = _scriptResult != null && _scriptResult.StartsWith("ok") && _pageResult != null && _pageResult.StartsWith("ok");
                            Log(ok ? "RESULT: OK" : "RESULT: FAILED");
                            if (StaySeconds > 0) { LogManager.Verbose = true; Log("staying " + StaySeconds + " s for measurements (GTAN_AUTOTEST_STAY); verbose logging on"); Next(4); }
                            else Finish();
                        }
                        return;
                    case 4:
                        if (Clock.ElapsedMilliseconds - _stageStart > StaySeconds * 1000L) Finish();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("FAILED with " + ex.GetType().Name + ": " + ex.Message);
                Finish();
            }
        }

        private static void Next(int stage)
        {
            _stage = stage;
            _stageStart = Clock.ElapsedMilliseconds;
        }

        private static void Connect(Main main)
        {
            var target = Target.Trim();
            string password = null, key = null;
            var semi = target.IndexOf(';');
            if (semi >= 0) { password = target.Substring(semi + 1); target = target.Substring(0, semi); }
            var hash = target.IndexOf('#');
            if (hash >= 0) { key = target.Substring(hash + 1); target = target.Substring(0, hash); }
            var colon = target.LastIndexOf(':');
            var host = colon > 0 ? target.Substring(0, colon) : target;
            var port = 4499;
            if (colon > 0) int.TryParse(target.Substring(colon + 1), out port);
            Log("connecting to " + host + ":" + port + (key != null ? " (pinned key)" : "") + (password != null ? " (password)" : ""));
            CefMenu.Suspended = true;
            CefMenu.Hide("autotest connect");
            main.ConnectToServer(host, port, password != null, password ?? "", key);
        }

        private static void StartChecks()
        {
            var wrapper = JavascriptHook.ScriptEngines.FirstOrDefault(w => w.ResourceParent == "freeroam") ?? JavascriptHook.ScriptEngines[0];
            var engine = wrapper.Engine;
            Log("using the script " + wrapper.ResourceParent + "/" + wrapper.Filename + " for the checks");

            // 1. a client script calls the server: API.rpc.call -> RpcRouter -> server -> back into the promise
            engine.AddHostObject("autotest", new AutoTestSink());
            engine.Execute("autotest.js",
                "API.rpc.call('freeroam:ping', { from: 'autotest script' }).then(" +
                "  function (r) { autotest.script('ok ' + JSON.stringify(r)); }," +
                "  function (e) { autotest.script('failed ' + (e && e.code) + ' ' + (e && e.message)); });");
            Log("script check started (API.rpc.call freeroam:ping)");

            // 2. a CEF page owned by that script calls the server: gtan.rpc.call -> host -> the script's API.rpc -> server -> page
            var browser = new Browser(engine, new Size(320, 120), true) { Position = new Point(40, 40) };
            browser.PageMessage = (name, args) =>
            {
                if (name != "autotest:page") return;
                _pageResult = args != null && args.Length > 0 ? Convert.ToString(args[0]) : "(empty)";
            };
            browser.GoToPage("https://gtan/autotest/index.html");
            _page = browser;
            Log("page check started (https://gtan/autotest/index.html, browser " + browser.Id + ")");
        }

        private static void Finish()
        {
            _stage = 5;
            try { _page?.Close(); } catch { }
            Log("done after " + Clock.ElapsedMilliseconds + " ms" + (QuitWhenDone ? "; quitting the game" : ""));
            if (!QuitWhenDone) return;
            try { if (Main.Client != null && Main.IsOnServer()) Main.Client.Disconnect("autotest done"); } catch { }
            System.Threading.Thread.Sleep(500);
            try { CEFManager.Draw = false; CEFManager.Dispose(); CEFManager.DisposeCef(); } catch { }
            var game = Process.GetProcessesByName("GTA5").FirstOrDefault();
            game?.Kill();
            Process.GetCurrentProcess().Kill();
        }

        private static void Log(string text)
        {
            LogManager.RuntimeLog("autotest: " + text);
        }

        internal static void ScriptReported(string result)
        {
            _scriptResult = result;
            Log("script reported: " + result);
        }
    }

    /// <summary>The host object the injected autotest script reports through. A public top-level type: ClearScript hides the
    /// members of types that are not publicly accessible (a nested class in an internal class is one), which made the first run
    /// report nothing although the RPC had completed.</summary>
    public sealed class AutoTestSink
    {
        public void script(string result)
        {
            AutoTest.ScriptReported(result);
        }
    }
}

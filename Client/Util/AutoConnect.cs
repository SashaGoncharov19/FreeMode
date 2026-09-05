using System;
using System.Diagnostics;
using System.Globalization;
using GTANetwork.GUI;

namespace GTANetwork.Util
{
    /// <summary>
    /// GTAN_CONNECT=host:port[#serverkey][;password] in the game's environment (the launcher passes it, T-024): as soon as the game
    /// is ready the client joins that server instead of showing the menu — the launcher's "Connect" and "run --connect". Unlike the
    /// autotest it runs no checks and never quits the game.
    /// </summary>
    internal static class AutoConnect
    {
        private static readonly string Target = Environment.GetEnvironmentVariable("GTAN_CONNECT");
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _stage;
        private static long _readyAt;

        internal static bool Enabled => !string.IsNullOrWhiteSpace(Target) && !AutoTest.Enabled;

        /// <summary>Main's script thread, every tick.</summary>
        internal static void Tick(Main main)
        {
            if (!Enabled || _stage >= 2) return;
            try
            {
                if (_stage == 0)
                {
                    if (!main.Initialised) return;
                    _readyAt = Clock.ElapsedMilliseconds;
                    _stage = 1;
                    LogManager.RuntimeLog("autoconnect: game ready after " + _readyAt + " ms; joining " + Target + " in 2 s");
                    return;
                }
                if (Clock.ElapsedMilliseconds - _readyAt < 2000) return;
                _stage = 2;
                var target = Target.Trim();
                string password = null, key = null;
                var semi = target.IndexOf(';');
                if (semi >= 0) { password = target.Substring(semi + 1); target = target.Substring(0, semi); }
                var hash = target.IndexOf('#');
                if (hash >= 0) { key = target.Substring(hash + 1); target = target.Substring(0, hash); }
                var colon = target.LastIndexOf(':');
                var host = colon > 0 ? target.Substring(0, colon) : target;
                var port = colon > 0 && int.TryParse(target.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 4499;
                LogManager.RuntimeLog("autoconnect: connecting to " + host + ":" + port + (key != null ? " (pinned key)" : "") + (password != null ? " (password)" : ""));
                CefMenu.Suspended = true;
                CefMenu.Hide("auto connect");
                main.ConnectToServer(host, port, password != null, password ?? "", key);
            }
            catch (Exception ex)
            {
                _stage = 2;
                LogManager.RuntimeLog("autoconnect: failed: " + ex.Message);
            }
        }
    }
}

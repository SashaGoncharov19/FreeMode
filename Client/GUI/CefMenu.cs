using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using GTA;
using GTANetwork.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GTANetwork.GUI
{
    /// <summary>
    /// The main menu on CEF (T-013): ui/menu/index.html full screen while the player is not on a server — the servers found
    /// (favourites and recent from PlayerSettings, LAN discovery, the master list), direct connect, the settings the NativeUI
    /// menu has, Quit. The client owns the browser (no script engine behind it), pushes its whole state with
    /// gtanMenu.update(state) and receives the page's actions as PageMessage ("menu:connect", "menu:favorite", ...). Actions
    /// that touch the game (connect, favourites, settings, the classic menu) run on Main's script thread through
    /// <see cref="Tick"/>. The NativeUI menu stays on the pause key (host tab, debug switches) and is the fallback when the
    /// page does not come up. Off with &lt;CefMenu&gt;false&lt;/CefMenu&gt; or when CEF is disabled.
    /// </summary>
    internal static class CefMenu
    {
        private const string Page = "https://gtan/menu/index.html";
        /// <summary>No "menu:ready" from the page within this time (host dead, page broken, machine swapping): the NativeUI menu takes over.</summary>
        private const int PageTimeoutMs = 30000;

        private sealed class ServerRow
        {
            public string Address;   // ip:port
            public string Name;
            public string Gamemode;
            public string Map;
            public int Players;
            public int MaxPlayers;
            public bool Passworded;
            public string Source;    // where it was found: "lan", "internet", "verified"; "" = only known from favourites/recent
            public bool Online;      // answered a discovery request during the current refresh
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, ServerRow> Servers = new Dictionary<string, ServerRow>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<Action> Actions = new ConcurrentQueue<Action>();
        private static Browser _browser;
        private static bool _pageReady;
        private static Stopwatch _clock;
        private static Timer _closeTimer;
        private static Timer _fallbackTimer;
        private static string _status = "";
        private static int _shown;

        /// <summary>The CEF menu is switched on and CEF is available.</summary>
        internal static bool Enabled
        {
            get
            {
                var settings = Main.PlayerSettings;
                return !CefUtil.DISABLE_CEF && settings != null && settings.CefMenu && !settings.DisableCEF;
            }
        }

        /// <summary>A connection is being made or exists: the menu stays away until the disconnect.</summary>
        internal static volatile bool Suspended;

        internal static bool Visible
        {
            get { lock (Lock) return _browser != null; }
        }

        /// <summary>Main's script thread: show the page (the browser host is started when it is not running yet).</summary>
        internal static void Show()
        {
            if (!Enabled || Suspended) return;
            lock (Lock) if (_browser != null) return;

            CEFManager.Initialize(Main.screen);
            CEFManager.InitializeCef();
            lock (Lock)
            {
                if (_browser != null) return;
                _clock = Stopwatch.StartNew();
                _pageReady = false;
                var size = Main.screen.Width > 0 && Main.screen.Height > 0 ? Main.screen : new Size(1920, 1080);
                var browser = new Browser(null, size, true) { Position = new Point(0, 0) };
                browser.PageMessage = OnPageMessage;
                browser.PageLoaded = (url, status) => { if (url != null && url.StartsWith(Page, StringComparison.OrdinalIgnoreCase)) PageReady(); };
                browser.GoToPage(Page);
                _browser = browser;
                _fallbackTimer?.Dispose();
                _fallbackTimer = new Timer(_ => Fallback(), null, PageTimeoutMs, Timeout.Infinite);
                _shown++;
            }
            CEFManager.Draw = true;
            CefController.ShowCursor = true;
            if (Main.MainMenu != null) Main.MainMenu.Visible = false;
            LogManager.RuntimeLog("menu: shown (" + Main.screen.Width + "x" + Main.screen.Height + ", " + (_shown == 1 ? "first time" : "again") + ")");
            Refresh();
        }

        /// <summary>Any thread: fade the page out and close the browser; <paramref name="reason"/> goes to Runtime.log.</summary>
        internal static void Hide(string reason)
        {
            Browser browser;
            long elapsed;
            lock (Lock)
            {
                browser = _browser;
                _browser = null;
                _pageReady = false;
                elapsed = _clock?.ElapsedMilliseconds ?? 0;
                _fallbackTimer?.Dispose();
                _fallbackTimer = null;
                _closeTimer?.Dispose();
                _closeTimer = null;
            }
            if (browser == null) return;
            CefController.ShowCursor = false;
            LogManager.RuntimeLog("menu: hidden after " + elapsed + " ms (" + reason + ")");
            try { browser.eval("window.gtanMenu && gtanMenu.hide()"); }
            catch (Exception ex) { LogManager.CefLog(ex, "MENU HIDE"); }
            var timer = new Timer(_ => { try { browser.Close(); } catch (Exception ex) { LogManager.CefLog(ex, "MENU CLOSE"); } }, null, 300, Timeout.Infinite);
            lock (Lock) _closeTimer = timer;
        }

        /// <summary>Main's script thread, every tick: runs what the page asked for.</summary>
        internal static void Tick()
        {
            Action action;
            while (Actions.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception ex) { LogManager.LogException(ex, "MENU ACTION"); }
            }
        }

        // ---- data in ----

        /// <summary>Ask again: favourites and recent at once, then LAN discovery and the master list (Main.RebuildServerBrowser).</summary>
        internal static void Refresh()
        {
            lock (Lock)
            {
                foreach (var row in Servers.Values) row.Online = false;
                SeedFromSettings();
                _status = "Searching for servers…";
            }
            Push();
            Actions.Enqueue(() => CrossReference.EntryPoint?.RebuildServerBrowser());
        }

        /// <summary>The master list arrived (or failed): thread-pool thread of RebuildServerBrowser.</summary>
        internal static void OnServerList(IEnumerable<string> internet, IEnumerable<string> verified, int totalPlayers, int totalServers, string error)
        {
            lock (Lock)
            {
                foreach (var address in verified ?? Enumerable.Empty<string>())
                {
                    var row = Row(address);
                    if (row != null) row.Source = "verified";
                }
                foreach (var address in internet ?? Enumerable.Empty<string>())
                {
                    var row = Row(address);
                    if (row != null && string.IsNullOrEmpty(row.Source)) row.Source = IsLocal(address) ? "lan" : "internet";
                }
                if (error != null) _status = error;
                else if (totalServers > 0) _status = totalServers + " server(s), " + totalPlayers + " player(s) online";
            }
            Push();
        }

        /// <summary>A server answered a discovery request (script thread, ProcessMessages).</summary>
        internal static void OnDiscovered(string address, string name, string gamemode, string map, int players, int maxPlayers, bool passworded)
        {
            int online;
            lock (Lock)
            {
                var row = Row(address);
                if (row == null) return;
                row.Name = string.IsNullOrWhiteSpace(name) ? address : name;
                row.Gamemode = gamemode ?? "";
                row.Map = map ?? "";
                row.Players = players;
                row.MaxPlayers = maxPlayers;
                row.Passworded = passworded;
                row.Online = true;
                if (string.IsNullOrEmpty(row.Source)) row.Source = IsLocal(address) ? "lan" : "internet";
                online = Servers.Values.Count(r => r.Online);
                _status = online + " server(s) answered";
            }
            Push();
        }

        internal static void Status(string text)
        {
            lock (Lock) _status = text ?? "";
            Push();
        }

        // under Lock
        private static void SeedFromSettings()
        {
            var settings = Main.PlayerSettings;
            if (settings == null) return;
            foreach (var address in settings.FavoriteServers ?? new List<string>()) Row(address);
            foreach (var address in settings.RecentServers ?? new List<string>()) Row(address);
        }

        // under Lock
        private static ServerRow Row(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || address.IndexOf(':') <= 0) return null;
            ServerRow row;
            if (!Servers.TryGetValue(address, out row)) Servers[address] = row = new ServerRow { Address = address, Name = address, Source = "", Gamemode = "", Map = "" };
            return row;
        }

        private static bool IsLocal(string address)
        {
            var host = address.Split(':')[0];
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip)) return host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return IPAddress.IsLoopback(ip);
            return b[0] == 127 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        // ---- data out ----

        private static void PageReady()
        {
            lock (Lock)
            {
                if (_browser == null) return;
                _pageReady = true;
                _fallbackTimer?.Dispose();
                _fallbackTimer = null;
            }
            LogManager.RuntimeLog("menu: page ready after " + (_clock?.ElapsedMilliseconds ?? 0) + " ms");
            Push();
        }

        private static void Fallback()
        {
            lock (Lock) if (_browser == null || _pageReady) return;
            LogManager.RuntimeLog("menu: the page did not come up within " + PageTimeoutMs + " ms; the classic menu takes over (press the pause key for it any time)");
            Hide("timeout");
            Actions.Enqueue(OpenNativeMenu);
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
                json = JsonConvert.SerializeObject(State());
            }
            try { browser.eval("window.gtanMenu && gtanMenu.update(" + json + ")"); }
            catch (Exception ex) { LogManager.CefLog(ex, "MENU UPDATE"); }
        }

        // under Lock
        private static object State()
        {
            var s = Main.PlayerSettings;
            var favorites = new HashSet<string>(s?.FavoriteServers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var recent = new HashSet<string>(s?.RecentServers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return new
            {
                version = Main.CurrentVersion.ToString(),
                status = _status,
                servers = Servers.Values
                    .OrderByDescending(r => r.Online).ThenByDescending(r => favorites.Contains(r.Address)).ThenByDescending(r => r.Players).ThenBy(r => r.Name)
                    .Select(r => new
                    {
                        address = r.Address, name = r.Name, gamemode = r.Gamemode, map = r.Map, players = r.Players, maxPlayers = r.MaxPlayers,
                        passworded = r.Passworded, source = r.Source, online = r.Online, favorite = favorites.Contains(r.Address), recent = recent.Contains(r.Address),
                    }).ToList(),
                settings = s == null ? null : new
                {
                    displayName = s.DisplayName, showFps = s.ShowFPS, disableRockstarEditor = s.DisableRockstarEditor,
                    timestamp = s.Timestamp, militaryTime = s.Militarytime, scaleChatWithSafezone = s.ScaleChatWithSafezone, useClassicChat = s.UseClassicChat,
                    chatboxXOffset = s.ChatboxXOffset, chatboxYOffset = s.ChatboxYOffset,
                    cefLoader = s.CefLoader, cefMenu = s.CefMenu, cefGpu = s.CefGpu, masterServerAddress = s.MasterServerAddress ?? "",
                },
            };
        }

        // ---- the page's actions (host reader thread) ----

        private static void OnPageMessage(string name, object[] args)
        {
            try
            {
                switch (name)
                {
                    case "menu:ready": PageReady(); break;
                    case "menu:refresh": Refresh(); break;
                    case "menu:connect": Connect(Arg(args, 0), Arg(args, 1), Arg(args, 2)); break;
                    case "menu:favorite": SetFavorite(Arg(args, 0), Arg(args, 1) == "true"); break;
                    case "menu:forget": Forget(Arg(args, 0)); break;
                    case "menu:settings": ApplySettings(Arg(args, 0)); break;
                    case "menu:native": Actions.Enqueue(OpenNativeMenu); break;
                    case "menu:quit": Actions.Enqueue(Quit); break;
                    default: LogManager.RuntimeLog("menu: unknown page message " + name); break;
                }
            }
            catch (Exception ex)
            {
                LogManager.LogException(ex, "MENU PAGE MESSAGE " + name);
            }
        }

        private static string Arg(object[] args, int index)
        {
            if (args == null || index >= args.Length || args[index] == null) return null;
            var value = args[index];
            if (value is bool) return (bool)value ? "true" : "false";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Connect(string host, string portText, string password)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            host = host.Trim();
            string pinnedKey = null;
            var hash = host.IndexOf('#'); // "host:port#<server public key>" pins the key (T-009)
            if (hash >= 0) { pinnedKey = host.Substring(hash + 1).Trim(); host = host.Substring(0, hash).Trim(); }
            var port = 4499;
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon) // "ip:port" in one field (not an IPv6 literal)
            {
                int.TryParse(host.Substring(colon + 1), out port);
                host = host.Substring(0, colon);
            }
            int explicitPort;
            if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText.Trim(), out explicitPort)) port = explicitPort;
            if (port <= 0 || port > 65535) port = 4499;
            var address = host + ":" + port;
            var pass = password ?? "";
            Suspended = true;
            lock (Lock) _status = "Connecting to " + address + "…";
            Push();
            Actions.Enqueue(() =>
            {
                var main = CrossReference.EntryPoint;
                if (main == null) return;
                main.AddServerToRecent(address, pass);
                Hide("connect " + address);
                main.ConnectToServer(host, port, pass.Length > 0, pass, pinnedKey);
            });
        }

        private static void SetFavorite(string address, bool favorite)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            Actions.Enqueue(() =>
            {
                if (favorite) Main.AddToFavorites(address);
                else Main.RemoveFromFavorites(address);
                lock (Lock) SeedFromSettings();
                Push();
            });
        }

        private static void Forget(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            Actions.Enqueue(() =>
            {
                var settings = Main.PlayerSettings;
                if (settings == null) return;
                settings.RecentServers.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
                Util.Util.SaveSettings(Main.GTANInstallDir + "\\settings.xml");
                lock (Lock)
                {
                    ServerRow row;
                    if (Servers.TryGetValue(address, out row) && !row.Online && !settings.FavoriteServers.Contains(address)) Servers.Remove(address);
                }
                Push();
            });
        }

        /// <summary>The settings form was saved: a JSON object with the keys of State().settings; unknown keys are ignored.</summary>
        private static void ApplySettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            var values = JObject.Parse(json);
            Actions.Enqueue(() =>
            {
                var s = Main.PlayerSettings;
                if (s == null) return;
                string text;
                bool flag;
                int number;
                if (TryString(values, "displayName", out text)) s.DisplayName = text.Trim();
                if (TryString(values, "masterServerAddress", out text)) s.MasterServerAddress = text.Trim();
                if (TryBool(values, "showFps", out flag)) s.ShowFPS = flag;
                if (TryBool(values, "disableRockstarEditor", out flag)) s.DisableRockstarEditor = flag;
                if (TryBool(values, "timestamp", out flag)) s.Timestamp = flag;
                if (TryBool(values, "militaryTime", out flag)) s.Militarytime = flag;
                if (TryBool(values, "scaleChatWithSafezone", out flag)) s.ScaleChatWithSafezone = flag;
                if (TryBool(values, "useClassicChat", out flag)) s.UseClassicChat = flag;
                if (TryBool(values, "cefLoader", out flag)) s.CefLoader = flag;
                if (TryBool(values, "cefMenu", out flag)) s.CefMenu = flag;
                if (TryBool(values, "cefGpu", out flag)) s.CefGpu = flag;
                if (TryInt(values, "chatboxXOffset", out number)) s.ChatboxXOffset = number;
                if (TryInt(values, "chatboxYOffset", out number)) s.ChatboxYOffset = number;
                Util.Util.SaveSettings(Main.GTANInstallDir + "\\settings.xml");
                LogManager.RuntimeLog("menu: settings saved");
                lock (Lock) _status = "Settings saved" + (values["cefGpu"] != null || values["cefMenu"] != null ? " (CEF settings apply after a restart)" : "");
                Push();
            });
        }

        private static bool TryString(JObject values, string key, out string value)
        {
            var token = values[key];
            value = token != null && token.Type == JTokenType.String ? (string)token : null;
            return value != null;
        }

        private static bool TryBool(JObject values, string key, out bool value)
        {
            var token = values[key];
            value = token != null && token.Type == JTokenType.Boolean && (bool)token;
            return token != null && token.Type == JTokenType.Boolean;
        }

        private static bool TryInt(JObject values, string key, out int value)
        {
            var token = values[key];
            if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)) { value = (int)token; return true; }
            value = 0;
            return false;
        }

        /// <summary>The NativeUI menu (host tab, debug switches, or the fallback): the page goes, the classic menu opens.</summary>
        private static void OpenNativeMenu()
        {
            Hide("classic menu");
            var menu = Main.MainMenu;
            if (menu == null) return;
            menu.Visible = true;
            menu.RefreshIndex();
            if (!Main.IsOnServer()) World.RenderingCamera = Main.MainMenuCamera;
        }

        /// <summary>Same as the NativeUI menu's Quit.</summary>
        private static void Quit()
        {
            LogManager.RuntimeLog("menu: quit");
            if (Main.Client != null && Main.IsOnServer()) Main.Client.Disconnect("Quit");
            CEFManager.Draw = false;
            CEFManager.Dispose();
            CEFManager.DisposeCef();
            var game = Process.GetProcessesByName("GTA5").FirstOrDefault();
            game?.Kill();
            Process.GetCurrentProcess().Kill();
        }
    }
}

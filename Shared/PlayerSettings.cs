using System.Collections.Generic;
#if NETFRAMEWORK
using System.Windows.Forms;
#endif

namespace GTANetworkShared
{
    public class PlayerSettings
    {
        public string DisplayName { get; set; }
        public string MasterServerAddress { get; set; }
        public List<string> FavoriteServers { get; set; }
        public List<string> RecentServers { get; set; }
        public bool ScaleChatWithSafezone { get; set; }
        public string UpdateChannel { get; set; }
        public bool DisableRockstarEditor { get; set; }
        public Keys ScreenshotKey { get; set; }
        public bool ShowFPS { get; set; }
        public bool DisableCEF { get; set; }
        public bool Timestamp { get; set; }
        public bool Militarytime { get; set; }
        //public bool AutosetBorderlessWindowed { get; set; }
        public bool UseClassicChat { get; set; }
        public bool OfflineMode { get; set; }
        public bool MediaStream { get; set; }
        public bool CEFDevtool { get; set; }
        public bool DebugMode { get; set; }
        /// <summary>Let the browser use its GPU process (default false: software rendering, as the old browser did).</summary>
        public bool CefGpu { get; set; }
        /// <summary>Frames per second the off-screen browsers paint (1-60, default 60).</summary>
        public int CefFrameRate { get; set; }
        /// <summary>Start the browser host (Chromium) at game start instead of with the first browser a resource creates (default false).</summary>
        public bool CefPreload { get; set; }
        /// <summary>Run Chromium's GPU service inside the browser host process instead of a further GPU process (default true).</summary>
        public bool CefInProcessGpu { get; set; }
        /// <summary>With <see cref="CefGpu"/>: pages arrive as D3D11 shared textures copied GPU-side into the overlay instead of
        /// CPU frames through shared memory (default true; the client falls back by itself if a texture cannot be opened).</summary>
        public bool CefSharedTexture { get; set; }

        public int ChatboxXOffset { get; set; }
        public int ChatboxYOffset { get; set; }

        public string GamePath { get; set; }

        // --- Cross-platform launcher (GTANetwork.Launcher) settings. Ignored by the classic Windows launcher. ---

        /// <summary>How the launcher starts the game: "steam" (default), "proton" (run Proton directly) or "direct" (Windows).</summary>
        public string LaunchMethod { get; set; }
        /// <summary>Steam installation root. Auto-detected when empty.</summary>
        public string SteamPath { get; set; }
        /// <summary>Directory of the Proton build to use (contains the "proton" script). Auto-detected when empty.</summary>
        public string ProtonPath { get; set; }
        /// <summary>Wine prefix of the game (Steam: steamapps/compatdata/271590/pfx). Auto-detected when empty.</summary>
        public string ProtonPrefixPath { get; set; }
        /// <summary>Add -scOfflineOnly to commandline.txt while GTA Network runs (the classic launcher always did this).</summary>
        public bool ScOfflineOnly { get; set; }
        /// <summary>Temporarily move other *.asi plugins out of the game folder while GTA Network runs.</summary>
        public bool DisableOtherAsiPlugins { get; set; }
        /// <summary>Write script global 2576573 at startup to allow MP-only vehicles. The index is from 2016 builds
        /// and corrupts memory on newer ones, so this is off unless you know the current index works.</summary>
        public bool EnableMpVehiclesGlobal { get; set; }

        public PlayerSettings()
        {
            // The original master server (master.gtanet.work) is gone; empty = do not contact any. Favourites,
            // recent servers and LAN discovery work without one. Point this at your own master server if you run one.
            MasterServerAddress = "";
            FavoriteServers = new List<string>();
            RecentServers = new List<string>();
            ScaleChatWithSafezone = true;
            UpdateChannel = "stable";
            DisableRockstarEditor = true;
            //AutosetBorderlessWindowed = false;
            ScreenshotKey = Keys.F8;
            UseClassicChat = false;
            ShowFPS = true;
            DisableCEF = false;
            Timestamp = false;
            Militarytime = true;
            OfflineMode = false;
            MediaStream = false;
            CEFDevtool = false;
            CefGpu = false;
            CefFrameRate = 60;
            CefPreload = false;
            CefInProcessGpu = true;
            CefSharedTexture = true;
            DebugMode = false;
            GamePath = "";
            LaunchMethod = "steam";
            SteamPath = "";
            ProtonPath = "";
            ProtonPrefixPath = "";
            ScOfflineOnly = true;
            DisableOtherAsiPlugins = true;
            EnableMpVehiclesGlobal = false;
        }
    }
}

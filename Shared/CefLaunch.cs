using System.Collections.Generic;
using System.Text;

namespace GTANetworkShared
{
    /// <summary>
    /// How the client starts Chromium (CefSharp): the command-line switches of the browser process. They live here,
    /// outside the in-game client, so that the stand-alone harness (Tools/CefHarness) starts Chromium exactly the way
    /// the game does and a switch tried there is the switch the game gets.
    /// </summary>
    public static class CefLaunch
    {
        /// <summary>The Chromium switches for the given options. A key with an empty value is a plain flag.</summary>
        /// <param name="gpu">Let Chromium use the GPU. Off = display-compositor-only mode: no ANGLE, D3D11, SwiftShader or
        /// Vulkan is ever initialised in the process (the game already runs DXVK on the same GPU); off-screen pages are
        /// composited in software either way.</param>
        /// <param name="inProcessGpu">Run the GPU service (the software compositor) inside the browser process instead of
        /// launching a GPU subprocess.</param>
        /// <param name="mediaStream">Allow getUserMedia (camera, microphone) in pages.</param>
        /// <remarks>Feature and switch names were checked against the strings of libcef.dll 151 (Chromium ignores unknown
        /// names silently, so a stale name is a silent no-op: NetworkServiceInProcess had become one).</remarks>
        public static List<KeyValuePair<string, string>> Switches(bool gpu, bool inProcessGpu, bool mediaStream)
        {
            var s = new List<KeyValuePair<string, string>>();

            // Software rendering by default, exactly what the old single-process browser did.
            if (!gpu)
            {
                Add(s, "disable-gpu");
                Add(s, "disable-gpu-compositing");
                Add(s, "use-gl", "disabled");
                Add(s, "disable-software-rasterizer");
            }
            Add(s, "disable-gpu-vsync");
            Add(s, "autoplay-policy", "no-user-gesture-required");
            // One renderer for all our pages (every origin is a local resource; site isolation buys nothing here and
            // each renderer is a full process), no spare renderer warmed up in advance, network and audio services in
            // the browser process instead of utility processes, no CPU metrics utility.
            Add(s, "renderer-process-limit", "1");
            Add(s, "process-per-site");
            Add(s, "disable-site-isolation-trials");
            Add(s, "enable-features", "NetworkServiceInProcess2");
            // Fewer Windows-only subsystems to trip over under Wine (no DirectComposition, no window occlusion tracking)
            // and no Chrome-layer services a game overlay has no use for (media router, optimization hints, heavy-ad
            // intervention, translate, autofill server calls).
            Add(s, "disable-direct-composition");
            Add(s, "disable-features", "CalculateNativeWinOcclusion,SpareRendererForSitePerProcess,AudioServiceOutOfProcess,ProcessorMetrics,MediaRouter,OptimizationHints,HeavyAdIntervention,Translate,AutofillServerCommunication");
            Add(s, "disable-extensions");
            Add(s, "disable-component-extensions-with-background-pages");
            Add(s, "disable-print-preview");
            Add(s, "disable-speech-api");
            Add(s, "disable-notifications");
            Add(s, "disable-background-networking");
            Add(s, "disable-component-update");
            Add(s, "disable-default-apps");
            Add(s, "disable-domain-reliability");
            Add(s, "disable-breakpad");
            Add(s, "metrics-recording-only");
            Add(s, "disable-hang-monitor");
            Add(s, "disable-prompt-on-repost");
            Add(s, "no-first-run");
            Add(s, "no-pings");
            // The cache only ever holds our own resource files.
            Add(s, "disk-cache-size", "33554432");
            if (inProcessGpu) Add(s, "in-process-gpu");
            if (mediaStream) Add(s, "enable-media-stream");

            return s;
        }

        /// <summary>The switches as one command line: <c>--flag --key=value ...</c>.</summary>
        public static string Describe(IEnumerable<KeyValuePair<string, string>> switches)
        {
            var sb = new StringBuilder();
            foreach (var kv in switches)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("--").Append(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value)) sb.Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        private static void Add(List<KeyValuePair<string, string>> list, string key, string value = "")
        {
            list.Add(new KeyValuePair<string, string>(key, value ?? string.Empty));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTANetwork.Util;

namespace GTANetwork.Sync
{
    internal partial class SyncPed
    {
        /// <summary>Metres between the rendered ped and the last received position, updated every frame in Render (T-018).</summary>
        internal float LastRenderError;

        /// <summary>Packets per second from this player, from the average interval of the last ten packets.</summary>
        internal float PacketRateHz => AverageLatency > 0 ? (float)(1000.0 / AverageLatency) : 0f;

        internal void RecordRenderError()
        {
            try
            {
                if (Character == null || !Character.Exists()) return;
                if (_isInVehicle)
                {
                    if (MainVehicle != null && MainVehicle.Exists()) LastRenderError = (MainVehicle.Position - Position).Length();
                }
                else LastRenderError = (Character.Position - Position).Length();
            }
            catch (Exception) { /* a ped that vanished this frame */ }
        }
    }

    /// <summary>The 10-second [SYNC] summary over the streamed players (T-018): error p50/p95, packet age p95, rate.</summary>
    internal static class SyncMetrics
    {
        private static long _nextSummary;
        public static string LastSummary = "";

        public static void Tick(SyncPed[] bubble)
        {
            var now = Util.Util.TickCount;
            if (now < _nextSummary) return;
            _nextSummary = now + 10000;
            var errors = new List<float>(); var ages = new List<long>(); var rates = new List<float>();
            foreach (var ped in bubble)
            {
                if (ped == null || !ped.StreamedIn || ped.Character == null) continue;
                errors.Add(ped.LastRenderError);
                ages.Add(ped.TicksSinceLastUpdate);
                rates.Add(ped.PacketRateHz);
            }
            if (errors.Count == 0) { LastSummary = "[SYNC] players 0"; return; }
            errors.Sort(); ages.Sort();
            LastSummary = string.Format("[SYNC] players {0}, error p50 {1:0.00} m / p95 {2:0.00} m, age p95 {3} ms, rate {4:0.0} Hz, fps {5:0}",
                errors.Count, Percentile(errors, 0.5), Percentile(errors, 0.95), Percentile(ages, 0.95), rates.Average(), Game.FPS);
            LogManager.VerboseLog(LastSummary);
        }

        private static T Percentile<T>(List<T> sorted, double p)
        {
            var index = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }

    /// <summary>GTAN_RECORD_ROUTE=1: every pure sync packet the local player sends is appended to logs\route-&lt;stamp&gt;.jsonl (T-018), for the bot's --route.</summary>
    internal static class RouteRecorder
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("GTAN_RECORD_ROUTE") == "1";
        private static System.IO.StreamWriter _writer;
        private static long _start;
        private static int _count;

        public static void Write(GTANetworkShared.Vector3 position, float heading)
        {
            if (!Enabled || position == null) return;
            try
            {
                if (_writer == null)
                {
                    var path = System.IO.Path.Combine(Main.GTANInstallDir, "logs", "route-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                    _writer = new System.IO.StreamWriter(path, false) { AutoFlush = true };
                    _start = Util.Util.TickCount;
                    LogManager.RuntimeLog("route: recording to " + path);
                }
                _writer.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{{\"t\":{0},\"x\":{1:0.###},\"y\":{2:0.###},\"z\":{3:0.###},\"h\":{4:0.#}}}", Util.Util.TickCount - _start, position.X, position.Y, position.Z, heading));
                _count++;
            }
            catch (Exception ex)
            {
                if (_count == 0) LogManager.RuntimeLog("route: recording failed: " + ex.Message);
            }
        }
    }
}

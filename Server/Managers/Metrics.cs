using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// The server's counters for the load harness (T-002): tick durations, packets and bytes in and out, GC collections,
    /// near-set sizes, connected players. Cheap enough to stay on all the time (a few Interlocked adds per packet); read as
    /// GET /metrics.json from the file server (only when &lt;httpserver&gt; is on) and by eng/load-test.sh.
    /// </summary>
    internal static class Metrics
    {
        private const int Ring = 600;            // the last 600 ticks (10 s at 60 Hz)
        private const int SampleWindow = 60;     // one-second samples kept

        private static readonly double[] TickMs = new double[Ring];
        private static int _tickIndex, _tickCount;
        private static long _ticks, _packetsIn, _bytesIn, _packetsOut, _bytesOut;
        private static readonly long[] TierSent = new long[4];
        private static long _budgetDropped, _voiceFrames, _voiceDropped, _cheatDetections;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object SampleLock = new object();
        private static readonly Queue<Sample> Samples = new Queue<Sample>();
        private static long _nextSampleMs;
        private static int _players, _nearMax;
        private static double _nearAvg;

        private struct Sample
        {
            public double Seconds;
            public long Ticks, PacketsIn, BytesIn, PacketsOut, BytesOut;
            public long Full, Medium, Low, Far, BudgetDropped, VoiceFrames, VoiceRelays;
        }

        /// <summary>One server tick took this long (Program.cs main loop).</summary>
        public static void TickDone(double ms)
        {
            lock (TickMs)
            {
                TickMs[_tickIndex] = ms;
                _tickIndex = (_tickIndex + 1) % Ring;
                if (_tickCount < Ring) _tickCount++;
            }
            Interlocked.Increment(ref _ticks);
        }

        /// <summary>A message arrived from a client (ProcessMessages).</summary>
        public static void PacketIn(int bytes)
        {
            Interlocked.Increment(ref _packetsIn);
            Interlocked.Add(ref _bytesIn, bytes);
        }

        /// <summary>A message went out to <paramref name="recipients"/> connections (GameServer.Send).</summary>
        public static void PacketsOut(int recipients, int bytes)
        {
            if (recipients <= 0) return;
            Interlocked.Add(ref _packetsOut, recipients);
            Interlocked.Add(ref _bytesOut, (long)bytes * recipients);
        }

        /// <summary>A sync packet was queued for one recipient of this tier (0 full, 1 medium, 2 low, 3 far).</summary>
        public static void InterestSent(int tier) { Interlocked.Increment(ref TierSent[tier]); }

        /// <summary>The anti-cheat raised a finding (T-017).</summary>
        public static void CheatDetected() { Interlocked.Increment(ref _cheatDetections); }

        /// <summary>A voice frame arrived from a player (T-015).</summary>
        public static void VoiceFrame() { Interlocked.Increment(ref _voiceFrames); }
        public static void VoiceDropped() { Interlocked.Increment(ref _voiceDropped); }

        /// <summary>A sync packet was not sent to a recipient because its byte budget for this second is used up.</summary>
        public static void InterestDropped() { Interlocked.Increment(ref _budgetDropped); }

        /// <summary>Called every tick; once a second it snapshots the counters and measures the near sets.</summary>
        public static void MaybeSample(List<Client> clients)
        {
            var nowMs = Clock.ElapsedMilliseconds;
            if (nowMs < _nextSampleMs) return;
            _nextSampleMs = nowMs + 1000;

            var players = 0; var nearMax = 0; long nearSum = 0;
            lock (clients)
            {
                for (var i = 0; i < clients.Count; i++)
                {
                    var c = clients[i];
                    if (c == null || c.Fake || !c.ConnectionConfirmed) continue;
                    players++;
                    var near = c.Streamer?.NearCount ?? 0;
                    nearSum += near;
                    if (near > nearMax) nearMax = near;
                }
            }

            lock (SampleLock)
            {
                _players = players;
                _nearMax = nearMax;
                _nearAvg = players > 0 ? nearSum / (double)players : 0;
                Samples.Enqueue(new Sample
                {
                    Seconds = nowMs / 1000.0,
                    Ticks = Interlocked.Read(ref _ticks),
                    PacketsIn = Interlocked.Read(ref _packetsIn), BytesIn = Interlocked.Read(ref _bytesIn),
                    PacketsOut = Interlocked.Read(ref _packetsOut), BytesOut = Interlocked.Read(ref _bytesOut),
                    Full = Interlocked.Read(ref TierSent[0]), Medium = Interlocked.Read(ref TierSent[1]), Low = Interlocked.Read(ref TierSent[2]), Far = Interlocked.Read(ref TierSent[3]),
                    BudgetDropped = Interlocked.Read(ref _budgetDropped),
                    VoiceFrames = Interlocked.Read(ref _voiceFrames), VoiceRelays = Program.ServerInstance?.Voice?.Relays ?? 0,
                });
                while (Samples.Count > SampleWindow) Samples.Dequeue();
            }
        }

        /// <summary>The /metrics.json document. Rates are averages over the last ~5 s of samples; tick percentiles over the last 600 ticks.</summary>
        public static string ToJson()
        {
            double[] ticks;
            lock (TickMs)
            {
                ticks = new double[_tickCount];
                Array.Copy(TickMs, ticks, _tickCount);
            }
            Array.Sort(ticks);

            Sample first, last; int players, nearMax; double nearAvg; int sampleCount;
            lock (SampleLock)
            {
                players = _players; nearMax = _nearMax; nearAvg = _nearAvg; sampleCount = Samples.Count;
                if (sampleCount == 0) { first = last = default(Sample); }
                else
                {
                    var arr = Samples.ToArray();
                    last = arr[arr.Length - 1];
                    first = arr[Math.Max(0, arr.Length - 6)];   // ~5 s back
                }
            }
            var dt = last.Seconds - first.Seconds;
            if (dt <= 0) dt = 1;

            var process = Process.GetCurrentProcess();
            var doc = new
            {
                uptimeSeconds = Math.Round(Clock.Elapsed.TotalSeconds, 1),
                players,
                tickMs = new
                {
                    p50 = Percentile(ticks, 0.50), p99 = Percentile(ticks, 0.99), max = ticks.Length > 0 ? ticks[ticks.Length - 1] : 0,
                    avg = ticks.Length > 0 ? Math.Round(Average(ticks), 3) : 0,
                },
                ticksPerSecond = sampleCount > 1 ? Math.Round((last.Ticks - first.Ticks) / dt, 1) : 0,
                @in = new { pps = Math.Round((last.PacketsIn - first.PacketsIn) / dt, 1), bps = Math.Round((last.BytesIn - first.BytesIn) / dt), packets = last.PacketsIn, bytes = last.BytesIn },
                @out = new { pps = Math.Round((last.PacketsOut - first.PacketsOut) / dt, 1), bps = Math.Round((last.BytesOut - first.BytesOut) / dt), packets = last.PacketsOut, bytes = last.BytesOut },
                gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), heapBytes = GC.GetTotalMemory(false) },
                near = new { avg = Math.Round(nearAvg, 1), max = nearMax },
                process = new { rssBytes = process.WorkingSet64, threads = process.Threads.Count, cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 1) },
                relay = RelaySnapshot(),
                anticheat = new { detections = Interlocked.Read(ref _cheatDetections), kicked = Program.ServerInstance?.Anticheat?.Kicked ?? 0, manifest = Program.ServerInstance?.Anticheat?.HasManifest ?? false },
                voice = new { framesPps = Math.Round((last.VoiceFrames - first.VoiceFrames) / dt), relaysPps = Math.Round((last.VoiceRelays - first.VoiceRelays) / dt), dropped = Interlocked.Read(ref _voiceDropped) + (Program.ServerInstance?.Voice?.FramesDropped ?? 0) },
                interest = new
                {
                    fullPps = Math.Round((last.Full - first.Full) / dt), mediumPps = Math.Round((last.Medium - first.Medium) / dt),
                    lowPps = Math.Round((last.Low - first.Low) / dt), farPps = Math.Round((last.Far - first.Far) / dt),
                    budgetDroppedPps = Math.Round((last.BudgetDropped - first.BudgetDropped) / dt), budgetDropped = last.BudgetDropped,
                },
            };
            return JsonConvert.SerializeObject(doc);
        }

        private static object RelaySnapshot()
        {
            var relay = Program.ServerInstance?.Relay;
            if (relay == null) return new { workers = 0, queued = 0, dropped = 0L, lidgrenDropped = 0L };
            return new { workers = relay.Workers, queued = relay.Queued, dropped = relay.Dropped, lidgrenDropped = relay.LidgrenDropped };
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            var index = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return Math.Round(sorted[index], 3);
        }

        private static double Average(double[] values)
        {
            double sum = 0;
            for (var i = 0; i < values.Length; i++) sum += values[i];
            return sum / values.Length;
        }
    }
}

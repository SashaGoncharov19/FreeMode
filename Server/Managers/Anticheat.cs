using System;
using System.Collections.Generic;
using System.IO;
using GTANetworkServer.Constant;
using GTANetworkShared;
using Newtonsoft.Json;

namespace GTANetworkServer.Managers
{
    /// <summary>Per-player state of the checks (T-017).</summary>
    internal sealed class AnticheatState
    {
        public Vector3 LastPosition;
        public long LastPositionMs;
        public int SpeedStrikes;
        public long GraceUntilMs;
        public readonly Dictionary<string, long> LastFlagMs = new Dictionary<string, long>();
        public bool IntegrityChecked;
    }

    /// <summary>
    /// The anti-cheat baseline (T-017): every position a client claims is checked against speed and teleport limits (on foot
    /// &lt;footspeed&gt; m/s, in a vehicle the model's MaxSpeed × &lt;speedfactor&gt;, at least 60 m/s), health and armour against
    /// the game's maxima, and the client's integrity report against manifest.json next to the server when there is one. A finding
    /// raises API.onCheatDetected(player, kind, evidence) (also "cheatDetected" for TypeScript) at most once per kind per 5 s, and
    /// the server acts by itself according to &lt;anticheat action="log|kick|ban"&gt;. A grace period follows connect, respawn and
    /// a position set by the server, so the server's own teleports never count.
    /// </summary>
    internal sealed class Anticheat
    {
        private const int FlagIntervalMs = 5000;
        private const int SpeedStrikesNeeded = 3;

        private readonly GameServer _server;
        private readonly AnticheatSettings _settings;
        private readonly Dictionary<string, string> _manifest;   // relative path -> sha256 (lower-case); null = no manifest
        private readonly string _manifestVersion;
        private long _detections, _kicked;

        public Anticheat(GameServer server, AnticheatSettings settings, string manifestPath)
        {
            _server = server;
            _settings = settings ?? new AnticheatSettings();
            try
            {
                if (File.Exists(manifestPath))
                {
                    var doc = JsonConvert.DeserializeObject<ManifestDocument>(File.ReadAllText(manifestPath));
                    if (doc?.files != null && doc.files.Count > 0)
                    {
                        _manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in doc.files) _manifest[pair.Key.Replace('\\', '/')] = (pair.Value ?? "").Trim().ToLowerInvariant();
                        _manifestVersion = doc.version;
                        Program.Output("Anti-cheat: client manifest " + (doc.version ?? "?") + " with " + _manifest.Count + " files loaded; integrity=" + _settings.Integrity, LogCat.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Output("Anti-cheat: manifest.json could not be read: " + ex.Message, LogCat.Warn);
            }
        }

        private sealed class ManifestDocument
        {
            public string version { get; set; }
            public Dictionary<string, string> files { get; set; }
        }

        public string Action { get { return _settings.Action; } set { _settings.Action = value; } }
        public long Detections => System.Threading.Interlocked.Read(ref _detections);
        public long Kicked => System.Threading.Interlocked.Read(ref _kicked);
        public bool HasManifest => _manifest != null;

        /// <summary>No speed or teleport findings until <paramref name="ms"/> from now (connect, respawn, a server teleport).</summary>
        public void Grace(Client client, long now, int ms)
        {
            if (client == null) return;
            var state = client.Anticheat;
            state.GraceUntilMs = Math.Max(state.GraceUntilMs, now + ms);
            state.LastPosition = null;
            state.SpeedStrikes = 0;
        }

        /// <summary>A pure ped sync packet: speed and teleport on foot, health and armour.</summary>
        public void CheckPed(Client client, PedData packet, long now)
        {
            if (packet.PlayerHealth.HasValue && packet.PlayerHealth.Value > 200) Flag(client, "health", "health " + packet.PlayerHealth.Value + " over the game's 200", now);
            if (packet.PedArmor.HasValue && packet.PedArmor.Value > 100) Flag(client, "armour", "armour " + packet.PedArmor.Value + " over the game's 100", now);
            CheckMovement(client, packet.Position, Math.Max(60f, _settings.FootSpeed), "on foot", now);
        }

        /// <summary>A pure vehicle sync packet from the driver: speed and teleport against the model's MaxSpeed.</summary>
        public void CheckVehicle(Client client, VehicleData packet, int modelHash, long now)
        {
            float maxSpeed = 0; string name = null;
            try
            {
                var data = ConstantVehicleDataOrganizer.Get(modelHash);   // a struct; unknown models give an empty one
                maxSpeed = data.MaxSpeed; name = data.DisplayName;
            }
            catch (Exception) { /* unknown model: the generic limit below */ }
            var limit = maxSpeed > 0 ? Math.Max(60f, maxSpeed * Math.Max(1f, _settings.SpeedFactor)) : 120f;
            CheckMovement(client, packet.Position, limit, string.IsNullOrEmpty(name) ? "driving" : "driving " + name, now);
        }

        private void CheckMovement(Client client, Vector3 position, float limit, string how, long now)
        {
            if (position == null) return;
            var state = client.Anticheat;
            var last = state.LastPosition;
            var lastMs = state.LastPositionMs;
            state.LastPosition = position;
            state.LastPositionMs = now;
            if (last == null || now < state.GraceUntilMs) { state.SpeedStrikes = 0; return; }
            var dt = now - lastMs;
            if (dt < 50) return;
            var dx = position.X - last.X; var dy = position.Y - last.Y;
            var distance = (float)Math.Sqrt(dx * dx + dy * dy);
            if (distance > _settings.TeleportDistance && dt < 1500)
            {
                Flag(client, "teleport", string.Format("{0:0} m in {1} ms {2}", distance, dt, how), now);
                state.SpeedStrikes = 0;
                return;
            }
            var speed = distance / (dt / 1000f);
            if (speed > limit)
            {
                if (++state.SpeedStrikes >= SpeedStrikesNeeded)
                {
                    Flag(client, "speed", string.Format("{0:0} m/s over {1:0} m/s {2}", speed, limit, how), now);
                    state.SpeedStrikes = 0;
                }
            }
            else state.SpeedStrikes = 0;
        }

        /// <summary>The integrity report of the connection request against the manifest (only when the server has one).</summary>
        public void CheckIntegrity(Client client, IntegrityReport report, long now)
        {
            if (_manifest == null || string.Equals(_settings.Integrity, "off", StringComparison.OrdinalIgnoreCase)) return;
            var state = client.Anticheat;
            if (state.IntegrityChecked) return;
            state.IntegrityChecked = true;
            if (report == null || report.Files == null || report.Files.Count == 0)
            {
                Flag(client, "integrity", "the client sent no integrity report (older client, or the report was removed)", now, _settings.Integrity);
                return;
            }
            var reported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in report.Files) if (file?.Name != null) reported[file.Name.Replace('\\', '/')] = (file.Sha256 ?? "").ToLowerInvariant();
            var differ = new List<string>();
            foreach (var pair in _manifest)
            {
                string hash;
                if (!reported.TryGetValue(pair.Key, out hash)) differ.Add(pair.Key + " (not reported)");
                else if (hash != pair.Value) differ.Add(pair.Key);
            }
            if (differ.Count > 0) Flag(client, "integrity", string.Join(", ", differ) + " differ from the manifest " + (_manifestVersion ?? "") + " (client says " + (report.Version ?? "?") + ")", now, _settings.Integrity);
        }

        private void Flag(Client client, string kind, string evidence, long now, string actionOverride = null)
        {
            var state = client.Anticheat;
            long last;
            if (state.LastFlagMs.TryGetValue(kind, out last) && now - last < FlagIntervalMs) return;
            state.LastFlagMs[kind] = now;
            System.Threading.Interlocked.Increment(ref _detections);
            Metrics.CheatDetected();
            Program.Output("Cheat detected: " + kind + " by " + client.Name + " (" + client.NetConnection?.RemoteEndPoint?.Address + "): " + evidence, LogCat.Warn);
            try
            {
                lock (_server.RunningResources)
                    _server.RunningResources.ForEach(fs => fs.Engines.ForEach(en => en.InvokeCheatDetected(client, kind, evidence)));
            }
            catch (Exception ex)
            {
                Program.Output("Anti-cheat: the event handler failed: " + ex.Message, LogCat.Warn);
            }
            var action = (actionOverride ?? _settings.Action ?? "log").Trim().ToLowerInvariant();
            if (action == "kick" || action == "ban")
            {
                System.Threading.Interlocked.Increment(ref _kicked);
                var reason = "Cheat detected: " + kind + ".";
                if (action == "ban") _server.PublicAPI.banPlayer(client, reason);
                else _server.PublicAPI.kickPlayer(client, reason);
            }
        }
    }
}

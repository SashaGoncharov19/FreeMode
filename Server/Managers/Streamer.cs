using System;
using System.Collections.Generic;
using System.Threading;
using GTANetworkServer.Constant;
using GTANetworkShared;
using Lidgren.Network;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Recipient sets of one player's sync packets (T-003 interest management). Every 250 ms the streamer thread indexes all
    /// confirmed players in a grid of <c>cell</c>-metre cells per dimension and computes, for every player as a *sender*, who
    /// receives its packets at which rate: <c>Full</c> (within <c>full</c> m, nearest first, at most <c>maxfull</c>) gets every
    /// pure packet (10 Hz), <c>Medium</c> (within <c>medium</c> m) every third, <c>Low</c> (within <c>range</c> m, at most
    /// <c>maxnear</c> in all three tiers together) every tenth; <c>Far</c> (everyone else, other dimensions included) gets one
    /// position-only packet every 3 s. Players in dimension 0 see and are seen by every dimension. The per-recipient byte budget
    /// is applied when the packets are sent (Packets.cs). The arrays are replaced atomically and never mutated.
    /// </summary>
    internal class Streamer
    {
        public static bool Stop;

        private struct Entry
        {
            public Client Client;
            public Vector3 Position;
            public int Dimension;
        }

        private struct Candidate : IComparable<Candidate>
        {
            public float DistanceSquared;
            public Client Client;
            public int CompareTo(Candidate other) => DistanceSquared.CompareTo(other.DistanceSquared);
        }

        public static void MainThread()
        {
            while (!Stop)
            {
                try
                {
                    Pass();
                }
                catch (Exception ex)
                {
                    Program.Output("Streamer pass failed: " + ex.GetType().Name + ": " + ex.Message, LogCat.Warn);
                }
                Thread.Sleep(250);
            }
        }

        /// <summary>One pass: index everyone, then compute every sender's tiers.</summary>
        private static void Pass()
        {
            var server = Program.ServerInstance;
            if (server == null) return;
            var settings = server.Interest ?? new InterestSettings();
            var cell = Math.Max(50f, settings.CellSize);

            List<Client> players;
            lock (server.Clients) players = new List<Client>(server.Clients);
            var properties = server.NetEntityHandler.ToDict();

            var entries = new List<Entry>(players.Count);
            var grids = new Dictionary<int, Dictionary<long, List<Client>>>();
            foreach (var client in players)
            {
                if (client == null || client.Fake || client.NetConnection == null || !client.ConnectionConfirmed) continue;
                if (client.NetConnection.Status == NetConnectionStatus.Disconnected) continue;
                var position = client.Position;
                EntityProperties props;
                var dimension = properties.TryGetValue(client.handle.Value, out props) ? props.Dimension : 0;
                entries.Add(new Entry { Client = client, Position = position, Dimension = dimension });
                if (position == null) continue;
                Dictionary<long, List<Client>> grid;
                if (!grids.TryGetValue(dimension, out grid)) grids[dimension] = grid = new Dictionary<long, List<Client>>();
                var key = CellKey(position, cell);
                List<Client> bucket;
                if (!grid.TryGetValue(key, out bucket)) grid[key] = bucket = new List<Client>();
                bucket.Add(client);
            }

            var candidates = new List<Candidate>();
            var seen = new HashSet<Client>();
            foreach (var entry in entries)
            {
                try
                {
                    entry.Client.Streamer?.Compute(entry, entries, grids, settings, cell, candidates, seen);
                }
                catch (Exception)
                {
                    // a player that vanished mid-pass; the next pass starts from scratch
                }
            }

            CatchUpEntities(server, entries, settings, cell);
        }

        /// <summary>T-026: entities a player can see now but has never received (created out of range, or the player moved) get their create.</summary>
        private static void CatchUpEntities(GameServer server, List<Entry> entries, InterestSettings settings, float cell)
        {
            var range = Math.Max(cell, settings.Range);
            var rangeSquared = range * range;
            var cells = (int)Math.Ceiling(range / cell);
            var grid = new Dictionary<int, Dictionary<long, List<KeyValuePair<int, EntityProperties>>>>();   // dimension -> cell -> entities
            var count = 0;
            foreach (var pair in server.NetEntityHandler.ToCopy())
            {
                var e = pair.Value;
                if (e == null || e.Position == null || !GameServer.IsRangeLimitedEntity((EntityType)e.EntityType)) continue;
                Dictionary<long, List<KeyValuePair<int, EntityProperties>>> byCell;
                if (!grid.TryGetValue(e.Dimension, out byCell)) grid[e.Dimension] = byCell = new Dictionary<long, List<KeyValuePair<int, EntityProperties>>>();
                var key = CellKey(e.Position, cell);
                List<KeyValuePair<int, EntityProperties>> bucket;
                if (!byCell.TryGetValue(key, out bucket)) byCell[key] = bucket = new List<KeyValuePair<int, EntityProperties>>();
                bucket.Add(pair);
                count++;
            }
            if (count == 0) return;

            foreach (var entry in entries)
            {
                if (entry.Position == null) continue;
                var client = entry.Client;
                var cx = (int)Math.Floor(entry.Position.X / cell);
                var cy = (int)Math.Floor(entry.Position.Y / cell);
                var sent = 0;
                foreach (var dim in grid)
                {
                    if (entry.Dimension != 0 && dim.Key != 0 && dim.Key != entry.Dimension) continue;
                    for (var x = cx - cells; x <= cx + cells && sent < 40; x++)
                    {
                        for (var y = cy - cells; y <= cy + cells && sent < 40; y++)
                        {
                            List<KeyValuePair<int, EntityProperties>> bucket;
                            if (!dim.Value.TryGetValue(((long)x << 32) | (uint)y, out bucket)) continue;
                            foreach (var pair in bucket)
                            {
                                if (entry.Position.DistanceToSquared(pair.Value.Position) > rangeSquared) continue;
                                bool known;
                                lock (client.KnownLock) known = client.KnownEntities.Contains(pair.Key);
                                if (known) continue;
                                lock (client.KnownLock) client.KnownEntities.Add(pair.Key);
                                server.SendToClient(client, new CreateEntity { EntityType = pair.Value.EntityType, NetHandle = pair.Key, Properties = pair.Value }, PacketType.CreateEntity, true, ConnectionChannel.EntityBackend);
                                if (++sent >= 40) break;   // at most 40 catch-up creates per player per pass (every 250 ms)
                            }
                        }
                    }
                }
            }
        }

        private static long CellKey(Vector3 position, float cell)
        {
            var cx = (int)Math.Floor(position.X / cell);
            var cy = (int)Math.Floor(position.Y / cell);
            return ((long)cx << 32) | (uint)cy;
        }

        public Streamer(Client f)
        {
            _parent = f;
        }

        private readonly Client _parent;
        private Client[] _full = Array.Empty<Client>();     // every pure packet
        private Client[] _medium = Array.Empty<Client>();   // every third
        private Client[] _low = Array.Empty<Client>();      // every tenth
        private Client[] _near = Array.Empty<Client>();     // the three tiers together (bullets, unoccupied vehicles, light sync)
        private Client[] _far = Array.Empty<Client>();      // one position every 3 s

        public Client[] Full => _full;
        public Client[] Medium => _medium;
        public Client[] Low => _low;
        public Client[] Far => _far;

        public IEnumerable<Client> GetNearClients() => _near;
        public IEnumerable<Client> GetFarClients() => _far;

        /// <summary>How many players receive this one within range (the three rate tiers together; Metrics).</summary>
        public int NearCount => _near.Length;

        private void Compute(Entry me, List<Entry> all, Dictionary<int, Dictionary<long, List<Client>>> grids, InterestSettings settings, float cell, List<Candidate> candidates, HashSet<Client> seen)
        {
            if (me.Position == null) return;   // nothing received from this player yet; keep the previous sets

            var range = Math.Max(cell, settings.Range);
            var rangeSquared = range * range;
            var fullSquared = (float)settings.FullRange * settings.FullRange;
            var mediumSquared = (float)settings.MediumRange * settings.MediumRange;
            var cells = (int)Math.Ceiling(range / cell);
            var cx = (int)Math.Floor(me.Position.X / cell);
            var cy = (int)Math.Floor(me.Position.Y / cell);

            candidates.Clear();
            seen.Clear();
            foreach (var pair in grids)
            {
                // a dimension-0 player sees every dimension; everyone sees dimension 0 and their own
                if (me.Dimension != 0 && pair.Key != 0 && pair.Key != me.Dimension) continue;
                var grid = pair.Value;
                for (var x = cx - cells; x <= cx + cells; x++)
                {
                    for (var y = cy - cells; y <= cy + cells; y++)
                    {
                        List<Client> bucket;
                        if (!grid.TryGetValue(((long)x << 32) | (uint)y, out bucket)) continue;
                        foreach (var other in bucket)
                        {
                            if (other == _parent) continue;
                            var position = other.Position;
                            if (position == null) continue;
                            var distance = me.Position.DistanceToSquared(position);
                            if (distance > rangeSquared) continue;
                            candidates.Add(new Candidate { DistanceSquared = distance, Client = other });
                        }
                    }
                }
            }
            candidates.Sort();

            var maxNear = Math.Max(1, settings.MaxNear);
            var maxFull = Math.Max(0, settings.MaxFull);
            var full = new List<Client>();
            var medium = new List<Client>();
            var low = new List<Client>();
            for (var i = 0; i < candidates.Count && i < maxNear; i++)
            {
                var c = candidates[i];
                if (c.DistanceSquared <= fullSquared && full.Count < maxFull) full.Add(c.Client);
                else if (c.DistanceSquared <= mediumSquared) medium.Add(c.Client);
                else low.Add(c.Client);
                seen.Add(c.Client);
            }

            var far = new List<Client>();
            foreach (var entry in all)
            {
                if (entry.Client == _parent || seen.Contains(entry.Client)) continue;
                far.Add(entry.Client);
            }

            var near = new Client[full.Count + medium.Count + low.Count];
            full.CopyTo(near, 0);
            medium.CopyTo(near, full.Count);
            low.CopyTo(near, full.Count + medium.Count);

            _full = full.ToArray();
            _medium = medium.ToArray();
            _low = low.ToArray();
            _near = near;
            _far = far.ToArray();
        }
    }
}

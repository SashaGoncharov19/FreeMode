using System;
using System.Collections.Generic;
using System.Threading;
using GTANetworkShared;
using Lidgren.Network;

namespace GTANetworkServer.Managers
{
    // Recipient sets of one player's sync packets, refreshed once per second by the streamer thread:
    // "near" players (same or global dimension, within NearRange, closest first, at most MaxNear) receive
    // every pure and light sync packet; everyone else receives one position-only packet per second.
    internal class Streamer
    {
        public const float NearRange = 2500f;   // the client streams players in at 2000 m; margin for 1 s old positions
        public const int MaxNear = 250;         // the client's player budget

        public static bool Stop;
        public static void MainThread()
        {
            while (!Stop)
            {
                foreach (var client in Program.ServerInstance.PublicAPI.getAllPlayers())
                {
                    try
                    {
                        client.Streamer.Pulse();
                    }
                    catch (Exception)
                    {
                        // a player that vanished mid-pulse; the next pulse starts from scratch
                    }
                }

                Thread.Sleep(100);
            }
        }

        public Streamer(Client f)
        {
            _parent = f;
        }

        private readonly Client _parent;
        private Client[] _near = Array.Empty<Client>();   // replaced atomically, never mutated
        private Client[] _far = Array.Empty<Client>();
        private long _lastUpdate;

        public IEnumerable<Client> GetNearClients()
        {
            return _near;
        }

        public IEnumerable<Client> GetFarClients()
        {
            return _far;
        }

        private static int DimensionOf(Client client)
        {
            EntityProperties properties;
            return Program.ServerInstance.NetEntityHandler.ToDict().TryGetValue(client.handle.Value, out properties) ? properties.Dimension : 0;
        }

        public void Pulse()
        {
            var now = Program.MonotonicMs();
            if (now - _lastUpdate <= 1000) return;
            _lastUpdate = now;

            var parentPosition = _parent.Position;
            if (parentPosition == null) return;   // nothing received from this player yet; keep the previous sets
            var parentDimension = DimensionOf(_parent);

            var near = new List<KeyValuePair<float, Client>>();
            var far = new List<Client>();

            foreach (var client in Program.ServerInstance.PublicAPI.getAllPlayers())
            {
                if (client == _parent || client.Fake || client.NetConnection == null || !client.ConnectionConfirmed) continue;
                if (client.NetConnection.Status == NetConnectionStatus.Disconnected) continue;

                var position = client.Position;
                if (position == null)
                {
                    far.Add(client);
                    continue;
                }

                var dimension = DimensionOf(client);
                var sameWorld = parentDimension == 0 || dimension == 0 || parentDimension == dimension;
                var distance = parentPosition.DistanceToSquared(position);

                if (sameWorld && distance <= NearRange * NearRange) near.Add(new KeyValuePair<float, Client>(distance, client));
                else far.Add(client);
            }

            near.Sort((a, b) => a.Key.CompareTo(b.Key));

            var nearArray = new Client[Math.Min(near.Count, MaxNear)];
            for (var i = 0; i < nearArray.Length; i++) nearArray[i] = near[i].Value;
            for (var i = nearArray.Length; i < near.Count; i++) far.Add(near[i].Value);

            _near = nearArray;
            _far = far.ToArray();
        }
    }
}

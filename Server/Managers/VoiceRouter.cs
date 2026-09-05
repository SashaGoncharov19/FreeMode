using System;
using System.Collections.Generic;
using GTANetworkServer.Constant;
using GTANetworkShared;
using Lidgren.Network;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Voice chat relay (T-015). A client sends 20 ms Opus frames as PacketType.Voice on the unreliable Voice channel; the server
    /// never decodes them. Channel 0 is proximity voice: the frame goes to the players within the talker's voice range (the
    /// server's default or a per-player override) among the talker's near tiers (same dimension rules as sync). A channel above 0
    /// is a radio: every player on that channel hears the talker wherever they are. Listeners can mute talkers. A frame is at most
    /// 400 bytes and a player may send 60 frames per second; more is dropped. The first frame raises onPlayerStartTalking, 300 ms
    /// without a frame raises onPlayerStopTalking. The relayed packet is [Voice][int talker handle][int length][frame].
    /// </summary>
    internal sealed class VoiceRouter
    {
        public const int MaxFrameBytes = 400;
        public const int MaxFramesPerSecond = 60;
        private const int StopTalkingAfterMs = 300;

        private sealed class State
        {
            public int Channel;
            public float? Range;
            public HashSet<int> MutedTalkers;
            public long LastFrameMs, WindowStartMs;
            public int FramesInWindow;
            public bool Talking;
        }

        private readonly GameServer _server;
        private readonly Dictionary<Client, State> _states = new Dictionary<Client, State>();
        private readonly object _lock = new object();
        private long _framesIn, _framesDropped, _relays;

        public VoiceRouter(GameServer server)
        {
            _server = server;
        }

        /// <summary>Metres a talker on channel 0 is heard within, unless the player has its own range.</summary>
        public float DefaultRange { get; set; } = 40f;

        public long FramesIn => System.Threading.Interlocked.Read(ref _framesIn);
        public long FramesDropped => System.Threading.Interlocked.Read(ref _framesDropped);
        public long Relays => System.Threading.Interlocked.Read(ref _relays);

        private State Of(Client client)
        {
            State state;
            if (!_states.TryGetValue(client, out state)) _states[client] = state = new State();
            return state;
        }

        public int GetChannel(Client player) { lock (_lock) return Of(player).Channel; }
        public void SetChannel(Client player, int channel) { lock (_lock) Of(player).Channel = Math.Max(0, channel); }
        public float GetRange(Client player) { lock (_lock) return Of(player).Range ?? DefaultRange; }
        public void SetRange(Client player, float metres) { lock (_lock) Of(player).Range = metres > 0 ? metres : (float?)null; }

        public void Mute(Client listener, Client talker, bool muted)
        {
            lock (_lock)
            {
                var state = Of(listener);
                if (muted) (state.MutedTalkers ?? (state.MutedTalkers = new HashSet<int>())).Add(talker.handle.Value);
                else state.MutedTalkers?.Remove(talker.handle.Value);
            }
        }

        public bool IsMuted(Client listener, Client talker)
        {
            lock (_lock) { State s; return _states.TryGetValue(listener, out s) && s.MutedTalkers != null && s.MutedTalkers.Contains(talker.handle.Value); }
        }

        public void Remove(Client player)
        {
            lock (_lock) _states.Remove(player);
        }

        /// <summary>A frame from <paramref name="talker"/> (tick thread): rate-checked, then relayed to its listeners.</summary>
        public void Relay(Client talker, byte[] frame, long now)
        {
            System.Threading.Interlocked.Increment(ref _framesIn);
            if (frame == null || frame.Length == 0 || frame.Length > MaxFrameBytes) { System.Threading.Interlocked.Increment(ref _framesDropped); return; }

            int channel; float range; bool startedTalking = false;
            lock (_lock)
            {
                var state = Of(talker);
                if (now - state.WindowStartMs >= 1000) { state.WindowStartMs = now; state.FramesInWindow = 0; }
                if (++state.FramesInWindow > MaxFramesPerSecond) { System.Threading.Interlocked.Increment(ref _framesDropped); return; }
                state.LastFrameMs = now;
                if (!state.Talking) { state.Talking = true; startedTalking = true; }
                channel = state.Channel;
                range = state.Range ?? DefaultRange;
            }
            if (startedTalking) FireTalking(talker, true);

            var recipients = new List<NetConnection>();
            if (channel == 0)
            {
                var position = talker.Position;
                if (position == null) return;
                var rangeSquared = range * range;
                foreach (var listener in talker.Streamer.GetNearClients())
                {
                    if (!CanHear(listener, talker)) continue;
                    var other = listener.Position;
                    if (other == null || position.DistanceToSquared(other) > rangeSquared) continue;
                    recipients.Add(listener.NetConnection);
                }
            }
            else
            {
                List<Client> clients;
                lock (_server.Clients) clients = new List<Client>(_server.Clients);
                lock (_lock)
                {
                    foreach (var listener in clients)
                    {
                        if (listener == talker || !CanHear(listener, talker)) continue;
                        State s;
                        if (_states.TryGetValue(listener, out s) && s.Channel == channel) recipients.Add(listener.NetConnection);
                    }
                }
            }
            if (recipients.Count == 0) return;

            var msg = _server.Server.CreateMessage(9 + frame.Length);
            msg.Write((byte)PacketType.Voice);
            msg.Write(talker.handle.Value);
            msg.Write(frame.Length);
            msg.Write(frame);
            _server.Send(msg, recipients, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.Voice);
            System.Threading.Interlocked.Add(ref _relays, recipients.Count);
        }

        private bool CanHear(Client listener, Client talker)
        {
            if (listener == null || listener == talker || listener.Fake || listener.NetConnection == null || !listener.ConnectionConfirmed) return false;
            if (listener.NetConnection.Status == NetConnectionStatus.Disconnected) return false;
            return !IsMuted(listener, talker);
        }

        /// <summary>Every tick: talkers silent for 300 ms stop talking.</summary>
        public void Tick(long now)
        {
            List<Client> stopped = null;
            lock (_lock)
            {
                foreach (var pair in _states)
                {
                    if (pair.Value.Talking && now - pair.Value.LastFrameMs > StopTalkingAfterMs)
                    {
                        pair.Value.Talking = false;
                        (stopped ?? (stopped = new List<Client>())).Add(pair.Key);
                    }
                }
            }
            if (stopped != null) foreach (var c in stopped) FireTalking(c, false);
        }

        private void FireTalking(Client player, bool started)
        {
            try
            {
                lock (_server.RunningResources)
                    _server.RunningResources.ForEach(fs => fs.Engines.ForEach(en => { if (started) en.InvokePlayerStartTalking(player); else en.InvokePlayerStopTalking(player); }));
            }
            catch (Exception ex)
            {
                Program.Output("Voice: talking event failed: " + ex.Message, LogCat.Warn);
            }
        }
    }
}

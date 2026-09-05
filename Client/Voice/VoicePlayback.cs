using System;
using System.Collections.Generic;
using Concentus;
using GTA;
using GTA.Math;
using GTANetwork.Streamer;
using GTANetwork.Sync;
using GTANetwork.Util;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GTANetwork.Voice
{
    /// <summary>
    /// Playback of other players' voice (T-016): one Opus decoder and one buffered stream per talker, mixed into a single 48 kHz
    /// stereo output (WinMM WaveOutEvent, 80 ms latency). Every game tick the volume of each talker follows its synced position
    /// (linear falloff to silence at <see cref="HearingRange"/> metres) and the pan follows the direction relative to the camera.
    /// Frames arrive on the network thread; NAudio's output thread mixes; the game thread only updates volume and pan.
    /// </summary>
    internal static class VoicePlayback
    {
        public const float HearingRange = 45f;   // the server's default voice range is 40 m; a little slack for late positions
        private const int SampleRate = 48000;
        private const int FrameSamples = 960;

        private sealed class Talker
        {
            public IOpusDecoder Decoder;
            public BufferedWaveProvider Buffer;
            public VolumeSampleProvider Volume;
            public PanningSampleProvider Pan;
            public long LastFrameMs;
            public int Frames;
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<int, Talker> Talkers = new Dictionary<int, Talker>();
        private static MixingSampleProvider _mixer;
        private static IWavePlayer _output;
        private static int _errorsLogged;
        private static string _lastError;

        public static bool IsOpen { get { lock (Lock) return _output != null; } }
        public static int TalkerCount { get { lock (Lock) return Talkers.Count; } }
        public static string LastError => _lastError;

        /// <summary>The master volume, 0..1 (settings: VoiceVolume / 100).</summary>
        public static float MasterVolume = 1f;

        private static bool EnsureOpen()
        {
            if (_output != null) return true;
            try
            {
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
                var output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 4 };
                output.Init(_mixer);
                output.Play();
                _output = output;
                LogManager.RuntimeLog("voice: playback open (WaveOut, 48 kHz stereo, 80 ms)");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = "playback could not start: " + ex.Message;
                if (_errorsLogged++ < 3) LogManager.RuntimeLog("voice: " + _lastError);
                _output = null;
                return false;
            }
        }

        /// <summary>A relayed frame of <paramref name="talkerHandle"/> (network thread).</summary>
        public static void Enqueue(int talkerHandle, byte[] frame, long nowMs)
        {
            if (!Main.PlayerSettings.VoiceEnabled || frame == null || frame.Length == 0) return;
            try
            {
                lock (Lock)
                {
                    if (!EnsureOpen()) return;
                    Talker talker;
                    if (!Talkers.TryGetValue(talkerHandle, out talker))
                    {
                        talker = new Talker
                        {
                            Decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1),
                            Buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1)) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true },
                        };
                        talker.Volume = new VolumeSampleProvider(talker.Buffer.ToSampleProvider()) { Volume = 0f };
                        talker.Pan = new PanningSampleProvider(talker.Volume) { Pan = 0f };
                        _mixer.AddMixerInput(talker.Pan);
                        Talkers[talkerHandle] = talker;
                    }
                    var pcm = new short[FrameSamples];
                    var samples = talker.Decoder.Decode(frame, pcm, FrameSamples, false);
                    if (samples <= 0) return;
                    var bytes = new byte[samples * 2];
                    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                    talker.Buffer.AddSamples(bytes, 0, bytes.Length);
                    talker.LastFrameMs = nowMs;
                    talker.Frames++;
                }
                var ped = Main.NetEntityHandler.NetToStreamedItem(talkerHandle) as SyncPed;
                if (ped != null) ped.TalkingUntilMs = nowMs + 300;
            }
            catch (Exception ex)
            {
                if (_errorsLogged++ < 3) LogManager.RuntimeLog("voice: decode failed: " + ex.Message);
            }
        }

        /// <summary>Game thread, every tick: volume by distance, pan by direction, forget silent talkers.</summary>
        public static void Tick(long nowMs)
        {
            if (_output == null) return;
            Vector3 listener, forward;
            try
            {
                listener = GameplayCamera.Position;
                forward = GameplayCamera.Direction;
            }
            catch { return; }
            var right = Vector3.Cross(forward, Vector3.WorldUp);
            if (right.LengthSquared() < 0.0001f) right = new Vector3(1f, 0f, 0f);
            right.Normalize();
            var master = Math.Max(0f, Math.Min(1f, MasterVolume));

            List<int> gone = null;
            lock (Lock)
            {
                foreach (var pair in Talkers)
                {
                    var talker = pair.Value;
                    if (nowMs - talker.LastFrameMs > 10000)
                    {
                        (gone ?? (gone = new List<int>())).Add(pair.Key);
                        continue;
                    }
                    var ped = Main.NetEntityHandler.NetToStreamedItem(pair.Key) as SyncPed;
                    if (ped == null)
                    {
                        // no position: radio-like, full volume in the middle
                        talker.Volume.Volume = master;
                        talker.Pan.Pan = 0f;
                        continue;
                    }
                    var position = ped.Position;
                    var offset = position - listener;
                    var distance = offset.Length();
                    var attenuation = distance >= HearingRange ? 0f : 1f - distance / HearingRange;
                    if (distance < 2f) attenuation = 1f;
                    talker.Volume.Volume = master * attenuation;
                    if (distance > 0.5f)
                    {
                        offset.Normalize();
                        talker.Pan.Pan = Math.Max(-1f, Math.Min(1f, Vector3.Dot(right, offset)));
                    }
                    else talker.Pan.Pan = 0f;
                }
                if (gone != null)
                {
                    foreach (var handle in gone)
                    {
                        var talker = Talkers[handle];
                        try { _mixer.RemoveMixerInput(talker.Pan); } catch { /* ignored */ }
                        Talkers.Remove(handle);
                    }
                }
            }
        }

        public static void Close()
        {
            lock (Lock)
            {
                var output = _output;
                _output = null;
                Talkers.Clear();
                if (output == null) return;
                try { output.Stop(); output.Dispose(); } catch { /* ignored */ }
                LogManager.RuntimeLog("voice: playback closed");
            }
        }
    }
}

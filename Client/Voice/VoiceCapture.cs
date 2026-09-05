using System;
using System.Collections.Generic;
using System.Threading;
using Concentus;
using Concentus.Enums;
using GTANetwork.Util;
using GTANetworkShared;
using Lidgren.Network;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GTANetwork.Voice
{
    /// <summary>
    /// The microphone side of voice chat (T-016). While the push-to-talk key is held, the default capture device (WASAPI shared
    /// mode, WinMM as the fallback) is read, converted to 48 kHz 16-bit mono, encoded into 20 ms Opus frames (24 kbit/s) and sent
    /// as PacketType.Voice. The device stays open between presses (opening it costs ~100 ms); it is closed on disconnect.
    /// Everything runs on NAudio's capture thread; the game thread only flips <see cref="Talking"/>.
    /// </summary>
    internal static class VoiceCapture
    {
        public const int SampleRate = 48000;
        public const int FrameSamples = 960;   // 20 ms

        private static readonly object Lock = new object();
        private static IWaveIn _device;
        private static IOpusEncoder _encoder;
        private static WaveFormat _format;
        private static readonly List<short> _pending = new List<short>(FrameSamples * 4);
        private static readonly byte[] _encoded = new byte[400];
        private static double _resamplePosition;
        private static float _lastSample;
        private static int _framesSent, _errorsLogged;
        private static volatile bool _talking;
        private static string _lastError;

        /// <summary>True while the push-to-talk key is down; frames are only sent then.</summary>
        public static bool Talking
        {
            get { return _talking; }
            set
            {
                if (_talking == value) return;
                _talking = value;
                if (value) EnsureOpen();
                else lock (Lock) _pending.Clear();
            }
        }

        public static bool IsOpen { get { lock (Lock) return _device != null; } }
        public static int FramesSent => _framesSent;
        public static string LastError => _lastError;

        /// <summary>Opens the capture device once; errors are logged and the device stays closed (no voice, no crash).</summary>
        public static void EnsureOpen()
        {
            lock (Lock)
            {
                if (_device != null) return;
                try
                {
                    _encoder = _encoder ?? OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                    _encoder.Bitrate = 24000;
                    IWaveIn device;
                    try
                    {
                        var wasapi = new WasapiCapture { ShareMode = AudioClientShareMode.Shared };
                        device = wasapi;
                        _format = wasapi.WaveFormat;
                    }
                    catch (Exception wasapiEx)
                    {
                        LogManager.RuntimeLog("voice: WASAPI capture unavailable (" + wasapiEx.Message + "); trying WinMM");
                        var winmm = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 20 };
                        device = winmm;
                        _format = winmm.WaveFormat;
                    }
                    device.DataAvailable += OnData;
                    device.RecordingStopped += (s, e) => { if (e.Exception != null) Fail("recording stopped: " + e.Exception.Message); };
                    device.StartRecording();
                    _device = device;
                    _resamplePosition = 0;
                    LogManager.RuntimeLog("voice: capture open, " + _format.SampleRate + " Hz, " + _format.Channels + " ch, " + _format.BitsPerSample + " bit " + _format.Encoding);
                }
                catch (Exception ex)
                {
                    Fail("capture could not start: " + ex.Message);
                }
            }
        }

        public static void Close()
        {
            lock (Lock)
            {
                _talking = false;
                var device = _device;
                _device = null;
                _pending.Clear();
                if (device == null) return;
                try { device.StopRecording(); device.Dispose(); } catch { /* ignored */ }
                LogManager.RuntimeLog("voice: capture closed after " + _framesSent + " frames");
            }
        }

        private static void Fail(string text)
        {
            _lastError = text;
            if (_errorsLogged++ < 3) LogManager.RuntimeLog("voice: " + text);
            var device = _device;
            _device = null;
            try { device?.Dispose(); } catch { /* ignored */ }
        }

        private static void OnData(object sender, WaveInEventArgs e)
        {
            if (!_talking || !Main.IsOnServer()) return;
            try
            {
                lock (Lock)
                {
                    if (_device == null) return;
                    AppendMono48k(e.Buffer, e.BytesRecorded);
                    while (_pending.Count >= FrameSamples)
                    {
                        var frame = _pending.GetRange(0, FrameSamples).ToArray();
                        _pending.RemoveRange(0, FrameSamples);
                        var length = _encoder.Encode(frame, FrameSamples, _encoded, _encoded.Length);
                        if (length <= 0) continue;
                        var msg = Main.Client.CreateMessage(5 + length);
                        msg.Write((byte)PacketType.Voice);
                        msg.Write(length);
                        msg.Write(_encoded, 0, length);
                        Main.Send(msg, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.Voice);
                        _framesSent++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_errorsLogged++ < 3) LogManager.RuntimeLog("voice: encode/send failed: " + ex.Message);
            }
        }

        /// <summary>Whatever the device delivers (float or 16-bit, 1..n channels, any rate) becomes 48 kHz 16-bit mono samples.</summary>
        private static void AppendMono48k(byte[] buffer, int bytes)
        {
            var channels = Math.Max(1, _format.Channels);
            var isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat || (_format.Encoding == WaveFormatEncoding.Extensible && _format.BitsPerSample == 32);
            var bytesPerSample = _format.BitsPerSample / 8;
            var frames = bytes / (bytesPerSample * channels);
            var mono = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var offset = (i * channels + c) * bytesPerSample;
                    if (isFloat) sum += BitConverter.ToSingle(buffer, offset);
                    else if (bytesPerSample == 2) sum += BitConverter.ToInt16(buffer, offset) / 32768f;
                    else if (bytesPerSample == 4) sum += BitConverter.ToInt32(buffer, offset) / 2147483648f;
                    else if (bytesPerSample == 3) sum += ((buffer[offset + 2] << 24) | (buffer[offset + 1] << 16) | (buffer[offset] << 8)) / 2147483648f;
                }
                mono[i] = sum / channels;
            }

            if (_format.SampleRate == SampleRate)
            {
                foreach (var s in mono) _pending.Add(ToShort(s));
                return;
            }
            // linear resampling to 48 kHz, continuous across buffers
            var step = (double)_format.SampleRate / SampleRate;
            var position = _resamplePosition;
            var previous = _lastSample;
            while (position < mono.Length)
            {
                var index = (int)Math.Floor(position);
                var fraction = position - index;
                var a = index - 1 >= 0 ? mono[index - 1] : previous;
                var b = mono[index];
                _pending.Add(ToShort((float)(a + (b - a) * fraction)));
                position += step;
            }
            _resamplePosition = position - mono.Length;
            if (mono.Length > 0) _lastSample = mono[mono.Length - 1];
        }

        private static short ToShort(float sample)
        {
            if (sample > 1f) sample = 1f; else if (sample < -1f) sample = -1f;
            return (short)(sample * 32767f);
        }
    }
}

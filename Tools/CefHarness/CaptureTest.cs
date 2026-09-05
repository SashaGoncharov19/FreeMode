using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GTANetwork.CefHarness
{
    /// <summary>
    /// T-016: does audio capture work in this Wine prefix? Opens the default capture device the way the client does (WASAPI
    /// shared, then WinMM), records for N seconds and reports the format, the bytes and the peak level. Exit code 0 when
    /// samples arrived and were not all silence, 1 otherwise.
    /// </summary>
    internal static class CaptureTest
    {
        // stdout of a process started through Proton's steam.exe wrapper is not visible: everything also goes to this file
        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "capture-test.log");

        private static void Say(string text)
        {
            Say(text);
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine); } catch { /* ignored */ }
        }

        public static int Run(int seconds)
        {
            try { File.Delete(LogPath); } catch { /* ignored */ }
            Say("capture test: " + seconds + " s");
            try
            {
                var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    Say("  capture device: " + device.FriendlyName);
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    Say("  render device: " + device.FriendlyName);
            }
            catch (Exception ex)
            {
                Say("  device enumeration failed: " + ex.Message);
            }

            var result = Record("WASAPI shared", () => new WasapiCapture { ShareMode = AudioClientShareMode.Shared }, seconds);
            if (result != 0) result = Record("WinMM", () => new WaveInEvent { WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 20 }, seconds);
            Say(result == 0 ? "capture test: OK" : "capture test: FAILED");
            return result;
        }

        /// <summary>Runs the recording on a worker so that a device call that never returns under Wine is reported as a hang, not waited for.</summary>
        private static int Record(string name, Func<IWaveIn> factory, int seconds)
        {
            var result = 1;
            var worker = new Thread(() => { result = RecordInner(name, factory, seconds); }) { IsBackground = true };
            worker.Start();
            if (!worker.Join((seconds + 20) * 1000)) { Say("  " + name + ": HANG - the device call did not return in " + (seconds + 20) + " s"); return 1; }
            return result;
        }

        private static int RecordInner(string name, Func<IWaveIn> factory, int seconds)
        {
            try
            {
                using (var device = factory())
                {
                    long bytes = 0; float peak = 0; var buffers = 0;
                    var format = device.WaveFormat;
                    var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
                    device.DataAvailable += (s, e) =>
                    {
                        bytes += e.BytesRecorded; buffers++;
                        var step = format.BitsPerSample / 8;
                        for (var i = 0; i + step <= e.BytesRecorded; i += step)
                        {
                            var v = isFloat ? Math.Abs(BitConverter.ToSingle(e.Buffer, i)) : step == 2 ? Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f) : 0f;
                            if (v > peak) peak = v;
                        }
                    };
                    Exception stopped = null;
                    device.RecordingStopped += (s, e) => stopped = e.Exception;
                    device.StartRecording();
                    Thread.Sleep(seconds * 1000);
                    device.StopRecording();
                    Thread.Sleep(200);
                    Say("  " + name + ": " + format.SampleRate + " Hz, " + format.Channels + " ch, " + format.BitsPerSample + " bit " + format.Encoding + "; " + buffers + " buffers, " + bytes + " bytes, peak " + peak.ToString("0.000") + (stopped != null ? "; stopped: " + stopped.Message : ""));
                    var expected = (long)format.AverageBytesPerSecond * seconds;
                    if (bytes < expected / 2) { Say("  " + name + ": too little data (" + bytes + " of ~" + expected + ")"); return 1; }
                    if (peak <= 0.0001f) Say("  " + name + ": all silence (muted microphone, or no input routed)");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Say("  " + name + ": failed: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}

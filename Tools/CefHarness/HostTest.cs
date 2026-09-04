using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GTANetworkShared.Cef;

namespace GTANetwork.CefHarness
{
    /// <summary>
    /// The acceptance test of the separate browser process, the same conversation the in-game client has with it:
    /// start GTANetwork.CefHost.exe, wait for "ready", create a local-mode browser, load https://harness/ui/index.html
    /// from a resources folder, read its pixels from the shared-memory frame buffer and wait for the page's
    /// resourceCall to arrive as a "jsMessage". Exit codes: 0 all good, 2 Chromium did not start, 3 no pixels,
    /// 4 exception, 5 the host died or hung, 7 pixels but no resourceCall from the page.
    /// </summary>
    internal static class HostTest
    {
        private const string TestPage =
            "<!doctype html><html><head><meta charset='utf-8'><title>gtan host harness</title><link rel='stylesheet' href='style.css'></head>" +
            "<body><h1>GTA Network browser host</h1><p id='t'>no script</p><script src='app.js'></script></body></html>";
        private const string TestCss = "body{margin:0;padding:20px;background:#205080;color:#fff;font:24px sans-serif}";
        private const string TestJs =
            "document.getElementById('t').textContent = 'JS ok ' + new Date().toISOString();" +
            "console.log('harness page script ran');" +
            "resourceCall('harnessPing', 1, 'two', {three: 3});" +
            "gtan.eval('1 + 1');";

        private static readonly object StateLock = new object();
        private static readonly ManualResetEvent Ready = new ManualResetEvent(false);
        private static readonly ManualResetEvent Created = new ManualResetEvent(false);
        private static readonly ManualResetEvent Loaded = new ManualResetEvent(false);
        private static readonly ManualResetEvent FrameAnnounced = new ManualResetEvent(false);
        private static readonly ManualResetEvent JsMessage = new ManualResetEvent(false);
        private static readonly ManualResetEvent Closed = new ManualResetEvent(false);
        private static readonly ManualResetEvent Exited = new ManualResetEvent(false);
        private static string _initFailure;
        private static string _frameName;
        private static int _frameW, _frameH, _frameStride;
        private static string _jsCall;
        private static long _textureEvents;
        private static long _textureHandle;
        private static int _textureW, _textureH;
        private static readonly ManualResetEvent TextureAnnounced = new ManualResetEvent(false);
        private static string _jsEval;
        private static int _events;

        public static int Run(string hostExe, string logDir, int timeoutSec, bool gpu, bool inProcessGpu, bool verbose, string url, int holdSec, int benchSec, int benchW, int benchH, bool sharedTexture)
        {
            var log = Program.Log;
            if (!File.Exists(hostExe))
            {
                log("RESULT: host not found: " + hostExe + " (exit code 2)");
                return 2;
            }

            // A resource folder like <install>\resources: resources\harness\ui\{index.html,style.css,app.js}
            var resourceRoot = Path.Combine(logDir, "resources");
            var ui = Path.Combine(resourceRoot, "harness", "ui");
            Directory.CreateDirectory(ui);
            File.WriteAllText(Path.Combine(ui, "index.html"), TestPage, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ui, "style.css"), TestCss, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ui, "app.js"), TestJs, new UTF8Encoding(false));
            var pageUrl = url ?? "https://harness/ui/index.html";

            var args = new List<string>
            {
                "--parent", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                "--log", Path.Combine(logDir, "harness-host.log"),
                "--chromium-log", Path.Combine(logDir, "harness-chromium.log"),
                "--cache", Path.Combine(logDir, "cache"),
                "--resource-root", resourceRoot,
            };
            if (gpu) args.Add("--gpu");
            if (!inProcessGpu) args.Add("--gpu-process");
            if (verbose) args.Add("--verbose");

            var psi = new ProcessStartInfo(hostExe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostExe),
            };
            foreach (var a in args) psi.ArgumentList_Add(a);
            psi.Environment["WINEDEBUG"] = Environment.GetEnvironmentVariable("GTAN_CEF_WINEDEBUG") ?? "-all"; // as CEFManager does

            log("--> starting " + hostExe + " " + string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)));
            var clock = Stopwatch.StartNew();
            var host = Process.Start(psi);
            if (host == null)
            {
                log("RESULT: Process.Start returned null (exit code 5)");
                return 5;
            }
            host.EnableRaisingEvents = true;
            host.Exited += (s, e) => { log("host exited with code " + SafeExitCode(host) + " after " + clock.ElapsedMilliseconds + " ms"); Exited.Set(); };
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = host.StandardError.ReadLine()) != null) log("host stderr: " + line);
                }
                catch { }
            }) { IsBackground = true }.Start();

            var channel = new CefHostChannel(host.StandardOutput.BaseStream, host.StandardInput.BaseStream);
            var reader = new Thread(() => ReadEvents(channel, verbose)) { IsBackground = true, Name = "host events" };
            reader.Start();

            var deadline = TimeSpan.FromSeconds(timeoutSec);
            string outcome;
            var exit = 4;
            try
            {
                if (WaitHandle.WaitAny(new WaitHandle[] { Ready, Exited }, deadline) != 0 || _initFailure != null)
                {
                    outcome = _initFailure != null ? "Chromium did not start: " + _initFailure : Exited.WaitOne(0) ? "the host exited before it was ready" : "no 'ready' within " + timeoutSec + " s";
                    exit = _initFailure != null || Exited.WaitOne(0) ? 2 : 5;
                    return Finish(channel, host, outcome, exit, clock);
                }
                log("host ready after " + clock.ElapsedMilliseconds + " ms");

                channel.Send(new CefHostMessage(CefHostProtocol.Create, 1) { W = 420, H = 480, Local = true, Fps = 30, Shared = sharedTexture });
                if (WaitHandle.WaitAny(new WaitHandle[] { Created, Exited }, deadline) != 0)
                    return Finish(channel, host, "no 'created' for the browser", 5, clock);
                log("browser created after " + clock.ElapsedMilliseconds + " ms");
                channel.Send(new CefHostMessage(CefHostProtocol.Focus, 1) { On = true }); // as CefController does in game

                channel.Send(new CefHostMessage(CefHostProtocol.Load, 1) { Url = pageUrl });

                if (sharedTexture)
                {
                    // The zero-copy path: Chromium's frames arrive as D3D11 shared textures; open them on our own device
                    // (DXVK under Proton, like the game's) and read pixels back once to prove the content is there.
                    if (WaitHandle.WaitAny(new WaitHandle[] { TextureAnnounced, FrameAnnounced, Exited }, deadline) != 0)
                        return Finish(channel, host, TextureAnnounced.WaitOne(0) ? "?" : FrameAnnounced.WaitOne(0) ? "Chromium fell back to CPU frames (no shared textures; is the GPU on?)" : "no texture announced", 3, clock);
                    var verdict = SharedTextureCheck(deadline);
                    if (verdict.StartsWith("OK")) log(verdict);
                    else return Finish(channel, host, verdict, 3, clock);
                    if (benchSec > 0)
                    {
                        channel.Send(new CefHostMessage(CefHostProtocol.Close, 1));
                        WaitHandle.WaitAny(new WaitHandle[] { Closed, Exited }, TimeSpan.FromSeconds(10));
                        return Finish(channel, host, Benchmark(channel, host, benchW, benchH, benchSec, deadline, true), 0, clock);
                    }
                    channel.Send(new CefHostMessage(CefHostProtocol.Close, 1));
                    WaitHandle.WaitAny(new WaitHandle[] { Closed, Exited }, TimeSpan.FromSeconds(10));
                    return Finish(channel, host, verdict, 0, clock);
                }

                if (WaitHandle.WaitAny(new WaitHandle[] { FrameAnnounced, Exited }, deadline) != 0)
                    return Finish(channel, host, "no frame buffer announced (page " + (Loaded.WaitOne(0) ? "loaded" : "not loaded") + ")", 3, clock);

                string frameName; int w, h, stride;
                lock (StateLock) { frameName = _frameName; w = _frameW; h = _frameH; stride = _frameStride; }
                log("frame buffer " + frameName + " " + w + "x" + h + " stride " + stride);

                if (benchSec > 0)
                {
                    // Benchmark: an animated page in a big browser; how many frames reach shared memory per second,
                    // what a copy costs on our side, and what the host and its Chromium processes burn.
                    channel.Send(new CefHostMessage(CefHostProtocol.Close, 1));
                    WaitHandle.WaitAny(new WaitHandle[] { Closed, Exited }, TimeSpan.FromSeconds(10));
                    return Finish(channel, host, Benchmark(channel, host, benchW, benchH, benchSec, deadline, false), 0, clock);
                }

                var pixels = WaitForPixels(frameName, w, h, deadline, out var opaque, out var frames);
                if (!pixels) return Finish(channel, host, "frame buffer never had visible pixels (page " + (Loaded.WaitOne(0) ? "loaded" : "not loaded") + ")", 3, clock);
                log("pixels after " + clock.ElapsedMilliseconds + " ms: " + opaque + " opaque of " + (w * h) + ", " + frames + " frame(s) read");

                // input goes through the same pipe: a mouse move and a click must not upset anything
                channel.Send(new CefHostMessage(CefHostProtocol.MouseMove, 1) { X = 50, Y = 50 });
                channel.Send(new CefHostMessage(CefHostProtocol.MouseClick, 1) { X = 50, Y = 50, Button = 0, Clicks = 1 });
                channel.Send(new CefHostMessage(CefHostProtocol.MouseClick, 1) { X = 50, Y = 50, Button = 0, On = true, Clicks = 1 });
                channel.Send(new CefHostMessage(CefHostProtocol.Eval, 1) { Code = "document.body.style.background='#802020'; resourceCall('harnessEval', document.title);" });

                var gotJs = WaitHandle.WaitAny(new WaitHandle[] { JsMessage, Exited }, TimeSpan.FromSeconds(10)) == 0;
                string call, evalCode;
                lock (StateLock) { call = _jsCall; evalCode = _jsEval; }
                if (!gotJs) return Finish(channel, host, "pixels OK, but no resourceCall/resourceEval from the page within 10 s", 7, clock);
                log("page -> game: resourceCall " + call + (evalCode != null ? "; resourceEval " + evalCode : ""));

                if (holdSec > 0)
                {
                    log("holding the browser open for " + holdSec + " s (--hold)");
                    for (var i = 0; i < holdSec * 10 && !Exited.WaitOne(0); i++) { Thread.Sleep(100); channel.Send(new CefHostMessage(CefHostProtocol.MouseMove, 1) { X = 60 + i % 50, Y = 60 }); }
                }

                // resize: a new frame buffer must be announced
                FrameAnnounced.Reset();
                channel.Send(new CefHostMessage(CefHostProtocol.Resize, 1) { W = 300, H = 200 });
                if (WaitHandle.WaitAny(new WaitHandle[] { FrameAnnounced, Exited }, TimeSpan.FromSeconds(10)) == 0)
                {
                    lock (StateLock) log("resized: frame buffer " + _frameName + " " + _frameW + "x" + _frameH);
                }
                else log("WARNING: no new frame buffer within 10 s of the resize");

                channel.Send(new CefHostMessage(CefHostProtocol.Close, 1));
                WaitHandle.WaitAny(new WaitHandle[] { Closed, Exited }, TimeSpan.FromSeconds(10));

                return Finish(channel, host, "OK: ready, browser, local page, pixels, resourceCall, resize, close (" + _events + " events)", 0, clock);
            }
            catch (Exception ex)
            {
                log("EXCEPTION: " + ex);
                return Finish(channel, host, "exception: " + ex.GetType().Name + ": " + ex.Message, 4, clock);
            }
        }

        private const string BenchPage =
            "<!doctype html><html><head><meta charset='utf-8'><title>gtan bench</title><style>" +
            "body{margin:0;overflow:hidden;background:linear-gradient(270deg,#1b2735,#090a0f,#2b5876,#4e4376);background-size:800% 800%;animation:g 6s ease infinite;color:#fff;font:20px sans-serif}" +
            "@keyframes g{0%{background-position:0% 50%}50%{background-position:100% 50%}100%{background-position:0% 50%}}" +
            ".box{position:absolute;width:120px;height:120px;border-radius:16px;background:rgba(255,255,255,.85);animation:m 3s linear infinite}" +
            "@keyframes m{from{transform:translate(0,0) rotate(0)}to{transform:translate(400px,200px) rotate(360deg)}}" +
            "#hud{position:absolute;left:16px;top:16px;background:rgba(0,0,0,.5);padding:8px 12px;border-radius:8px}" +
            "canvas{position:absolute;right:0;bottom:0}</style></head><body>" +
            "<div class='box' style='left:40px;top:60px'></div><div class='box' style='left:300px;top:260px;animation-delay:-1.5s'></div>" +
            "<div id='hud'>frame <span id='n'>0</span></div><canvas id='c' width='480' height='320'></canvas>" +
            "<script>var n=0,c=document.getElementById('c').getContext('2d');function f(t){n++;document.getElementById('n').textContent=n;" +
            "c.fillStyle='rgba(0,0,0,0.2)';c.fillRect(0,0,480,320);for(var i=0;i<24;i++){c.fillStyle='hsl('+((t/10+i*15)%360)+',80%,60%)';c.beginPath();" +
            "c.arc(240+Math.cos(t/500+i)*180,160+Math.sin(t/400+i)*120,18,0,6.283);c.fill();}requestAnimationFrame(f);}requestAnimationFrame(f);</script></body></html>";

        private static string Benchmark(CefHostChannel channel, Process host, int w, int h, int seconds, TimeSpan deadline, bool sharedTexture)
        {
            var log = Program.Log;
            const int id = 2;
            FrameAnnounced.Reset();
            TextureAnnounced.Reset();
            Created.Reset();
            channel.Send(new CefHostMessage(CefHostProtocol.Create, id) { W = w, H = h, Local = false, Fps = 60, Shared = sharedTexture });
            if (WaitHandle.WaitAny(new WaitHandle[] { Created, Exited }, deadline) != 0) return "bench: no 'created'";
            channel.Send(new CefHostMessage(CefHostProtocol.Load, id) { Url = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(BenchPage)) });
            if (sharedTexture)
            {
                if (WaitHandle.WaitAny(new WaitHandle[] { TextureAnnounced, Exited }, deadline) != 0) return "bench: no shared texture announced";
                var cpu0 = ProcessorTimes(host);
                var clock0 = Stopwatch.StartNew();
                var events0 = Interlocked.Read(ref _textureEvents);
                using (var reader = new SharedTextureReader())
                {
                    long copies = 0, gpuTicks = 0, lastHandle = 0;
                    var opened = 0;
                    while (clock0.Elapsed.TotalSeconds < seconds)
                    {
                        var handle = Interlocked.Read(ref _textureHandle);
                        var evts = Interlocked.Read(ref _textureEvents);
                        if (handle != 0 && evts != copies + events0)
                        {
                            var t0 = Stopwatch.GetTimestamp();
                            if (reader.CopyFrom(new IntPtr(handle), out var freshlyOpened)) { copies++; gpuTicks += Stopwatch.GetTimestamp() - t0; if (freshlyOpened) opened++; }
                            lastHandle = handle;
                        }
                        else Thread.Sleep(1);
                    }
                    var el = clock0.Elapsed.TotalSeconds;
                    var cpu1 = ProcessorTimes(host);
                    var stResult = "bench " + w + "x" + h + " shared textures: " + ((Interlocked.Read(ref _textureEvents) - events0) / el).ToString("0.0", CultureInfo.InvariantCulture) + " texture events/s, " +
                                 (copies / el).ToString("0.0", CultureInfo.InvariantCulture) + " GPU copies/s (" + (copies > 0 ? gpuTicks * 1000.0 / Stopwatch.Frequency / copies : 0).ToString("0.000", CultureInfo.InvariantCulture) +
                                 " ms per CopyResource, " + opened + " texture(s) in Chromium's pool); CPU: host " + Percent(cpu1.Item1 - cpu0.Item1, el) + ", Chromium subprocesses " + Percent(cpu1.Item2 - cpu0.Item2, el) + ", this harness " + Percent(cpu1.Item3 - cpu0.Item3, el);
                    log(stResult);
                    channel.Send(new CefHostMessage(CefHostProtocol.Close, id));
                    return stResult;
                }
            }
            if (WaitHandle.WaitAny(new WaitHandle[] { FrameAnnounced, Exited }, deadline) != 0) return "bench: no frame buffer";

            string frameName;
            lock (StateLock) frameName = _frameName;
            var cpuBefore = ProcessorTimes(host);
            var clock = Stopwatch.StartNew();
            long frames = 0, partial = 0, copiedBytes = 0;
            var copyTicks = 0L;
            var maxGap = 0.0;
            var lastFrameAt = 0.0;
            using (var frame = CefFrameBuffer.Open(frameName))
            {
                var buffer = Marshal.AllocHGlobal(frame.Stride * frame.Height);
                try
                {
                    long last = -1;
                    var had = false;
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        int dx, dy, dw, dh;
                        var t0 = Stopwatch.GetTimestamp();
                        if (frame.TryCopyTo(buffer, frame.Stride, ref last, had, out dx, out dy, out dw, out dh))
                        {
                            copyTicks += Stopwatch.GetTimestamp() - t0;
                            had = true;
                            frames++;
                            copiedBytes += (long)dw * dh * 4;
                            if (dw < frame.Width || dh < frame.Height) partial++;
                            var now = clock.Elapsed.TotalMilliseconds;
                            if (lastFrameAt > 0) maxGap = Math.Max(maxGap, now - lastFrameAt);
                            lastFrameAt = now;
                        }
                        else Thread.Sleep(1);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            var elapsed = clock.Elapsed.TotalSeconds;
            var cpuAfter = ProcessorTimes(host);
            var fps = frames / elapsed;
            var copyMs = frames > 0 ? copyTicks * 1000.0 / Stopwatch.Frequency / frames : 0;
            var result = "bench " + w + "x" + h + ": " + fps.ToString("0.0", CultureInfo.InvariantCulture) + " frames/s delivered (" + frames + " in " +
                         elapsed.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + partial + " partial, " + (copiedBytes / 1048576.0 / elapsed).ToString("0.0", CultureInfo.InvariantCulture) +
                         " MB/s copied, " + copyMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms per copy, longest gap " + maxGap.ToString("0", CultureInfo.InvariantCulture) + " ms); CPU: host " +
                         Percent(cpuAfter.Item1 - cpuBefore.Item1, elapsed) + ", Chromium subprocesses " + Percent(cpuAfter.Item2 - cpuBefore.Item2, elapsed) + ", this harness " + Percent(cpuAfter.Item3 - cpuBefore.Item3, elapsed);
            log(result);
            channel.Send(new CefHostMessage(CefHostProtocol.Close, id));
            return result;
        }

        private static string Percent(TimeSpan cpu, double seconds)
        {
            return (cpu.TotalSeconds / seconds * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        /// <summary>CPU time of the host, of every CefSharp.BrowserSubprocess and of this process.</summary>
        private static Tuple<TimeSpan, TimeSpan, TimeSpan> ProcessorTimes(Process host)
        {
            TimeSpan h = TimeSpan.Zero, sub = TimeSpan.Zero, self = TimeSpan.Zero;
            try { host.Refresh(); h = host.TotalProcessorTime; } catch { }
            try
            {
                foreach (var p in Process.GetProcessesByName("CefSharp.BrowserSubprocess"))
                {
                    try { sub += p.TotalProcessorTime; } catch { }
                    p.Dispose();
                }
            }
            catch { }
            try { self = Process.GetCurrentProcess().TotalProcessorTime; } catch { }
            return Tuple.Create(h, sub, self);
        }

        private static int Finish(CefHostChannel channel, Process host, string outcome, int exit, Stopwatch clock)
        {
            try { channel.Send(new CefHostMessage(CefHostProtocol.Shutdown)); } catch { }
            if (!Exited.WaitOne(TimeSpan.FromSeconds(15)))
            {
                Program.Log("the host did not exit within 15 s of 'shutdown'; killing it");
                try { host.Kill(); } catch { }
                if (exit == 0) { outcome += " (but the host had to be killed)"; exit = 5; }
            }
            Program.Log("RESULT: " + outcome + " (exit code " + exit + ", " + clock.ElapsedMilliseconds + " ms)");
            return exit;
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        private static void ReadEvents(CefHostChannel channel, bool verbose)
        {
            try
            {
                CefHostMessage m;
                while ((m = channel.Receive()) != null)
                {
                    Interlocked.Increment(ref _events);
                    switch (m.Type)
                    {
                        case CefHostProtocol.Ready:
                            Program.Log("event ready: Chromium " + m.Chromium + ", CEF " + m.CefVersion + ", CefSharp " + m.CefSharp + " (" + m.Text + ")");
                            Ready.Set();
                            break;
                        case CefHostProtocol.InitFailed:
                            Program.Log("event initFailed: " + m.Text);
                            _initFailure = m.Text;
                            Ready.Set();
                            break;
                        case CefHostProtocol.Created:
                            Program.Log("event created #" + m.Id);
                            Created.Set();
                            break;
                        case CefHostProtocol.Texture:
                        {
                            var n = Interlocked.Increment(ref _textureEvents);
                            Interlocked.Exchange(ref _textureHandle, m.Handle);
                            lock (StateLock) { _textureW = m.W; _textureH = m.H; }
                            if (n <= 3 || (verbose && n % 60 == 0)) Program.Log("event texture #" + m.Id + ": handle 0x" + m.Handle.ToString("X") + " " + m.W + "x" + m.H + " dirty " + m.Dx + "x" + m.Dy + " at " + m.X + "," + m.Y + " (pool " + m.Gen + ")");
                            TextureAnnounced.Set();
                            break;
                        }
                        case CefHostProtocol.Frame:
                            Program.Log("event frame #" + m.Id + ": " + m.FrameName + " " + m.W + "x" + m.H + " stride " + m.Stride + " gen " + m.Gen);
                            lock (StateLock) { _frameName = m.FrameName; _frameW = m.W; _frameH = m.H; _frameStride = m.Stride; }
                            FrameAnnounced.Set();
                            break;
                        case CefHostProtocol.LoadEnd:
                            Program.Log("event loadEnd #" + m.Id + ": HTTP " + m.Status + " " + m.Url);
                            Loaded.Set();
                            break;
                        case CefHostProtocol.LoadError:
                            Program.Log("event loadError #" + m.Id + ": " + m.Status + " " + m.Text + " " + m.Url);
                            break;
                        case CefHostProtocol.JsMessage:
                            if (m.Name != null)
                            {
                                var text = m.Name + "(" + string.Join(", ", (m.Args ?? new object[0]).Select(a => a == null ? "null" : a.ToString().Replace("\r", "").Replace("\n", " "))) + ")";
                                Program.Log("event jsMessage #" + m.Id + ": resourceCall " + text);
                                lock (StateLock) _jsCall = _jsCall == null ? text : _jsCall + " | " + text;
                            }
                            else
                            {
                                Program.Log("event jsMessage #" + m.Id + ": resourceEval " + m.Code);
                                lock (StateLock) _jsEval = m.Code;
                            }
                            JsMessage.Set();
                            break;
                        case CefHostProtocol.Console:
                            Program.Log("event console #" + m.Id + " [" + m.Level + "] " + m.Text + " (" + m.Source + ":" + m.Line + ")");
                            break;
                        case CefHostProtocol.Log:
                            Program.Log("event log: " + m.Text);
                            break;
                        case CefHostProtocol.Closed:
                            Program.Log("event closed #" + m.Id);
                            Closed.Set();
                            break;
                        default:
                            if (verbose || (m.Type != CefHostProtocol.Loading && m.Type != CefHostProtocol.LoadStart)) Program.Log("event " + m);
                            break;
                    }
                }
                Program.Log("host closed its stdout");
            }
            catch (Exception ex)
            {
                Program.Log("event reader stopped: " + ex.Message);
            }
        }

        /// <summary>Open the current shared texture on our own D3D11 device, copy it, read it back and count opaque pixels.</summary>
        private static string SharedTextureCheck(TimeSpan deadline)
        {
            try
            {
                using (var reader = new SharedTextureReader())
                {
                    var clock = Stopwatch.StartNew();
                    var attempts = 0;
                    while (clock.Elapsed < deadline)
                    {
                        var handle = Interlocked.Read(ref _textureHandle);
                        if (handle == 0) { Thread.Sleep(20); continue; }
                        attempts++;
                        int opaque, w, h;
                        var text = reader.ReadBack(new IntPtr(handle), out opaque, out w, out h);
                        if (text != null) return "shared texture open failed: " + text;
                        Program.Log("shared texture 0x" + handle.ToString("X") + ": " + w + "x" + h + ", " + opaque + " opaque pixels (read-back " + attempts + ")");
                        if (opaque > w * h / 2)
                            return "OK: shared D3D11 texture " + w + "x" + h + " opened cross-process and read back, " + opaque + " opaque pixels, " + Interlocked.Read(ref _textureEvents) + " texture event(s)";
                        Thread.Sleep(100);
                    }
                    return "shared texture never showed the page (" + attempts + " read-backs)";
                }
            }
            catch (Exception ex)
            {
                return "shared texture check failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static bool WaitForPixels(string frameName, int w, int h, TimeSpan deadline, out int opaque, out int frames)
        {
            opaque = 0;
            frames = 0;
            var clock = Stopwatch.StartNew();
            using (var frame = CefFrameBuffer.Open(frameName))
            {
                Program.Log("opened frame buffer " + frame.Name + " " + frame.Width + "x" + frame.Height + " stride " + frame.Stride + ", sequence " + frame.Sequence);
                var buffer = new byte[frame.Stride * frame.Height];
                long last = -1;
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    while (clock.Elapsed < deadline)
                    {
                        int dx, dy, dw, dh;
                        if (frame.TryCopyTo(handle.AddrOfPinnedObject(), frame.Stride, ref last, out dx, out dy, out dw, out dh))
                        {
                            frames++;
                            opaque = 0;
                            for (var i = 3; i < buffer.Length; i += 4) if (buffer[i] != 0) opaque++;
                            if (frames <= 3) Program.Log("frame " + frames + " (sequence " + last + "): " + opaque + " opaque pixels, dirty " + dw + "x" + dh + " at " + dx + "," + dy);
                            // the test page paints a solid background, so nearly every pixel must be opaque
                            if (opaque > w * h / 2) return true;
                        }
                        Thread.Sleep(15);
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
            return false;
        }
    }

    internal static class ProcessStartInfoExtensions
    {
        /// <summary>.NET Framework has no ArgumentList; quote the way CommandLineToArgvW un-quotes.</summary>
        public static void ArgumentList_Add(this ProcessStartInfo psi, string argument)
        {
            var needsQuotes = argument.Length == 0 || argument.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
            var escaped = argument.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"");
            psi.Arguments = (psi.Arguments.Length > 0 ? psi.Arguments + " " : "") + (needsQuotes ? "\"" + escaped + "\"" : escaped);
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GTANetwork.Launcher;

/// <summary>
/// --debug on Linux: one line per second about the things that freeze a game from outside the game — swap traffic,
/// memory-pressure stalls, page faults of GTA5.exe, Chromium's footprint, CPU and GPU clocks and temperatures — into
/// logs/hitch-monitor.log. A hitch the in-game client logs ([HITCH] in logs/Runtime.log, with the millisecond) can
/// then be matched with what the machine was doing in that second: a burst of swap-ins or a memory stall is the
/// machine, a GPU clock drop is thermals, and if neither moved while our overlay took 0.1 ms, it was the game.
/// </summary>
internal sealed class HitchMonitor : IDisposable
{
    private readonly string _logPath;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private bool _nvidiaSmi;

    private HitchMonitor(string logPath)
    {
        _logPath = logPath;
        _nvidiaSmi = Environment.GetEnvironmentVariable("PATH")?.Split(':').Any(d => File.Exists(Path.Combine(d, "nvidia-smi"))) == true;
        _thread = new Thread(Run) { IsBackground = true, Name = "hitch monitor" };
    }

    public static HitchMonitor? Start(string logPath)
    {
        try
        {
            var m = new HitchMonitor(logPath);
            m._thread.Start();
            Log.Info("Hitch monitor: one line per second (swap, memory stalls, GTA5 page faults, Chromium memory, CPU/GPU clocks) in " + logPath);
            return m;
        }
        catch (Exception ex)
        {
            Log.Warn("Hitch monitor not started: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(3000);
    }

    private void Run()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            using var w = new StreamWriter(_logPath, append: true) { AutoFlush = true };
            w.WriteLine($"==== hitch monitor {DateTime.Now:yyyy-MM-dd HH:mm:ss}: swap in/out per second, memory-pressure stall (ms the whole machine waited for memory), " +
                        "MemAvailable, GTA5.exe RSS/swapped/major faults per second, Chromium (browser host + subprocesses) RSS, CPU busy/MHz/°C, GPU MHz/°C/busy/event reasons ====");

            var vm = ReadVmstat();
            var psi = ReadPsiFullTotal();
            var cpu = ReadCpuStat();
            long gtaMajflt = -1;
            var nvidiaFailures = 0;

            while (!_stop.WaitOne(1000))
            {
                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ");

                var vm2 = ReadVmstat();
                sb.Append("swap in ").Append((vm2.swapIn - vm.swapIn) * 4).Append(" KB/s out ").Append((vm2.swapOut - vm.swapOut) * 4).Append(" KB/s");
                vm = vm2;

                var psi2 = ReadPsiFullTotal();
                sb.Append(" | mem stall ").Append((psi2 - psi) / 1000).Append(" ms");
                psi = psi2;
                sb.Append(" avail ").Append(ReadMemInfo("MemAvailable") / 1024).Append(" MB");

                var gta = GameProcess.Find("GTA5.exe").FirstOrDefault();
                if (gta > 0)
                {
                    var status = ReadStatus(gta);
                    var majflt = ReadMajorFaults(gta);
                    sb.Append(" | GTA5 rss ").Append(status.rss / 1024).Append(" MB swapped ").Append(status.swap / 1024).Append(" MB majflt +")
                      .Append(gtaMajflt >= 0 && majflt >= gtaMajflt ? majflt - gtaMajflt : 0);
                    gtaMajflt = majflt;
                }
                else sb.Append(" | GTA5 not running");

                sb.Append(" | chromium rss ").Append(ChromiumRssKb() / 1024).Append(" MB");

                var cpu2 = ReadCpuStat();
                var total = cpu2.total - cpu.total;
                var busy = total > 0 ? 100 * (total - (cpu2.idle - cpu.idle)) / total : 0;
                cpu = cpu2;
                sb.Append(" | cpu ").Append(busy).Append("% ").Append(ReadCpuMhz()).Append(" MHz");
                var temp = ReadCpuTemp();
                if (temp > 0) sb.Append(' ').Append(temp).Append("°C");

                if (_nvidiaSmi)
                {
                    var gpu = QueryNvidia();
                    if (gpu != null) sb.Append(" | gpu ").Append(gpu);
                    else if (++nvidiaFailures >= 3) _nvidiaSmi = false;
                }

                w.WriteLine(sb.ToString());
            }
            w.WriteLine("==== hitch monitor stopped ====");
        }
        catch (Exception ex)
        {
            Log.Warn("Hitch monitor: " + ex.Message);
        }
    }

    private static (long swapIn, long swapOut) ReadVmstat()
    {
        long si = 0, so = 0;
        foreach (var line in SafeLines("/proc/vmstat"))
        {
            if (line.StartsWith("pswpin ", StringComparison.Ordinal)) si = long.Parse(line.AsSpan(7), CultureInfo.InvariantCulture);
            else if (line.StartsWith("pswpout ", StringComparison.Ordinal)) so = long.Parse(line.AsSpan(8), CultureInfo.InvariantCulture);
        }
        return (si, so);
    }

    /// <summary>Microseconds every task on the machine spent stalled on memory (the "full" line of /proc/pressure/memory).</summary>
    private static long ReadPsiFullTotal()
    {
        foreach (var line in SafeLines("/proc/pressure/memory"))
        {
            if (!line.StartsWith("full ", StringComparison.Ordinal)) continue;
            var i = line.IndexOf("total=", StringComparison.Ordinal);
            if (i >= 0 && long.TryParse(line.AsSpan(i + 6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        }
        return 0;
    }

    private static long ReadMemInfo(string key)
    {
        foreach (var line in SafeLines("/proc/meminfo"))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && long.TryParse(parts[1], out var kb) ? kb : 0;
        }
        return 0;
    }

    private static (long rss, long swap) ReadStatus(int pid)
    {
        long rss = 0, swap = 0;
        foreach (var line in SafeLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = FirstNumber(line.AsSpan(6));
            else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swap = FirstNumber(line.AsSpan(7));
        }
        return (rss, swap);
    }

    private static long ReadMajorFaults(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // the command name is in parentheses and may contain spaces; fields start after the closing one
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // rest[0] = state (field 3); majflt is field 12 -> rest[9]
            return rest.Length > 9 && long.TryParse(rest[9], out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long ChromiumRssKb()
    {
        long total = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out var pid)) continue;
                string cmdline;
                try { cmdline = File.ReadAllText(dir + "/cmdline"); } catch { continue; }
                if (!cmdline.Contains("GTANetwork.CefHost.exe", StringComparison.Ordinal) && !cmdline.Contains("CefSharp.BrowserSubprocess.exe", StringComparison.Ordinal)) continue;
                total += ReadStatus(pid).rss;
            }
        }
        catch
        {
        }
        return total;
    }

    private static (long total, long idle) ReadCpuStat()
    {
        foreach (var line in SafeLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long total = 0;
            for (var i = 1; i < f.Length && i <= 8; i++) total += long.Parse(f[i], CultureInfo.InvariantCulture);
            var idle = long.Parse(f[4], CultureInfo.InvariantCulture) + (f.Length > 5 ? long.Parse(f[5], CultureInfo.InvariantCulture) : 0);
            return (total, idle);
        }
        return (0, 0);
    }

    private static int ReadCpuMhz()
    {
        double sum = 0;
        var n = 0;
        foreach (var line in SafeLines("/proc/cpuinfo"))
        {
            if (!line.StartsWith("cpu MHz", StringComparison.Ordinal)) continue;
            var i = line.IndexOf(':');
            if (i > 0 && double.TryParse(line.AsSpan(i + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)) { sum += mhz; n++; }
        }
        return n > 0 ? (int)(sum / n) : 0;
    }

    private static int ReadCpuTemp()
    {
        try
        {
            foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                var type = File.ReadAllText(zone + "/type").Trim();
                if (type != "x86_pkg_temp" && type != "k10temp" && type != "cpu-thermal") continue;
                return int.Parse(File.ReadAllText(zone + "/temp").Trim(), CultureInfo.InvariantCulture) / 1000;
            }
        }
        catch
        {
        }
        return 0;
    }

    private static string? QueryNvidia()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--query-gpu=clocks.sm,temperature.gpu,utilization.gpu,clocks_event_reasons.active");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000) || p.ExitCode != 0) return null;
            var f = output.Trim().Split(',', StringSplitOptions.TrimEntries);
            return f.Length >= 4 ? $"{f[0]} MHz {f[1]}°C {f[2]}% events {f[3]}" : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static long FirstNumber(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        var end = 0;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        return end > 0 ? long.Parse(s[..end], CultureInfo.InvariantCulture) : 0;
    }

    private static IEnumerable<string> SafeLines(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }
        foreach (var l in lines) yield return l;
    }
}

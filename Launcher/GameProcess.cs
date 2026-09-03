using System.Diagnostics;

namespace GTANetwork.Launcher;

/// <summary>Finds the (Wine) game process by executable name, e.g. "GTA5.exe".</summary>
internal static class GameProcess
{
    public static bool IsRunning(string exeName) => Find(exeName).Count > 0;

    public static List<int> Find(string exeName)
    {
        var result = new List<int>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                result.Add(p.Id);
                p.Dispose();
            }
            return result;
        }

        if (!Directory.Exists("/proc")) return result;

        // /proc/<pid>/comm holds the executable name (truncated to 15 chars); Wine keeps the Windows name.
        var comm15 = exeName.Length > 15 ? exeName.Substring(0, 15) : exeName;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;

            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                if (string.Equals(comm, comm15, StringComparison.OrdinalIgnoreCase) || string.Equals(comm, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(pid);
                    continue;
                }

                var cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                var first = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first == null) continue;
                var exe = first.Substring(Math.Max(first.LastIndexOf('/'), first.LastIndexOf('\\')) + 1);
                if (string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase)) result.Add(pid);
            }
            catch
            {
                // process went away or is not ours
            }
        }

        return result;
    }

    public static bool WaitForStart(string exeName, TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            if (IsRunning(exeName)) return true;
            Thread.Sleep(1000);
        }
        return IsRunning(exeName);
    }

    public static void WaitForExit(string exeName, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && IsRunning(exeName))
        {
            Thread.Sleep(2000);
        }
    }
}

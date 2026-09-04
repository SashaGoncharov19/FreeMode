using System;
using GTANetworkServer.Constant;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GTANetworkServer.Runtime
{
    /// <summary>
    /// Starts and supervises the Bun process that hosts the TypeScript server resources (runtime/main.ts). Bun is found through
    /// GTAN_BUN, then runtime/bun/bun(.exe) next to the server, then PATH. Restarts are decided by the bridge (back-off 1, 2, 5 s,
    /// five per minute at most).
    /// </summary>
    internal sealed class RuntimeProcess : IDisposable
    {
        private Process _process;
        private readonly string _runtimeDir;

        public RuntimeProcess(string runtimeDir)
        {
            _runtimeDir = runtimeDir;
        }

        public int Pid => _process?.Id ?? 0;
        public bool IsRunning { get { try { return _process != null && !_process.HasExited; } catch { return false; } } }

        /// <summary>The bun executable, or null with the reason.</summary>
        public static string FindBun(out string error)
        {
            error = null;
            var env = Environment.GetEnvironmentVariable("GTAN_BUN");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bun.exe" : "bun";
            var local = Path.Combine(AppContext.BaseDirectory, "runtime", "bun", exe);
            if (File.Exists(local)) return local;
            var localCwd = Path.Combine("runtime", "bun", exe);
            if (File.Exists(localCwd)) return Path.GetFullPath(localCwd);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            error = "bun not found: set GTAN_BUN, put it in runtime/bun/, or install it (https://bun.sh); TypeScript server resources need it";
            return null;
        }

        /// <summary>Starts <c>bun run main.ts</c> in the runtime folder with the socket address; stdout/stderr go to the server log.</summary>
        public void Start(string bun, string socketArg, string token)
        {
            var main = Path.Combine(_runtimeDir, "main.ts");
            if (!File.Exists(main)) throw new FileNotFoundException("runtime/main.ts not found next to the server", main);
            var psi = new ProcessStartInfo(bun)
            {
                WorkingDirectory = _runtimeDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(main);
            psi.ArgumentList.Add("--socket");
            psi.ArgumentList.Add(socketArg);
            psi.Environment["GTAN_SERVER_PID"] = Environment.ProcessId.ToString();
            psi.Environment["GTAN_RUNTIME_TOKEN"] = token;
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for " + bun);
            _process.OutputDataReceived += (s, e) => { if (e.Data != null) Program.Output("[runtime] " + e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Program.Output("[runtime] " + e.Data, LogCat.Warn); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public void Dispose()
        {
            var p = _process;
            _process = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
            }
            catch { }
            p.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using GTANetworkShared;

namespace GTANetwork.Util
{
    /// <summary>
    /// The client's integrity report (T-017): SHA-256 of every DLL in bin\scripts, the browser host and libcef, computed once in
    /// the background at start and sent with the connection request; the server compares it with the release's manifest.json.
    /// </summary>
    internal static class Integrity
    {
        private static IntegrityReport _report;
        private static int _started;

        public static IntegrityReport Report => _report;

        public static void ComputeInBackground(string installDir, string version)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            new Thread(() =>
            {
                try
                {
                    var files = new List<FileHash>();
                    var scripts = Path.Combine(installDir, "bin", "scripts");
                    if (Directory.Exists(scripts))
                        foreach (var dll in Directory.GetFiles(scripts, "*.dll")) files.Add(new FileHash { Name = "bin/scripts/" + Path.GetFileName(dll), Sha256 = Sha256Of(dll) });
                    foreach (var extra in new[] { Path.Combine("cef", "GTANetwork.CefHost.exe"), Path.Combine("cef", "libcef.dll") })
                    {
                        var path = Path.Combine(installDir, extra);
                        if (File.Exists(path)) files.Add(new FileHash { Name = extra.Replace('\\', '/'), Sha256 = Sha256Of(path) });
                    }
                    _report = new IntegrityReport { Version = version, Files = files };
                    LogManager.RuntimeLog("integrity: " + files.Count + " files hashed");
                }
                catch (Exception ex)
                {
                    LogManager.RuntimeLog("integrity: report failed: " + ex.Message);
                }
            }) { IsBackground = true, Name = "integrity" }.Start();
        }

        private static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}

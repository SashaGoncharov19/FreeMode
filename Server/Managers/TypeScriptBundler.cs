using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Client scripts in TypeScript (T-005). At resource start the entry file (<c>&lt;script src="client/index.ts" type="client"
    /// lang="typescript"/&gt;</c>) is bundled with Bun into one JavaScript text — imports resolved, types erased, wrapped as an
    /// IIFE — which the in-game engine (ClearScript V8, one script text per file) runs exactly like a client.js. Bundles are
    /// cached under <c>resources/.cache/&lt;resource&gt;/&lt;hash&gt;.js</c>, keyed by the Bun version, the entry and the contents
    /// of the resource's source files, so a second start costs one hash. Bun erases types without checking them: when the
    /// resource has TypeScript installed (<c>node_modules/typescript</c>) and a <c>tsconfig.json</c>, <c>tsc --noEmit</c> runs
    /// first and a type error fails the start with its file:line; a syntax error fails it in any case.
    /// </summary>
    internal static class TypeScriptBundler
    {
        private const int TimeoutMs = 30000;
        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".mts", ".cts", ".js", ".mjs", ".cjs", ".json" };
        private static readonly HashSet<string> SkippedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".cache", ".git" };
        private static readonly object VersionLock = new object();
        private static string _bunVersion;

        internal sealed class Result
        {
            public string JavaScript;
            public bool Cached;
            public long Milliseconds;
            public int Bytes => JavaScript == null ? 0 : Encoding.UTF8.GetByteCount(JavaScript);
        }

        /// <summary>
        /// Bundles <paramref name="entry"/> (a path relative to <paramref name="resourceDir"/>) with <paramref name="bun"/>.
        /// Throws with a readable message (Bun's or tsc's output, file:line included) when the script cannot be bundled.
        /// </summary>
        public static Result Bundle(string resourceName, string resourceDir, string entry, string bun)
        {
            var clock = Stopwatch.StartNew();
            resourceDir = Path.GetFullPath(resourceDir);
            var entryPath = Path.Combine(resourceDir, entry);
            if (!File.Exists(entryPath)) throw new FileNotFoundException("client script " + entry + " not found", entryPath);

            var version = BunVersion(bun);
            var hash = SourceHash(resourceDir, entry, version);
            var cacheDir = Path.Combine(Path.GetDirectoryName(resourceDir) ?? resourceDir, ".cache", resourceName);
            var cacheFile = Path.Combine(cacheDir, hash + ".js");
            if (File.Exists(cacheFile))
                return new Result { JavaScript = File.ReadAllText(cacheFile), Cached = true, Milliseconds = clock.ElapsedMilliseconds };

            TypeCheck(resourceDir, bun);

            Directory.CreateDirectory(cacheDir);
            var temp = cacheFile + ".tmp";
            int exitCode;
            var output = Run(bun, resourceDir, new[] { "build", entry, "--target=browser", "--format=iife", "--outfile=" + temp }, out exitCode);
            if (exitCode != 0 || !File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException("bun build failed for " + entry + " (exit code " + exitCode + "):\n" + output);
            }
            var js = File.ReadAllText(temp);
            File.Move(temp, cacheFile, true);
            Prune(cacheDir, cacheFile);
            return new Result { JavaScript = js, Cached = false, Milliseconds = clock.ElapsedMilliseconds };
        }

        /// <summary>The resource brought its own TypeScript: a real type check before the bundle; its errors are the exception message.</summary>
        private static void TypeCheck(string resourceDir, string bun)
        {
            var tsconfig = Path.Combine(resourceDir, "tsconfig.json");
            if (!File.Exists(tsconfig) || !Directory.Exists(Path.Combine(resourceDir, "node_modules", "typescript"))) return;
            int exitCode;
            var output = Run(bun, resourceDir, new[] { "x", "tsc", "--noEmit", "-p", "tsconfig.json" }, out exitCode);
            if (exitCode != 0) throw new InvalidOperationException("TypeScript errors (tsc --noEmit):\n" + output);
        }

        private static string BunVersion(string bun)
        {
            lock (VersionLock)
            {
                if (_bunVersion != null) return _bunVersion;
                int exitCode;
                var output = Run(bun, Directory.GetCurrentDirectory(), new[] { "--version" }, out exitCode);
                _bunVersion = exitCode == 0 ? output.Trim() : "unknown";
                return _bunVersion;
            }
        }

        /// <summary>MD5 over the Bun version, the entry and every source file of the resource (node_modules by its lock file only).</summary>
        private static string SourceHash(string resourceDir, string entry, string bunVersion)
        {
            using (var md5 = MD5.Create())
            {
                Feed(md5, "bun " + bunVersion + "\n" + entry + "\n");
                foreach (var file in SourceFiles(resourceDir).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var relative = file.Substring(resourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    var bytes = File.ReadAllBytes(file);
                    Feed(md5, relative + ":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n");
                    md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(md5.Hash.Select(b => b.ToString("x2")));
            }
        }

        private static void Feed(MD5 md5, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static IEnumerable<string> SourceFiles(string dir)
        {
            var pending = new Stack<string>();
            pending.Push(dir);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(current); }
                catch (Exception) { continue; }
                foreach (var path in entries)
                {
                    if (Directory.Exists(path))
                    {
                        if (!SkippedDirs.Contains(Path.GetFileName(path))) pending.Push(path);
                        continue;
                    }
                    var name = Path.GetFileName(path);
                    if (SourceExtensions.Contains(Path.GetExtension(path)) || name == "bun.lock" || name == "bun.lockb" || name == "package-lock.json")
                        yield return path;
                }
            }
        }

        /// <summary>Old bundles of this resource go after a week; the current one stays.</summary>
        private static void Prune(string cacheDir, string keep)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(cacheDir, "*.js"))
                {
                    if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Runs Bun with the arguments in <paramref name="workingDir"/>; returns stdout + stderr, kills it after <see cref="TimeoutMs"/>.</summary>
        private static string Run(string bun, string workingDir, IEnumerable<string> arguments, out int exitCode)
        {
            var psi = new ProcessStartInfo(bun)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["FORCE_COLOR"] = "0";
            using (var process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("could not start " + bun);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    throw new TimeoutException("bun " + string.Join(" ", arguments) + " did not finish within " + TimeoutMs / 1000 + " s");
                }
                process.WaitForExit();
                exitCode = process.ExitCode;
                var text = (stderr.Result + "\n" + stdout.Result).Trim();
                return string.Join("\n", text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0));
            }
        }
    }
}

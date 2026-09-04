using System.Text.Json;

namespace GTANetwork.Launcher;

/// <summary>What was changed inside the game folder, so that it can be undone (also after a crash).</summary>
internal sealed class DeployManifest
{
    public DateTime Time { get; set; }
    public List<string> Deployed { get; set; } = new();                 // files we placed into the game folder
    public Dictionary<string, string> Backups { get; set; } = new();    // overwritten file -> backup path (relative to game folder)
    public List<string> MovedAsi { get; set; } = new();                 // foreign *.asi moved to Disabled/
    public bool CommandlineCreated { get; set; }
    public string? CommandlineOriginal { get; set; }
}

internal static class Deployment
{
    public const string ManifestName = "gtanetwork-deploy.json";
    public const string DisabledDir = "Disabled";
    public const string ShvdnAsi = "ScriptHookVDotNet.asi";

    private static readonly string[] LoaderFiles = { "ScriptHookV.dll", "dinput8.dll" };

    public static string ManifestPath(string gameDir) => Path.Combine(gameDir, ManifestName);

    public static bool IsDeployed(string gameDir) => File.Exists(ManifestPath(gameDir));

    /// <summary>Checks that the installation folder has everything the game side needs. Returns problems.</summary>
    public static List<string> Check(Paths paths, string? gameDir)
    {
        var problems = new List<string>();

        foreach (var name in LoaderFiles)
        {
            if (File.Exists(Path.Combine(paths.BinDir, name))) continue;
            if (gameDir != null && File.Exists(Path.Combine(gameDir, name))) continue;
            problems.Add($"{name} is missing. Download ScriptHookV from http://www.dev-c.com/gtav/scripthookv/ and copy ScriptHookV.dll and dinput8.dll into {paths.BinDir}");
        }

        if (FindShvdn(paths) == null)
            problems.Add($"ScriptHookVDotNet.dll is missing in {paths.BinDir} (built from Shv.NET on Windows; also published by the GitHub Actions windows job).");

        if (!File.Exists(Path.Combine(paths.ScriptsDir, "GTANetwork.dll")))
            problems.Add($"GTANetwork.dll is missing in {paths.ScriptsDir}");

        if (!File.Exists(Path.Combine(paths.CefDir, "libcef.dll")))
            problems.Add($"CEF runtime is missing ({paths.CefDir}/libcef.dll); the in-game browser UI will not work.");

        return problems;
    }

    private static string? FindShvdn(Paths paths)
    {
        foreach (var name in new[] { "ScriptHookVDotNet.dll", ShvdnAsi })
        {
            var candidate = Path.Combine(paths.BinDir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void Deploy(Paths paths, string gameDir, bool disableOtherAsi, bool scOfflineOnly)
    {
        if (IsDeployed(gameDir))
        {
            Log.Warn("A previous deployment was not cleaned up (crash?). Restoring it first.");
            Restore(gameDir);
        }

        var problems = Check(paths, gameDir);
        var fatal = problems.Where(p => !p.Contains("CEF", StringComparison.Ordinal)).ToList();
        if (fatal.Count > 0) throw new LauncherException(string.Join(Environment.NewLine, fatal));
        foreach (var p in problems.Except(fatal)) Log.Warn(p);

        var manifest = new DeployManifest { Time = DateTime.Now };
        var disabled = Path.Combine(gameDir, DisabledDir);

        try
        {
            // 1. Other ASI plugins conflict with the multiplayer client; park them for the session.
            if (disableOtherAsi)
            {
                foreach (var asi in Directory.GetFiles(gameDir, "*.asi"))
                {
                    var name = Path.GetFileName(asi);
                    if (string.Equals(name, ShvdnAsi, StringComparison.OrdinalIgnoreCase)) continue;

                    Directory.CreateDirectory(disabled);
                    var target = Path.Combine(disabled, name);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(asi, target);
                    manifest.MovedAsi.Add(name);
                    Log.Info($"Disabled foreign plugin for this session: {name}");
                }
            }

            // 2. Loader + native helpers: every *.dll / *.asi in <install>/bin (not scripts/).
            //    ScriptHookVDotNet.dll becomes ScriptHookVDotNet.asi so that ScriptHookV's dinput8.dll loads it.
            var sources = Directory.GetFiles(paths.BinDir)
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var source in sources)
            {
                var name = Path.GetFileName(source);
                var targetName = string.Equals(name, "ScriptHookVDotNet.dll", StringComparison.OrdinalIgnoreCase) ? ShvdnAsi : name;
                CopyWithBackup(gameDir, source, targetName, manifest);
            }

            // 3. Tell SHVDN where the client assemblies live (<install>/bin/scripts).
            var iniTemp = Path.Combine(Path.GetTempPath(), "ScriptHookVDotNet.ini");
            File.WriteAllText(iniTemp,
                "; generated by GTANetwork.Launcher - do not edit, it is rewritten on every start" + Environment.NewLine +
                "ScriptsLocation=" + Paths.ToWindowsPath(paths.ScriptsDir) + Environment.NewLine +
                "ReloadKeyBinding=None" + Environment.NewLine);
            CopyWithBackup(gameDir, iniTemp, "ScriptHookVDotNet.ini", manifest);
            File.Delete(iniTemp);

            // 4. Social Club offline mode (what the classic launcher always did).
            if (scOfflineOnly)
            {
                var commandline = Path.Combine(gameDir, "commandline.txt");
                if (File.Exists(commandline))
                {
                    manifest.CommandlineOriginal = File.ReadAllText(commandline);
                    if (!manifest.CommandlineOriginal.Contains("-scOfflineOnly", StringComparison.OrdinalIgnoreCase))
                        File.AppendAllText(commandline, Environment.NewLine + "-scOfflineOnly" + Environment.NewLine);
                }
                else
                {
                    File.WriteAllText(commandline, "-scOfflineOnly" + Environment.NewLine);
                    manifest.CommandlineCreated = true;
                }
            }

            Save(gameDir, manifest);
            Log.Ok($"Deployed {manifest.Deployed.Count} file(s) into {gameDir}");
        }
        catch
        {
            Save(gameDir, manifest);
            Restore(gameDir);
            throw;
        }
    }

    private static void CopyWithBackup(string gameDir, string source, string targetName, DeployManifest manifest)
    {
        var target = Path.Combine(gameDir, targetName);

        if (File.Exists(target) && !manifest.Deployed.Contains(targetName, StringComparer.OrdinalIgnoreCase))
        {
            var backupRelative = Path.Combine(DisabledDir, "gtan-backup", targetName);
            var backup = Path.Combine(gameDir, backupRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, true);
            manifest.Backups[targetName] = backupRelative;
        }

        File.Copy(source, target, true);
        if (!manifest.Deployed.Contains(targetName, StringComparer.OrdinalIgnoreCase)) manifest.Deployed.Add(targetName);
    }

    public static void Restore(string gameDir)
    {
        var manifestPath = ManifestPath(gameDir);
        if (!File.Exists(manifestPath))
        {
            Log.Info("Nothing to restore.");
            return;
        }

        DeployManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeployManifest>(File.ReadAllText(manifestPath)) ?? new DeployManifest();
        }
        catch (Exception ex)
        {
            throw new LauncherException($"Cannot read {manifestPath}: {ex.Message}. Clean the game folder by hand (remove ScriptHookVDotNet.asi/.ini, dinput8.dll, ScriptHookV.dll, move Disabled/*.asi back).");
        }

        foreach (var name in manifest.Deployed)
        {
            var file = Path.Combine(gameDir, name);
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { Log.Warn($"Could not delete {file}: {ex.Message}"); }
        }

        foreach (var (name, backupRelative) in manifest.Backups)
        {
            var backup = Path.Combine(gameDir, backupRelative);
            try
            {
                if (File.Exists(backup)) File.Move(backup, Path.Combine(gameDir, name), true);
            }
            catch (Exception ex) { Log.Warn($"Could not restore {name}: {ex.Message}"); }
        }

        foreach (var name in manifest.MovedAsi)
        {
            var parked = Path.Combine(gameDir, DisabledDir, name);
            try
            {
                if (File.Exists(parked)) File.Move(parked, Path.Combine(gameDir, name), true);
            }
            catch (Exception ex) { Log.Warn($"Could not move {name} back: {ex.Message}"); }
        }

        var commandline = Path.Combine(gameDir, "commandline.txt");
        try
        {
            if (manifest.CommandlineCreated)
            {
                if (File.Exists(commandline)) File.Delete(commandline);
            }
            else if (manifest.CommandlineOriginal != null)
            {
                File.WriteAllText(commandline, manifest.CommandlineOriginal);
            }
        }
        catch (Exception ex) { Log.Warn($"Could not restore commandline.txt: {ex.Message}"); }

        try
        {
            var backupDir = Path.Combine(gameDir, DisabledDir, "gtan-backup");
            if (Directory.Exists(backupDir) && !Directory.EnumerateFileSystemEntries(backupDir).Any()) Directory.Delete(backupDir);
            var disabled = Path.Combine(gameDir, DisabledDir);
            if (Directory.Exists(disabled) && !Directory.EnumerateFileSystemEntries(disabled).Any()) Directory.Delete(disabled);
        }
        catch
        {
            // ignored
        }

        File.Delete(manifestPath);
        Log.Ok("Game folder restored.");
    }

    private static void Save(string gameDir, DeployManifest manifest)
    {
        File.WriteAllText(ManifestPath(gameDir), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}

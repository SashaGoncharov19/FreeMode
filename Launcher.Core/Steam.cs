using System.Runtime.InteropServices;

namespace GTANetwork.Launcher;

/// <summary>Locates Steam, its library folders, GTA V, the game's Wine prefix and Proton builds.</summary>
public static class Steam
{
    public const int GtaVAppId = 271590; // Grand Theft Auto V (Legacy)

    public static string? FindSteamRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return Path.GetFullPath(configured);

        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var value = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(value)) candidates.Add(value.Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                // ignored
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".steam", "root"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")); // Flatpak
            candidates.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));               // Snap
            candidates.Add(Path.Combine(home, ".steam", "debian-installation"));
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "steamapps"))) return ResolveLinks(candidate);
        }

        return null;
    }

    public static bool IsFlatpak(string steamRoot) => steamRoot.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);

    /// <summary>All Steam library folders (each contains a "steamapps" directory).</summary>
    public static List<string> FindLibraries(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };

        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) vdfPath = Path.Combine(steamRoot, "config", "libraryfolders.vdf");

        if (File.Exists(vdfPath))
        {
            try
            {
                var root = Vdf.Parse(File.ReadAllText(vdfPath));
                if (Vdf.Get(root, "libraryfolders") is Dictionary<string, object> folders)
                {
                    foreach (var entry in folders.Values)
                    {
                        string? path = entry switch
                        {
                            string s => s,                                                                 // old format: "1" "/path"
                            Dictionary<string, object> d when d.TryGetValue("path", out var p) => p as string, // new format
                            _ => null,
                        };

                        if (!string.IsNullOrEmpty(path)) libraries.Add(path.Replace("\\\\", "\\"));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not parse {vdfPath}: {ex.Message}");
            }
        }

        return libraries
            .Select(ResolveLinks)
            .Where(l => Directory.Exists(Path.Combine(l, "steamapps")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds the GTA V install folder. Returns (gameDir, libraryDir) or null.</summary>
    public static (string GameDir, string LibraryDir)? FindGame(IEnumerable<string> libraries)
    {
        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{GtaVAppId}.acf");
            string installDirName = "Grand Theft Auto V";

            if (File.Exists(manifest))
            {
                try
                {
                    var acf = Vdf.Parse(File.ReadAllText(manifest));
                    if (Vdf.Get(acf, "AppState", "installdir") is string dir && !string.IsNullOrWhiteSpace(dir)) installDirName = dir;
                }
                catch
                {
                    // ignored
                }
            }

            var gameDir = Path.Combine(library, "steamapps", "common", installDirName);
            if (File.Exists(Path.Combine(gameDir, "GTA5.exe"))) return (gameDir, library);
        }

        return null;
    }

    public static string? FindPrefix(string libraryDir)
    {
        var prefix = Path.Combine(libraryDir, "steamapps", "compatdata", GtaVAppId.ToString(), "pfx");
        return Directory.Exists(prefix) ? prefix : null;
    }

    /// <summary>The compat tool name Steam has configured for GTA V (e.g. "proton_experimental", "GE-Proton9-20"), if any.</summary>
    public static string? ConfiguredCompatTool(string steamRoot)
    {
        var configPath = Path.Combine(steamRoot, "config", "config.vdf");
        if (!File.Exists(configPath)) return null;

        try
        {
            var root = Vdf.Parse(File.ReadAllText(configPath));
            var mapping = Vdf.Get(root, "InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping") as Dictionary<string, object>;
            if (mapping == null) return null;

            foreach (var key in new[] { GtaVAppId.ToString(), "0" }) // "0" = global default
            {
                if (mapping.TryGetValue(key, out var entry) && entry is Dictionary<string, object> d && d.TryGetValue("name", out var name) && name is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    /// <summary>Picks a Proton directory (the one containing the "proton" script).</summary>
    public static string? FindProton(string? configured, string steamRoot, IEnumerable<string> libraries)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var dir = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(dir, "proton"))) return dir;
            Log.Warn($"Configured Proton path does not contain a \"proton\" script: {dir}");
        }

        var available = new List<string>();

        foreach (var library in libraries)
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            available.AddRange(Directory.GetDirectories(common).Where(d => File.Exists(Path.Combine(d, "proton"))));
        }

        var custom = Path.Combine(steamRoot, "compatibilitytools.d");
        if (Directory.Exists(custom))
            available.AddRange(Directory.GetDirectories(custom).Where(d => File.Exists(Path.Combine(d, "proton"))));

        if (available.Count == 0) return null;

        // Prefer the tool Steam already uses for the game.
        var configuredTool = ConfiguredCompatTool(steamRoot);
        if (configuredTool != null)
        {
            var exact = available.FirstOrDefault(d => string.Equals(Path.GetFileName(d), configuredTool, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var token = configuredTool.Replace("proton_", "", StringComparison.OrdinalIgnoreCase).Replace("_", " ");
            var fuzzy = available.FirstOrDefault(d => Path.GetFileName(d).Contains(token, StringComparison.OrdinalIgnoreCase));
            if (fuzzy != null) return fuzzy;
        }

        return available
            .OrderByDescending(d => Path.GetFileName(d).Contains("Experimental", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>Launch options the user set for GTA V in Steam (first user found), or null.</summary>
    public static string? LaunchOptions(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return null;

        foreach (var user in Directory.GetDirectories(userdata))
        {
            var localConfig = Path.Combine(user, "config", "localconfig.vdf");
            if (!File.Exists(localConfig)) continue;

            try
            {
                var root = Vdf.Parse(File.ReadAllText(localConfig));
                var apps = Vdf.Get(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps") as Dictionary<string, object>
                           ?? Vdf.Get(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "Apps") as Dictionary<string, object>;
                if (apps != null && apps.TryGetValue(GtaVAppId.ToString(), out var app) && app is Dictionary<string, object> d && d.TryGetValue("LaunchOptions", out var lo))
                    return lo as string;
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.LinkTarget != null ? (info.ResolveLinkTarget(true)?.FullName ?? path) : info.FullName;
        }
        catch
        {
            return path;
        }
    }
}

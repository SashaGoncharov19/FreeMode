using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using GTANetworkShared;

namespace GTANetwork.Launcher;

internal static class Program
{
    private const string Usage = @"GTA Network launcher (cross-platform)

Usage: GTANetwork.Launcher [command] [options]

Commands:
  run       (default) install the mod into the game folder, start GTA V, wait for it to exit, clean up
  deploy    only install the mod files into the game folder (use ""restore"" afterwards)
  restore   undo ""deploy"" (also done automatically at the start of ""run"")
  doctor    show what was detected and what is missing

Options:
  --game-path <dir>        folder that contains GTA5.exe (auto-detected from Steam)
  --method <steam|proton|direct>
                           steam  : ask Steam to start the game (needs launch options, see doctor)
                           proton : start the game through Proton directly (Linux)
                           direct : start PlayGTAV.exe / GTAVLauncher.exe (Windows)
  --steam <dir>            Steam root (auto-detected)
  --proton <dir>           Proton build to use, the folder containing the ""proton"" script (auto-detected)
  --prefix <dir>           Wine prefix of the game (auto-detected: steamapps/compatdata/271590/pfx)
  --install-dir <dir>      GTA Network folder (default: the folder of this executable)
  --keep-asi               do not park other *.asi plugins during the session
  --no-offline             do not add -scOfflineOnly to commandline.txt
  --no-wait                start the game and exit immediately (files stay deployed; run ""restore"" later)
  --save                   write the effective --game-path/--method/--steam/--proton/--prefix into settings.xml
  --debug                  debug mode: GTAN_DEBUG=1 for the in-game client (diagnostic lines in Runtime.log and
                           CEF.log) and PROTON_LOG=1 for Proton (Wine log with exceptions and module loads in
                           ~/steam-271590.log; GTAN_WINEDEBUG overrides the channels). On Linux also a per-second
                           system monitor (swap, memory stalls, GTA5 page faults, Chromium memory, CPU/GPU clocks)
                           in logs/hitch-monitor.log, to match against [HITCH] lines in Runtime.log. Costs frame rate.
  -h, --help               this text
";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (LauncherException ex)
        {
            Log.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            return 1;
        }
    }

    /// <summary>--debug: diagnostics on in the client (GTAN_DEBUG=1) and the Wine log for Proton (PROTON_LOG=1).</summary>
    private static bool _debug;

    private static int Run(string[] args)
    {
        var command = "run";
        string? gamePath = null, method = null, steamPath = null, protonPath = null, prefixPath = null, installDir = null;
        bool keepAsi = false, noOffline = false, noWait = false, save = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new LauncherException($"Option {args[i]} needs a value.");

            switch (args[i])
            {
                case "run": case "deploy": case "restore": case "doctor": command = args[i]; break;
                case "--game-path": gamePath = Next(); break;
                case "--method": method = Next(); break;
                case "--steam": steamPath = Next(); break;
                case "--proton": protonPath = Next(); break;
                case "--prefix": prefixPath = Next(); break;
                case "--install-dir": installDir = Next(); break;
                case "--keep-asi": keepAsi = true; break;
                case "--no-offline": noOffline = true; break;
                case "--no-wait": noWait = true; break;
                case "--save": save = true; break;
                case "--debug": _debug = true; break;
                case "-h": case "--help": case "help": Console.WriteLine(Usage); return 0;
                default: throw new LauncherException($"Unknown argument: {args[i]}{Environment.NewLine}{Usage}");
            }
        }

        var paths = new Paths(installDir ?? Environment.GetEnvironmentVariable("GTAN_INSTALL_DIR") ?? AppContext.BaseDirectory);
        Log.UseFile(Path.Combine(paths.LogsDir, "launcher.log"));

        var settings = LoadSettings(paths.SettingsPath);
        if (gamePath != null) settings.GamePath = gamePath;
        if (method != null) settings.LaunchMethod = method;
        if (steamPath != null) settings.SteamPath = steamPath;
        if (protonPath != null) settings.ProtonPath = protonPath;
        if (prefixPath != null) settings.ProtonPrefixPath = prefixPath;
        if (keepAsi) settings.DisableOtherAsiPlugins = false;
        if (noOffline) settings.ScOfflineOnly = false;

        var env = DetectedEnvironment.Detect(paths, settings);

        if (save || string.IsNullOrWhiteSpace(settings.GamePath) && env.GameDir != null)
        {
            settings.GamePath = env.GameDir ?? settings.GamePath;
            if (save)
            {
                settings.SteamPath = env.SteamRoot ?? settings.SteamPath;
                settings.ProtonPath = env.ProtonDir ?? settings.ProtonPath;
                settings.ProtonPrefixPath = env.Prefix ?? settings.ProtonPrefixPath;
            }
            SaveSettings(paths.SettingsPath, settings);
        }

        switch (command)
        {
            case "doctor":
                return Doctor(paths, settings, env);

            case "restore":
                Deployment.Restore(RequireGame(env));
                return 0;

            case "deploy":
                Deployment.Deploy(paths, RequireGame(env), settings.DisableOtherAsiPlugins, settings.ScOfflineOnly);
                GamePatcher.Patch(GamePatcher.DocumentsDir(env.Prefix));
                return 0;

            default:
                return Play(paths, settings, env, noWait);
        }
    }

    private static string RequireGame(DetectedEnvironment env)
    {
        return env.GameDir ?? throw new LauncherException("GTA V was not found. Pass --game-path <folder containing GTA5.exe> (add --save to remember it).");
    }

    private static int Play(Paths paths, PlayerSettings settings, DetectedEnvironment env, bool noWait)
    {
        var gameDir = RequireGame(env);

        if (GameProcess.IsRunning("GTA5.exe"))
            throw new LauncherException("GTA5.exe is already running. Close the game first.");

        var problems = Deployment.Check(paths, gameDir);
        foreach (var p in problems) Log.Warn(p);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        Deployment.Deploy(paths, gameDir, settings.DisableOtherAsiPlugins, settings.ScOfflineOnly);
        GamePatcher.Patch(GamePatcher.DocumentsDir(env.Prefix));

        var keepDeployed = false;

        try
        {
            Launch(paths, settings, env, gameDir);

            if (noWait)
            {
                keepDeployed = true;
                Log.Info("--no-wait: leaving the mod deployed. Run \"GTANetwork.Launcher restore\" after playing.");
                return 0;
            }

            Log.Info("Waiting for GTA5.exe to start (up to 15 minutes, log in to the Rockstar launcher if it asks) ...");
            if (!GameProcess.WaitForStart("GTA5.exe", TimeSpan.FromMinutes(15), cancel.Token))
            {
                Log.Error("GTA5.exe did not start.");
                return 3;
            }

            Log.Ok("GTA5.exe is running. Waiting for it to exit ...");
            // --debug on Linux: a per-second picture of the machine next to the game's own hitch lines (HitchMonitor).
            using (_debug && OperatingSystem.IsLinux() ? HitchMonitor.Start(Path.Combine(paths.InstallDir, "logs", "hitch-monitor.log")) : null)
            {
                GameProcess.WaitForExit("GTA5.exe", cancel.Token);
            }

            if (cancel.IsCancellationRequested)
                Log.Warn("Interrupted. Restoring the game folder; the game may still be running.");
            else
                Log.Info("GTA5.exe exited.");

            return 0;
        }
        finally
        {
            if (!keepDeployed)
            {
                Thread.Sleep(1000);
                Deployment.Restore(gameDir);
            }
        }
    }

    private static void Launch(Paths paths, PlayerSettings settings, DetectedEnvironment env, string gameDir)
    {
        var method = (settings.LaunchMethod ?? "steam").Trim().ToLowerInvariant();

        switch (method)
        {
            case "steam":
            {
                if (_debug) Log.Warn("--debug cannot pass GTAN_DEBUG through Steam; set <DebugMode>true</DebugMode> in settings.xml instead.");
                if (!OperatingSystem.IsWindows())
                {
                    Log.Info("Reminder: the Steam launch options of GTA V must contain  WINEDLLOVERRIDES=\"dinput8=n,b\" %command%");
                    if (env.SteamLaunchOptions != null && !env.SteamLaunchOptions.Contains("dinput8", StringComparison.OrdinalIgnoreCase))
                        Log.Warn($"Current launch options are: \"{env.SteamLaunchOptions}\" - ScriptHookV will NOT load without the dinput8 override!");
                }

                var url = $"steam://rungameid/{Steam.GtaVAppId}";

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (env.SteamRoot != null && Steam.IsFlatpak(env.SteamRoot))
                {
                    Start("flatpak", "run", "com.valvesoftware.Steam", "-applaunch", Steam.GtaVAppId.ToString());
                }
                else if (Which("steam") != null)
                {
                    Start("steam", "-applaunch", Steam.GtaVAppId.ToString());
                }
                else
                {
                    Start("xdg-open", url);
                }

                Log.Ok("Asked Steam to start GTA V.");
                break;
            }

            case "proton":
            {
                if (OperatingSystem.IsWindows()) throw new LauncherException("--method proton is for Linux; use steam or direct on Windows.");
                if (env.ProtonDir == null) throw new LauncherException("No Proton build found. Install one through Steam (Proton Experimental) or pass --proton <dir>.");
                if (env.Prefix == null) throw new LauncherException("No Wine prefix for GTA V found. Run the game once through Steam, or pass --prefix <.../compatdata/271590/pfx>.");
                if (env.SteamRoot == null) throw new LauncherException("Steam root not found; pass --steam <dir>.");

                var exe = new[] { "PlayGTAV.exe", "GTAVLauncher.exe", "GTA5.exe" }
                    .Select(n => Path.Combine(gameDir, n)).FirstOrDefault(File.Exists)
                    ?? throw new LauncherException("No game launcher executable found in " + gameDir);

                var psi = new ProcessStartInfo(Path.Combine(env.ProtonDir, "proton"))
                {
                    WorkingDirectory = gameDir,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(exe);

                var compatData = Path.GetDirectoryName(env.Prefix.TrimEnd('/'))!; // .../compatdata/271590
                psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = env.SteamRoot;
                psi.Environment["STEAM_COMPAT_DATA_PATH"] = compatData;
                psi.Environment["SteamAppId"] = Steam.GtaVAppId.ToString();
                psi.Environment["SteamGameId"] = Steam.GtaVAppId.ToString();
                psi.Environment["WINEDLLOVERRIDES"] = "dinput8=n,b";
                psi.Environment["GTAN_INSTALL_DIR"] = Paths.ToWindowsPath(paths.InstallDir);
                if (_debug)
                {
                    psi.Environment["GTAN_DEBUG"] = "1";
                    psi.Environment["PROTON_LOG"] = "1";
                    // Proton's default WINEDEBUG for PROTON_LOG=1 adds +unwind and +debugstr, which trace every stack walk
                    // (millions of lines from .NET's GC alone), and GTA V calls ActivateKeyboardLayout in a hot loop while
                    // loading (10 million fixme lines, over 1 GB of log per session). Keep what crash analysis needs.
                    psi.Environment["WINEDEBUG"] = Environment.GetEnvironmentVariable("GTAN_WINEDEBUG")
                        ?? "+timestamp,+pid,+tid,+seh,+threadname,+loaddll,+mscoree,-keyboard";
                    Log.Info($"Debug mode: GTAN_DEBUG=1 (client diagnostics), PROTON_LOG=1 with WINEDEBUG={psi.Environment["WINEDEBUG"]} (Wine log: ~/steam-271590.log)");
                }

                Log.Info($"Starting through Proton: {psi.FileName} run {exe}");
                Log.Info("(Steam should be running in the background for Steam builds of the game.)");
                Process.Start(psi);
                break;
            }

            case "direct":
            {
                if (!OperatingSystem.IsWindows()) throw new LauncherException("--method direct is for Windows; use steam or proton on Linux.");
                var exe = new[] { "PlayGTAV.exe", "GTAVLauncher.exe" }
                    .Select(n => Path.Combine(gameDir, n)).FirstOrDefault(File.Exists)
                    ?? throw new LauncherException("No game launcher executable found in " + gameDir);
                if (_debug) Environment.SetEnvironmentVariable("GTAN_DEBUG", "1"); // inherited by the game process
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
                Log.Ok("Started " + exe);
                break;
            }

            default:
                throw new LauncherException($"Unknown launch method \"{method}\" (steam, proton or direct).");
        }
    }

    private static int Doctor(Paths paths, PlayerSettings settings, DetectedEnvironment env)
    {
        Console.WriteLine();
        Console.WriteLine($"OS:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Install folder:   {paths.InstallDir}");
        Console.WriteLine($"Settings:         {paths.SettingsPath} ({(File.Exists(paths.SettingsPath) ? "found" : "will be created")})");
        Console.WriteLine($"Launch method:    {settings.LaunchMethod}");
        Console.WriteLine($"Steam root:       {env.SteamRoot ?? "not found"}");
        Console.WriteLine($"Steam libraries:  {(env.Libraries.Count == 0 ? "-" : string.Join(", ", env.Libraries))}");
        Console.WriteLine($"GTA V folder:     {env.GameDir ?? "NOT FOUND (use --game-path)"}");
        Console.WriteLine($"Wine prefix:      {env.Prefix ?? "not found"}");
        Console.WriteLine($"Proton:           {env.ProtonDir ?? "not found"}");
        Console.WriteLine($"GTA V documents:  {GamePatcher.DocumentsDir(env.Prefix) ?? "not found (start the game once)"}");
        if (!OperatingSystem.IsWindows())
            Console.WriteLine($"Launch options:   {env.SteamLaunchOptions ?? "(none)"}");
        Console.WriteLine();

        var ok = true;

        foreach (var problem in Deployment.Check(paths, env.GameDir))
        {
            Log.Warn(problem);
            ok = false;
        }

        if (env.GameDir == null) ok = false;

        if (!OperatingSystem.IsWindows())
        {
            if (env.Prefix == null)
            {
                Log.Warn("No Wine prefix for GTA V found (steamapps/compatdata/271590/pfx). Start the game once through Steam so that Proton creates it, or pass --prefix.");
                ok = false;
            }

            if (env.Prefix != null)
            {
                var clr = Path.Combine(env.Prefix, "drive_c", "windows", "Microsoft.NET", "Framework64", "v4.0.30319", "clr.dll");
                if (File.Exists(clr))
                    Log.Ok(".NET Framework 4.x is installed in the prefix.");
                else
                {
                    Log.Warn(".NET Framework 4.8 does not seem to be installed in the prefix. ScriptHookVDotNet needs it:  protontricks 271590 dotnet48   (or: WINEPREFIX=<prefix> winetricks dotnet48)");
                    ok = false;
                }
            }

            if (string.Equals(settings.LaunchMethod, "steam", StringComparison.OrdinalIgnoreCase) &&
                (env.SteamLaunchOptions == null || !env.SteamLaunchOptions.Contains("dinput8", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn("Set the Steam launch options of GTA V to:  WINEDLLOVERRIDES=\"dinput8=n,b\" %command%   (or use --method proton)");
                ok = false;
            }
        }

        if (env.GameDir != null && Deployment.IsDeployed(env.GameDir))
            Log.Warn("The mod is currently deployed in the game folder (run \"restore\" to remove it).");

        if (ok) Log.Ok("Everything looks fine. Run \"GTANetwork.Launcher\" to play.");
        return ok ? 0 : 1;
    }

    private static void Start(string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private static string? Which(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(File.Exists);
    }

    private static PlayerSettings LoadSettings(string path)
    {
        if (!File.Exists(path)) return new PlayerSettings();

        try
        {
            using var stream = File.OpenRead(path);
            return (PlayerSettings)new XmlSerializer(typeof(PlayerSettings)).Deserialize(stream)! ;
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.xml could not be read ({ex.Message}); using defaults.");
            return new PlayerSettings();
        }
    }

    private static void SaveSettings(string path, PlayerSettings settings)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            new XmlSerializer(typeof(PlayerSettings)).Serialize(stream, settings);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save {path}: {ex.Message}");
        }
    }
}

/// <summary>Everything the launcher managed to detect about the machine.</summary>
internal sealed class DetectedEnvironment
{
    public string? SteamRoot { get; private set; }
    public List<string> Libraries { get; private set; } = new();
    public string? GameDir { get; private set; }
    public string? LibraryDir { get; private set; }
    public string? Prefix { get; private set; }
    public string? ProtonDir { get; private set; }
    public string? SteamLaunchOptions { get; private set; }

    public static DetectedEnvironment Detect(Paths paths, PlayerSettings settings)
    {
        var env = new DetectedEnvironment();

        env.SteamRoot = Steam.FindSteamRoot(settings.SteamPath);
        if (env.SteamRoot != null) env.Libraries = Steam.FindLibraries(env.SteamRoot);

        if (!string.IsNullOrWhiteSpace(settings.GamePath) && File.Exists(Path.Combine(settings.GamePath, "GTA5.exe")))
        {
            env.GameDir = Path.GetFullPath(settings.GamePath);
            env.LibraryDir = env.Libraries.FirstOrDefault(l => env.GameDir.StartsWith(l, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var found = Steam.FindGame(env.Libraries);
            if (found != null)
            {
                env.GameDir = found.Value.GameDir;
                env.LibraryDir = found.Value.LibraryDir;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(settings.ProtonPrefixPath) && Directory.Exists(settings.ProtonPrefixPath))
                env.Prefix = Path.GetFullPath(settings.ProtonPrefixPath);
            else
            {
                // Steam keeps the prefix in the library that holds the game, but look everywhere to be safe.
                if (env.LibraryDir != null) env.Prefix = Steam.FindPrefix(env.LibraryDir);
                env.Prefix ??= env.Libraries.Select(Steam.FindPrefix).FirstOrDefault(p => p != null);
            }

            if (env.SteamRoot != null)
            {
                env.ProtonDir = Steam.FindProton(settings.ProtonPath, env.SteamRoot, env.Libraries);
                env.SteamLaunchOptions = Steam.LaunchOptions(env.SteamRoot);
            }
        }

        return env;
    }
}

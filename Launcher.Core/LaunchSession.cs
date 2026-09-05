using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using GTANetworkShared;

namespace GTANetwork.Launcher;

/// <summary>Everything the launcher managed to detect about the machine.</summary>
public sealed class DetectedEnvironment
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

/// <summary>settings.xml of the install (PlayerSettings, shared with the in-game client).</summary>
public static class SettingsStore
{
    public static PlayerSettings Load(string path)
    {
        if (!File.Exists(path)) return new PlayerSettings();
        try
        {
            using var stream = File.OpenRead(path);
            return (PlayerSettings)new XmlSerializer(typeof(PlayerSettings)).Deserialize(stream)!;
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.xml could not be read ({ex.Message}); using defaults.");
            return new PlayerSettings();
        }
    }

    public static bool Save(string path, PlayerSettings settings)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            new XmlSerializer(typeof(PlayerSettings)).Serialize(stream, settings);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save {path}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>One line of the doctor report: Level is INFO, OK or WARN.</summary>
public sealed record DoctorLine(string Level, string Text);

/// <summary>
/// The launch pipeline the command line launcher and the GUI share: deploy the mod into the game folder, patch the game
/// settings, start the game (Steam, Proton or directly), wait for GTA5.exe, restore the folder. Progress goes through
/// <see cref="Log"/>; failures throw <see cref="LauncherException"/> with a message for the player.
/// </summary>
public static class LaunchSession
{
    public static string RequireGame(DetectedEnvironment env)
    {
        return env.GameDir ?? throw new LauncherException("GTA V was not found. Pass --game-path <folder containing GTA5.exe> (add --save to remember it).");
    }

    /// <summary>
    /// Deploys, starts the game and (unless <paramref name="noWait"/>) waits for GTA5.exe to exit, then restores the game folder.
    /// Returns the process exit code of the command line launcher: 0 ok, 3 when the game never started.
    /// </summary>
    public static int Play(Paths paths, PlayerSettings settings, DetectedEnvironment env, bool debug, bool noWait, CancellationToken cancel)
    {
        var gameDir = RequireGame(env);

        if (GameProcess.IsRunning("GTA5.exe"))
            throw new LauncherException("GTA5.exe is already running. Close the game first.");

        var problems = Deployment.Check(paths, gameDir);
        foreach (var p in problems) Log.Warn(p);

        Deployment.Deploy(paths, gameDir, settings.DisableOtherAsiPlugins, settings.ScOfflineOnly);
        GamePatcher.Patch(GamePatcher.DocumentsDir(env.Prefix));

        var keepDeployed = false;

        try
        {
            Launch(paths, settings, env, gameDir, debug);

            if (noWait)
            {
                keepDeployed = true;
                Log.Info("--no-wait: leaving the mod deployed. Run \"GTANetwork.Launcher restore\" after playing.");
                return 0;
            }

            Log.Info("Waiting for GTA5.exe to start (up to 15 minutes, log in to the Rockstar launcher if it asks) ...");
            if (!GameProcess.WaitForStart("GTA5.exe", TimeSpan.FromMinutes(15), cancel))
            {
                Log.Error("GTA5.exe did not start.");
                return 3;
            }

            Log.Ok("GTA5.exe is running. Waiting for it to exit ...");
            // --debug on Linux: a per-second picture of the machine next to the game's own hitch lines (HitchMonitor).
            using (debug && OperatingSystem.IsLinux() ? HitchMonitor.Start(Path.Combine(paths.InstallDir, "logs", "hitch-monitor.log")) : null)
            {
                GameProcess.WaitForExit("GTA5.exe", cancel);
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

    /// <summary>Starts the game with the configured method (steam, proton, direct); the mod must be deployed already.</summary>
    public static void Launch(Paths paths, PlayerSettings settings, DetectedEnvironment env, string gameDir, bool debug)
    {
        var method = (settings.LaunchMethod ?? "steam").Trim().ToLowerInvariant();

        switch (method)
        {
            case "steam":
            {
                if (debug) Log.Warn("--debug cannot pass GTAN_DEBUG through Steam; set <DebugMode>true</DebugMode> in settings.xml instead.");
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
                foreach (var name in new[] { "GTAN_AUTOTEST", "GTAN_AUTOTEST_QUIT" }) // the in-game smoke test (Client/Util/AutoTest.cs)
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrEmpty(value)) { psi.Environment[name] = value; Log.Info(name + "=" + value + " passed to the game"); }
                }
                if (debug)
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
                if (debug) Environment.SetEnvironmentVariable("GTAN_DEBUG", "1"); // inherited by the game process
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
                Log.Ok("Started " + exe);
                break;
            }

            default:
                throw new LauncherException($"Unknown launch method \"{method}\" (steam, proton or direct).");
        }
    }

    /// <summary>What was detected and what is missing, as lines for the console or the GUI; <c>ok</c> = ready to play.</summary>
    public static (bool Ok, List<DoctorLine> Lines) Doctor(Paths paths, PlayerSettings settings, DetectedEnvironment env)
    {
        var lines = new List<DoctorLine>();
        void Info(string text) => lines.Add(new DoctorLine("INFO", text));
        void Warn(string text) => lines.Add(new DoctorLine("WARN", text));
        void Good(string text) => lines.Add(new DoctorLine("OK", text));

        Info($"OS:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Info($"Install folder:   {paths.InstallDir}");
        Info($"Settings:         {paths.SettingsPath} ({(File.Exists(paths.SettingsPath) ? "found" : "will be created")})");
        Info($"Launch method:    {settings.LaunchMethod}");
        Info($"Steam root:       {env.SteamRoot ?? "not found"}");
        Info($"Steam libraries:  {(env.Libraries.Count == 0 ? "-" : string.Join(", ", env.Libraries))}");
        Info($"GTA V folder:     {env.GameDir ?? "NOT FOUND (use --game-path)"}");
        Info($"Wine prefix:      {env.Prefix ?? "not found"}");
        Info($"Proton:           {env.ProtonDir ?? "not found"}");
        Info($"GTA V documents:  {GamePatcher.DocumentsDir(env.Prefix) ?? "not found (start the game once)"}");
        if (!OperatingSystem.IsWindows())
            Info($"Launch options:   {env.SteamLaunchOptions ?? "(none)"}");

        var ok = true;

        foreach (var problem in Deployment.Check(paths, env.GameDir))
        {
            Warn(problem);
            ok = false;
        }

        if (env.GameDir == null) ok = false;

        if (!OperatingSystem.IsWindows())
        {
            if (env.Prefix == null)
            {
                Warn("No Wine prefix for GTA V found (steamapps/compatdata/271590/pfx). Start the game once through Steam so that Proton creates it, or pass --prefix.");
                ok = false;
            }

            if (env.Prefix != null)
            {
                var clr = Path.Combine(env.Prefix, "drive_c", "windows", "Microsoft.NET", "Framework64", "v4.0.30319", "clr.dll");
                if (File.Exists(clr))
                    Good(".NET Framework 4.x is installed in the prefix.");
                else
                {
                    Warn(".NET Framework 4.8 does not seem to be installed in the prefix. ScriptHookVDotNet needs it:  protontricks 271590 dotnet48   (or: WINEPREFIX=<prefix> winetricks dotnet48)");
                    ok = false;
                }
            }

            if (string.Equals(settings.LaunchMethod, "steam", StringComparison.OrdinalIgnoreCase) &&
                (env.SteamLaunchOptions == null || !env.SteamLaunchOptions.Contains("dinput8", StringComparison.OrdinalIgnoreCase)))
            {
                Warn("Set the Steam launch options of GTA V to:  WINEDLLOVERRIDES=\"dinput8=n,b\" %command%   (or use --method proton)");
                ok = false;
            }
        }

        if (env.GameDir != null && Deployment.IsDeployed(env.GameDir))
            Warn("The mod is currently deployed in the game folder (run \"restore\" to remove it).");

        if (ok) Good("Everything looks fine. Press Play.");
        return (ok, lines);
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
}

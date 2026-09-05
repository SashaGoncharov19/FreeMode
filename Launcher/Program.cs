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
  prepare <host:port>
            download the server's custom DLC packs (GET /dlcpacks.json) into <install>/dlcpacks/<name>/ and verify them
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
        string? prepareTarget = null;
        string? gamePath = null, method = null, steamPath = null, protonPath = null, prefixPath = null, installDir = null;
        bool keepAsi = false, noOffline = false, noWait = false, save = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new LauncherException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "run": case "deploy": case "restore": case "doctor": command = args[i]; break;
                case "prepare": command = "prepare"; prepareTarget = Next(); break;
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

        var settings = SettingsStore.Load(paths.SettingsPath);
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
            SettingsStore.Save(paths.SettingsPath, settings);
        }

        switch (command)
        {
            case "doctor":
            {
                var (ok, lines) = LaunchSession.Doctor(paths, settings, env);
                Console.WriteLine();
                foreach (var line in lines)
                {
                    if (line.Level == "INFO") Console.WriteLine(line.Text);
                    else if (line.Level == "OK") Log.Ok(line.Text);
                    else Log.Warn(line.Text);
                }
                if (ok) Log.Ok("Run \"GTANetwork.Launcher\" to play.");
                return ok ? 0 : 1;
            }

            case "restore":
                Deployment.Restore(LaunchSession.RequireGame(env));
                return 0;

            case "prepare":
            {
                var target = prepareTarget ?? throw new LauncherException("prepare needs <host:port>");
                var colon = target.LastIndexOf(':');
                var host = colon > 0 ? target.Substring(0, colon) : target;
                var port = colon > 0 && int.TryParse(target.Substring(colon + 1), out var p) ? p : 4499;
                var result = DlcPacks.PrepareAsync(paths, host, port, line => Log.Info(line)).GetAwaiter().GetResult();
                if (result.Ok) Log.Ok(result.Packs.Count == 0 ? "Nothing to prepare." : $"{result.Downloaded.Count} downloaded, {result.UpToDate.Count} up to date, in {paths.DlcPacksDir}");
                else Log.Error($"{result.Failed.Count} pack(s) failed: {string.Join("; ", result.Failed.ConvertAll(f => f.Name + " - " + f.Error))}");
                return result.Ok ? 0 : 1;
            }

            case "deploy":
                Deployment.Deploy(paths, LaunchSession.RequireGame(env), settings.DisableOtherAsiPlugins, settings.ScOfflineOnly);
                GamePatcher.Patch(GamePatcher.DocumentsDir(env.Prefix));
                return 0;

            default:
            {
                using var cancel = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
                return LaunchSession.Play(paths, settings, env, _debug, noWait, cancel.Token);
            }
        }
    }
}

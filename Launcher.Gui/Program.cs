using Avalonia;
using Avalonia.Headless;
using GTANetwork.Launcher.Gui.ViewModels;
using GTANetwork.Launcher.Gui.Views;

namespace GTANetwork.Launcher.Gui;

internal static class Program
{
    /// <summary>The install folder: --install-dir, GTAN_INSTALL_DIR, or the folder of this executable (or its parent when it sits in gui/).</summary>
    internal static string InstallDir = ResolveInstallDir(Array.Empty<string>());

    [STAThread]
    public static int Main(string[] args)
    {
        InstallDir = ResolveInstallDir(args);
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("GTANetwork.Launcher.Gui [--install-dir <dir>] [--self-test]\n  the GTA Network launcher window; --self-test builds it headless and exits (CI)");
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Builds the window without a display (Avalonia.Headless), loads the status and the settings, exits 0 when everything came up.</summary>
    private static int SelfTest()
    {
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
            var result = session.Dispatch(() =>
            {
                var vm = new MainViewModel(InstallDir);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                vm.RefreshStatus();
                vm.RefreshLogs();
                var ok = window.IsVisible && vm.StatusLines.Count > 0 && vm.LaunchMethods.Count == 3;
                Console.WriteLine("self-test: window " + (window.IsVisible ? "shown" : "NOT shown") + ", " + vm.StatusLines.Count + " status line(s), settings for " + vm.Paths.SettingsPath + ", " + vm.LogFiles.Count + " log file(s)");
                window.Close();
                return ok;
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(result ? "self-test OK" : "self-test FAILED");
            return result ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("self-test FAILED: " + ex);
            return 1;
        }
    }

    private static string ResolveInstallDir(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++) if (args[i] == "--install-dir") return args[i + 1];
        var env = Environment.GetEnvironmentVariable("GTAN_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // published into <install>/gui/: the install is the parent
        if (string.Equals(Path.GetFileName(dir), "gui", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "settings.xml")))
            return Path.GetDirectoryName(dir)!;
        return dir;
    }
}

/// <summary>The app as the headless session builds it (--self-test).</summary>
internal static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

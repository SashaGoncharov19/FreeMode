using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GTANetworkShared;

namespace GTANetwork.Launcher.Gui.ViewModels;

/// <summary>One line of the status list: Level is INFO, OK or WARN (the view colours WARN).</summary>
public sealed record StatusLine(string Level, string Text)
{
    public bool IsWarning => Level == "WARN";
    public bool IsOk => Level == "OK";
}

/// <summary>
/// The window's state: Home (the doctor's status lines, Play/Stop, the log), Settings (bound to settings.xml through
/// PlayerSettings), Logs (the files in logs/). Play runs Launcher.Core's LaunchSession on a background thread; the
/// launcher's log lines arrive through Log.Written and are shown as they come.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public Paths Paths { get; }
    private PlayerSettings _settings;
    private DetectedEnvironment? _env;
    private CancellationTokenSource? _playCancel;
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _logTimer;

    public MainViewModel(string installDir)
    {
        Paths = new Paths(installDir);
        Log.UseFile(Path.Combine(Paths.LogsDir, "launcher.log"));
        _settings = SettingsStore.Load(Paths.SettingsPath);
        LoadSettingsFields();
        Log.Written += OnLogWritten;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) => { if (IsLogs && SelectedLogFile != null) LoadLogTail(); };
        _logTimer.Start();
    }

    // ---- navigation ----

    [ObservableProperty] private string _section = "home";
    public bool IsHome => Section == "home";
    public bool IsSettings => Section == "settings";
    public bool IsLogs => Section == "logs";

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsLogs));
        if (IsLogs) RefreshLogs();
    }

    [RelayCommand] private void ShowHome() => Section = "home";
    [RelayCommand] private void ShowSettings() => Section = "settings";
    [RelayCommand] private void ShowLogs() => Section = "logs";

    // ---- home: status, play ----

    public ObservableCollection<StatusLine> StatusLines { get; } = new();
    [ObservableProperty] private string _statusSummary = "";
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _debug;
    [ObservableProperty] private string _logText = "";
    public string Version => "GTAN " + ParseableVersion.FromAssembly(typeof(MainViewModel).Assembly);
    public bool CanPlay => Ready && !IsPlaying;

    partial void OnReadyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    /// <summary>Detects the machine again and rebuilds the status list (the doctor of the command line launcher).</summary>
    [RelayCommand]
    public void RefreshStatus()
    {
        try
        {
            _settings = SettingsStore.Load(Paths.SettingsPath);
            LoadSettingsFields();
            _env = DetectedEnvironment.Detect(Paths, _settings);
            var (ok, lines) = LaunchSession.Doctor(Paths, _settings, _env);
            StatusLines.Clear();
            foreach (var line in lines) StatusLines.Add(new StatusLine(line.Level, line.Text));
            Ready = ok || _env.GameDir != null; // warnings do not block Play; a missing game does
            StatusSummary = _env.GameDir == null ? "GTA V was not found: set the game folder in Settings." : ok ? "Ready to play." : "Ready, with warnings (see below).";
        }
        catch (Exception ex)
        {
            StatusLines.Clear();
            StatusLines.Add(new StatusLine("WARN", ex.Message));
            StatusSummary = "Detection failed.";
            Ready = false;
        }
    }

    /// <summary>Deploy, start the game, wait for it to exit, restore: on a background thread; Stop cancels the wait and restores.</summary>
    [RelayCommand]
    private async Task Play()
    {
        if (IsPlaying || _env == null) return;
        IsPlaying = true;
        _playCancel = new CancellationTokenSource();
        var token = _playCancel.Token;
        var settings = _settings;
        var env = _env;
        var debug = Debug;
        try
        {
            var code = await Task.Run(() => LaunchSession.Play(Paths, settings, env, debug, noWait: false, token), CancellationToken.None);
            Log.Info(code == 0 ? "Session finished." : "Session finished with code " + code + ".");
        }
        catch (LauncherException ex)
        {
            Log.Error(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
        }
        finally
        {
            IsPlaying = false;
            _playCancel.Dispose();
            _playCancel = null;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _playCancel?.Cancel();
    }

    private void OnLogWritten(string level, string message)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + level + "] " + message;
        Dispatcher.UIThread.Post(() =>
        {
            _log.AppendLine(line);
            if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
            LogText = _log.ToString();
        });
    }

    // ---- settings (settings.xml, PlayerSettings) ----

    public ObservableCollection<string> LaunchMethods { get; } = new() { "steam", "proton", "direct" };
    [ObservableProperty] private string _launchMethod = "steam";
    [ObservableProperty] private string _gamePath = "";
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private string _protonPath = "";
    [ObservableProperty] private string _protonPrefixPath = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _masterServerAddress = "";
    [ObservableProperty] private bool _scOfflineOnly;
    [ObservableProperty] private bool _disableOtherAsiPlugins;
    [ObservableProperty] private bool _debugMode;
    [ObservableProperty] private bool _showFps;
    [ObservableProperty] private bool _cefGpu;
    [ObservableProperty] private bool _cefLoader;
    [ObservableProperty] private bool _cefMenu;
    [ObservableProperty] private bool _cefPreload;
    [ObservableProperty] private string _cefFrameRate = "60";
    [ObservableProperty] private string _cefIdleExitSeconds = "60";
    [ObservableProperty] private string _settingsStatus = "";

    private void LoadSettingsFields()
    {
        var s = _settings;
        LaunchMethod = LaunchMethods.Contains(s.LaunchMethod ?? "") ? s.LaunchMethod! : "steam";
        GamePath = s.GamePath ?? "";
        SteamPath = s.SteamPath ?? "";
        ProtonPath = s.ProtonPath ?? "";
        ProtonPrefixPath = s.ProtonPrefixPath ?? "";
        DisplayName = s.DisplayName ?? "";
        MasterServerAddress = s.MasterServerAddress ?? "";
        ScOfflineOnly = s.ScOfflineOnly;
        DisableOtherAsiPlugins = s.DisableOtherAsiPlugins;
        DebugMode = s.DebugMode;
        ShowFps = s.ShowFPS;
        CefGpu = s.CefGpu;
        CefLoader = s.CefLoader;
        CefMenu = s.CefMenu;
        CefPreload = s.CefPreload;
        CefFrameRate = s.CefFrameRate.ToString();
        CefIdleExitSeconds = s.CefIdleExitSeconds.ToString();
    }

    /// <summary>Writes the form into settings.xml (the in-game client reads the same file) and detects the machine again.</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        var s = _settings;
        s.LaunchMethod = LaunchMethod;
        s.GamePath = GamePath.Trim();
        s.SteamPath = SteamPath.Trim();
        s.ProtonPath = ProtonPath.Trim();
        s.ProtonPrefixPath = ProtonPrefixPath.Trim();
        s.DisplayName = DisplayName.Trim();
        s.MasterServerAddress = MasterServerAddress.Trim();
        s.ScOfflineOnly = ScOfflineOnly;
        s.DisableOtherAsiPlugins = DisableOtherAsiPlugins;
        s.DebugMode = DebugMode;
        s.ShowFPS = ShowFps;
        s.CefGpu = CefGpu;
        s.CefLoader = CefLoader;
        s.CefMenu = CefMenu;
        s.CefPreload = CefPreload;
        if (int.TryParse(CefFrameRate, out var fps)) s.CefFrameRate = Math.Clamp(fps, 1, 60);
        if (int.TryParse(CefIdleExitSeconds, out var idle)) s.CefIdleExitSeconds = Math.Max(0, idle);
        var saved = SettingsStore.Save(Paths.SettingsPath, s);
        SettingsStatus = saved ? "Saved to " + Paths.SettingsPath + " at " + DateTime.Now.ToString("HH:mm:ss") : "Could not save " + Paths.SettingsPath + " (see the log).";
        RefreshStatus();
    }

    // ---- logs ----

    public ObservableCollection<string> LogFiles { get; } = new();
    [ObservableProperty] private string? _selectedLogFile;
    [ObservableProperty] private string _logTail = "";

    partial void OnSelectedLogFileChanged(string? value) => LoadLogTail();

    [RelayCommand]
    public void RefreshLogs()
    {
        var selected = SelectedLogFile;
        LogFiles.Clear();
        try
        {
            if (Directory.Exists(Paths.LogsDir))
                foreach (var file in Directory.EnumerateFiles(Paths.LogsDir, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    LogFiles.Add(Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            LogTail = ex.Message;
        }
        SelectedLogFile = selected != null && LogFiles.Contains(selected) ? selected : LogFiles.FirstOrDefault(f => f.Equals("Runtime.log", StringComparison.OrdinalIgnoreCase)) ?? LogFiles.FirstOrDefault();
    }

    private void LoadLogTail()
    {
        if (SelectedLogFile == null) { LogTail = ""; return; }
        try
        {
            var path = Path.Combine(Paths.LogsDir, SelectedLogFile);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 64 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            LogTail = (start > 0 ? "… " : "") + string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 300)));
        }
        catch (Exception ex)
        {
            LogTail = ex.Message;
        }
    }
}

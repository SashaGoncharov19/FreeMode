namespace GTANetwork.Launcher;

public sealed class LauncherException : Exception
{
    public LauncherException(string message) : base(message) { }
}

/// <summary>Layout of a GTA Network installation folder.</summary>
public sealed class Paths
{
    public Paths(string installDir)
    {
        InstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string InstallDir { get; }
    public string BinDir => Path.Combine(InstallDir, "bin");
    public string ScriptsDir => Path.Combine(BinDir, "scripts");
    public string CefDir => Path.Combine(InstallDir, "cef");
    public string ImagesDir => Path.Combine(InstallDir, "images");
    public string LogsDir => Path.Combine(InstallDir, "logs");
    public string SettingsPath => Path.Combine(InstallDir, "settings.xml");

    /// <summary>Path as the (Wine) game process will see it. Proton maps the Linux root to Z:.</summary>
    public static string ToWindowsPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return full;
        return "Z:" + full.Replace('/', '\\');
    }
}

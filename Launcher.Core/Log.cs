namespace GTANetwork.Launcher;

public static class Log
{
    private static readonly object Sync = new();
    private static string? _file;

    public static void UseFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = path;
            File.AppendAllText(_file, $"{Environment.NewLine}==== GTA Network launcher started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
        }
        catch
        {
            _file = null;
        }
    }

    /// <summary>Every line as it is written (level: INFO, OK, WARN, ERR); the GUI shows them. Raised on the writing thread.</summary>
    public static event Action<string, string>? Written;

    public static void Info(string message) => Write("INFO", message, ConsoleColor.Gray);
    public static void Ok(string message) => Write(" OK ", message, ConsoleColor.Green);
    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERR ", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        lock (Sync)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{level}] {message}");
            Console.ForegroundColor = previous;

            try { Written?.Invoke(level.Trim(), message); } catch { }

            if (_file == null) return;
            try
            {
                File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss}] [{level.Trim()}] {message}{Environment.NewLine}");
            }
            catch
            {
                // ignored
            }
        }
    }
}

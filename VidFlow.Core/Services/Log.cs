namespace VidFlow.Services;

/// <summary>Minimal daily-rolling file log under %LocalAppData%\VidFlow\logs. Keeps 7 days.</summary>
public static class Log
{
    static readonly object Gate = new();
    static bool _pruned;

    public static string Dir => Path.Combine(AppSettings.DataDir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (!_pruned)
                {
                    _pruned = true;
                    foreach (var old in new DirectoryInfo(Dir).GetFiles("vidflow-*.log")
                                 .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
                        old.Delete();
                }
                File.AppendAllText(Path.Combine(Dir, $"vidflow-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

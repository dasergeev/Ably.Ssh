namespace WpfApp;

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);

public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static readonly List<LogEntry> Entries = [];

    public static event Action<LogEntry>? EntryAdded;

    public static IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (SyncRoot)
        {
            return Entries.ToArray();
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

    public static void Exception(string context, Exception exception)
    {
        Log("EXCEPTION", $"{context}{Environment.NewLine}{exception}");
    }

    private static void Log(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        lock (SyncRoot)
        {
            Entries.Add(entry);
        }

        EntryAdded?.Invoke(entry);
    }
}

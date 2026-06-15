namespace Bmt.LoadGen;

/// <summary>Minimal timestamped console logger for the loadgen CLI (mirrors seeder/preflight).</summary>
internal static class ConsoleLog
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{level}] {message}");
        }
    }
}

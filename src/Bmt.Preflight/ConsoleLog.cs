namespace Bmt.Preflight;

/// <summary>Minimal timestamped console logger for the preflight CLI (mirrors the seeder's).</summary>
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

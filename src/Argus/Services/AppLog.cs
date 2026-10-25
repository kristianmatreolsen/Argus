using System.IO;

namespace Argus.Services;

// Small diagnostics log at %LOCALAPPDATA%\Argus\argus.log. Sensor and device failures are otherwise
// swallowed (one failing sensor must not break the dashboard), so this is where they become visible.
// The file rolls over to argus.old.log at 1 MB.
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Sync = new();
    private static readonly HashSet<string> ReportedKeys = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus");

    public static string FilePath { get; } = Path.Combine(Folder, "argus.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", Describe(message, exception));

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    // For failures that repeat on every refresh: only the first occurrence per key is written.
    public static void WarnOnce(string key, string message, Exception? exception = null)
    {
        lock (Sync)
        {
            if (!ReportedKeys.Add(key))
            {
                return;
            }
        }

        Warn(message, exception);
    }

    private static string Describe(string message, Exception? exception) =>
        exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}";

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, Path.Combine(Folder, "argus.old.log"), overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}

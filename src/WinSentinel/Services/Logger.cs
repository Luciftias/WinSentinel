using System.IO;

namespace WinSentinel.Services;

/// <summary>
/// Tiny append-only logger used for crash reports and rare diagnostics. Writes to
/// %AppData%\WinSentinel\winsentinel.log, trims itself when the file grows past 512 KB and
/// never throws — logging must not take the app down.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSentinel");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "winsentinel.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    public static void Error(string context, string message) => Write("ERROR", $"{context}: {message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                Trim();
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }

    private static void Trim()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 512 * 1024)
                File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
            // ignore
        }
    }
}

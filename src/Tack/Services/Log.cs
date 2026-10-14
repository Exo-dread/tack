using System.IO;

namespace Tack.Services;

/// <summary>A tiny append-only diagnostics log (tack.log next to the data file). Never leaves the machine.</summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string folder) => _path = Path.Combine(folder, "tack.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

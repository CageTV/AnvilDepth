using System;
using System.IO;

namespace AnvilDepth.Services;

/// <summary>
/// Minimal file logger for diagnosing crashes that never reach a .NET exception handler at all —
/// e.g. a native access violation from disposing an ONNX InferenceSession while another thread is
/// still calling Run() on it. In that case AppDomain.UnhandledException never fires, so the ONLY
/// evidence of what happened is whatever was already written to disk before the crash. That's why
/// every write here goes straight to disk (File.AppendAllText, not a buffered StreamWriter) rather
/// than being batched — a log that's still sitting in a buffer when the process dies is no better
/// than no log at all.
///
/// Log location: %AppData%\AnvilDepth\logs\anvildepth.log (log folder, not Program Files, so it's
/// writable without admin rights regardless of where the app is installed).
/// </summary>
public static class Logger
{
    private static readonly object _fileLock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnvilDepth", "logs", "anvildepth.log");

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Trim to the last ~2000 lines on startup so the file doesn't grow forever across
            // many sessions — keep enough history to see the lead-up to a crash without keeping
            // months of it.
            if (File.Exists(LogPath))
            {
                var lines = File.ReadAllLines(LogPath);
                if (lines.Length > 2000)
                    File.WriteAllLines(LogPath, lines[^2000..]);
            }
            File.AppendAllText(LogPath, $"{Environment.NewLine}--- AnvilDepth started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself be a reason the app fails to start.
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Swallow — a failed log write must never crash or interrupt the caller.
        }
    }

    public static void LogException(string context, Exception ex)
    {
        Log($"EXCEPTION in {context}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
    }

    public static string LogFolderPath => Path.GetDirectoryName(LogPath)!;
}

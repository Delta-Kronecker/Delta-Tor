using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Dedicated logger for the TUN mechanism that writes detailed diagnostic trace
/// lines directly to <c>tunlog.txt</c> in the app data root (<see cref="AppPaths.Root"/>).
/// </summary>
public static class TunLog
{
    private static readonly object SyncLock = new();
    private static bool _initialized;

    public static string LogPath => Path.Combine(AppPaths.Root, "tunlog.txt");

    /// <summary>
    /// Append a log line to <c>tunlog.txt</c> with high-precision timestamp.
    /// Thread-safe and resilient against filesystem errors.
    /// </summary>
    public static void Write(string tag, string message)
    {
        lock (SyncLock)
        {
            try
            {
                if (!_initialized)
                {
                    _initialized = true;
                    var header = $"\n=== TUN SESSION STARTED at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n";
                    File.AppendAllText(LogPath, header, Encoding.UTF8);
                }

                var timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"[{timeStr}] [{tag}] {message}\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never throw or disrupt tunnel operations
            }
        }
    }

    /// <summary>Write an exception and its full stack trace to <c>tunlog.txt</c>.</summary>
    public static void WriteEx(string tag, string message, Exception ex)
    {
        Write(tag, $"{message}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
    }
}

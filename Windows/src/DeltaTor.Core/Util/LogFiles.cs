using System.Globalization;
using System.Text;

namespace DeltaTor.Core.Util;

/// <summary>
/// On-disk connection log, the replacement for the old in-app log screen.
///
/// Lines are grouped by the same categories the log screen used to filter on —
/// the raced transports (<see cref="AppLog.Transports"/>) plus "shared" for
/// lines that belong to no single transport — so the folder reads like the old
/// transport picker: logs/vanilla, logs/obfs4, …, logs/shared. Inside each
/// category a file per day keeps a long-running install readable without one
/// giant file.
///
/// Recording is gated by <see cref="AppLog.Enabled"/> before this class is
/// called, so the toggle being off means nothing touches the disk. Writers stay
/// open for the life of the process (append, shared for reading) and flush every
/// line: a crash still leaves the last line that reached the disk.
/// </summary>
public static class LogFiles
{
    /// <summary>Category for lines that do not come from one transport.</summary>
    public const string SharedCategory = "shared";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.Ordinal);
    private static string _day = "";

    /// <summary>The folder the recorded logs live in (<see cref="AppPaths.LogsDir"/>).</summary>
    public static string Root => AppPaths.LogsDir;

    /// <summary>
    /// Append one line to its category's daily file. Never throws: diagnostics
    /// must not be able to break a connection.
    /// </summary>
    public static void Write(string? transport, int session, char level, string tag, string msg)
    {
        lock (Sync)
        {
            try
            {
                var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != _day)
                {
                    CloseLocked();
                    _day = day;
                }

                var writer = WriterFor(string.IsNullOrEmpty(transport) ? SharedCategory : transport);
                var time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var sessionTag = session > 0 ? $" [s{session}]" : "";
                writer.WriteLine($"{time}{sessionTag} {level}/{tag}: {msg}");
            }
            catch
            {
                // Logging is best-effort; filesystem errors never propagate.
            }
        }
    }

    /// <summary>Flush and close every open file (used on shutdown).</summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            try
            {
                CloseLocked();
            }
            catch
            {
                // Nothing useful to do while exiting.
            }
        }
    }

    private static StreamWriter WriterFor(string category)
    {
        if (Writers.TryGetValue(category, out var existing)) return existing;

        var dir = Path.Combine(Root, category);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{_day}.log");
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            // A crash should not cost the last line that was produced.
            AutoFlush = true
        };
        Writers[category] = writer;
        return writer;
    }

    private static void CloseLocked()
    {
        foreach (var writer in Writers.Values)
        {
            try
            {
                writer.Dispose();
            }
            catch
            {
                // Closing a broken handle must not stop the others.
            }
        }
        Writers.Clear();
    }
}
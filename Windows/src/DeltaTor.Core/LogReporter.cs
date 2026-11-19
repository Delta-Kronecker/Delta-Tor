using System.IO.Compression;
using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>Why a report is being offered.</summary>
public enum ErrorKind
{
    Connection,
    Crash
}

/// <summary>
/// One error the user is asked to send a log for. <see cref="Message"/> is the
/// failure the app already showed (a refused connect, say); <see cref="Crash"/>
/// is the stack trace of a crash from a previous run that was persisted before
/// the process died.
/// </summary>
public sealed record ErrorReport(ErrorKind Kind, string? Message, string? Crash);

/// <summary>
/// Turns an error into a "send the log" prompt.
///
/// The dialog itself lives in the app layer; this object only announces what to
/// ask about and builds the archive. Two triggers feed it:
///
///  - a connection failure, reported the moment the engine gives up;
///  - a crash. The process that crashed cannot show a dialog, so the throwable
///    and the recent log are written to a file first and turned into a report on
///    the next launch, when there is a window to show it in.
///
/// The archive is built next to the app. Nothing is uploaded by the app:
/// Telegram is opened with the file attached and the user sends it themselves,
/// so no bot token and no secret live in the binary.
/// </summary>
public static class LogReporter
{
    private const string TAG = "LogReporter";

    /// <summary>The bot the user is asked to send the archive to.</summary>
    public const string BotName = "@DeltaKronecker_Bot";

    public const string BotUrl = "https://t.me/DeltaKronecker_Bot";

    /// <summary>Raised with a new report. Fires on the reporting thread.</summary>
    public static event Action<ErrorReport>? Raised;

    public static void ReportConnection(string message) =>
        Raise(new ErrorReport(ErrorKind.Connection, message, null));

    /// <summary>
    /// A crash the app can still ask about now (a handled UI-thread exception):
    /// raise it without persisting, because the process lives on and the dialog
    /// is about to be shown.
    /// </summary>
    public static void ReportCrash(Exception? error, string origin)
    {
        // Before the main window subscribes (startup) there is no dialog to
        // raise, so persist it and let the next launch ask instead of losing it.
        if (Raised is null)
        {
            WriteCrashFile(error, origin);
            return;
        }
        Raise(new ErrorReport(ErrorKind.Crash, null, CrashText(error, origin)));
    }

    private static void Raise(ErrorReport report)
    {
        AppLog.I(TAG, $"error report raised: {report.Kind} · {report.Message}");
        Raised?.Invoke(report);
    }

    /// <summary>
    /// Turn a crash that killed the previous run into a report, if there is one.
    /// Reads and deletes the persisted file so a single crash is asked about
    /// once, not on every launch from then on.
    /// </summary>
    public static ErrorReport? ConsumePendingCrash()
    {
        try
        {
            if (!File.Exists(AppPaths.PendingCrashFile)) return null;
            var text = File.ReadAllText(AppPaths.PendingCrashFile);
            File.Delete(AppPaths.PendingCrashFile);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new ErrorReport(ErrorKind.Crash, null, text);
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"crash file read failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persist a crashing run before the process dies. Best effort: the process
    /// is already going, and a failed write must not change how it ends.
    /// </summary>
    public static void WriteCrashFile(Exception? error, string origin)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.PendingCrashFile, CrashText(error, origin));
        }
        catch
        {
            // Nothing useful to do while crashing.
        }
    }

    private static string CrashText(Exception? error, string origin) => new StringBuilder()
        .AppendLine("DeltaTor crash")
        .AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        .AppendLine($"origin: {origin}")
        .AppendLine()
        .AppendLine("----- stack trace -----")
        .AppendLine(error?.ToString() ?? "unknown")
        .AppendLine("----- recent log -----")
        .AppendLine(AppLog.DumpReport())
        .ToString();

    /// <summary>
    /// Build the archive: the recent log, a small system header, the recorded
    /// log files when recording was on, and the error/crash text when there is
    /// one. Returns null when the file could not be written. Previous exports
    /// are cleared first so they never accumulate.
    /// </summary>
    public static string? BuildZip(ErrorReport report)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ExportDir);
            // Clear only previous log exports: the bridge export shares this
            // folder and is not ours to delete.
            foreach (var old in Directory.EnumerateFiles(
                AppPaths.ExportDir, "deltator-log-*.zip"))
            {
                try { File.Delete(old); } catch { /* keep going */ }
            }

            var zip = Path.Combine(
                AppPaths.ExportDir, $"deltator-log-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddText(archive, "system.txt", SystemInfo(report));

                var log = AppLog.DumpReport();
                if (log.Length > 0) AddText(archive, "connection-log.txt", log);

                if (Directory.Exists(AppPaths.LogsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(
                        AppPaths.LogsDir, "*.log", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(AppPaths.LogsDir, file)
                            .Replace('\\', '/');
                        var entry = archive.CreateEntry($"logs/{rel}", CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        // LogFiles keeps its writers open for the life of the
                        // process; share reads so reading a live file cannot
                        // fail with a sharing violation.
                        using var source = new FileStream(
                            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        source.CopyTo(entryStream);
                    }
                }

                if (!string.IsNullOrWhiteSpace(report.Message))
                    AddText(archive, "error.txt", report.Message + "\n");
                if (!string.IsNullOrWhiteSpace(report.Crash))
                    AddText(archive, "crash.txt", report.Crash);
            }

            AppLog.I(TAG, $"report zip: {zip}");
            return zip;
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"report zip failed: {e.Message}");
            return null;
        }
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string SystemInfo(ErrorReport report)
    {
        var sb = new StringBuilder()
            .AppendLine("DeltaTor connection log")
            .AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"app: {AppVersion()}")
            .AppendLine($"os: {Environment.OSVersion.VersionString}")
            .AppendLine($"machine: {Environment.MachineName}")
            .AppendLine($"kind: {report.Kind}");
        if (!string.IsNullOrWhiteSpace(report.Message))
            sb.AppendLine($"error: {report.Message}");
        sb.AppendLine($"recording: {(AppLog.Enabled ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine($"Send this archive to {BotName}.");
        return sb.ToString();
    }

    private static string AppVersion()
    {
        try
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                    ?? typeof(LogReporter).Assembly.GetName().Version;
            return v?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }
}

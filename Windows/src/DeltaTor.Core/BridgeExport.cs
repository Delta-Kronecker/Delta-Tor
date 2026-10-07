using System.Globalization;
using System.IO.Compression;
using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Packs every bridge the app is holding into one zip and hands it to the user.
///
/// One file per list rather than a single blob, because the point of a backup
/// is being usable somewhere else: every txt file under bridges/ is a plain
/// Tor bridge file that can be dropped straight into Tor Browser. The
/// remembered bridges (the txt files under memory/) are exported as bridge
/// lines too, not as fingerprints, so the proven set is portable and not just
/// a list of hashes that mean nothing outside this app.
///
/// The archive is rebuilt in the export directory and the previous export is
/// cleared, so an export can never accumulate across shares.
/// </summary>
public static class BridgeExport
{
    private const string TAG = "BridgeExport";

    /// <summary>Written as `bridges/<file>`, in this order.</summary>
    private static readonly (string Transport, string File)[] Lists =
    {
        (ParallelTorManager.TransportVanilla, "vanilla.txt"),
        (ParallelTorManager.TransportObfs4, "obfs4.txt"),
        (ParallelTorManager.TransportWebtunnel, "webtunnel.txt"),
        (ParallelTorManager.TransportSnowflake, "snowflake.txt"),
        (ParallelTorManager.TransportFresh, "fresh.txt"),
        (ParallelTorManager.TransportCombined, "combined.txt")
    };

    /// <summary>
    /// Written as `memory/<file>`. `auto-mixed.txt` is the pool the auto mode
    /// races; the rest are the per-mode twins.
    /// </summary>
    private static readonly (string Transport, string File)[] Memory =
    {
        (ParallelTorManager.TransportVanilla, "vanilla.txt"),
        (ParallelTorManager.TransportObfs4, "obfs4.txt"),
        (ParallelTorManager.TransportWebtunnel, "webtunnel.txt"),
        (ParallelTorManager.TransportSnowflake, "snowflake.txt"),
        (ParallelTorManager.TransportFresh, "fresh.txt"),
        (ParallelTorManager.TransportCombined, "combined.txt"),
        (ParallelTorManager.TransportMemory, "auto-mixed.txt")
    };

    /// <summary>
    /// Build the zip. Returns null when there is nothing in it: a backup of
    /// zero bridges is not a backup, and offering an empty archive only looks
    /// broken.
    /// </summary>
    public static string? Build()
    {
        var cached = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ParallelTorManager.BRIDGE_SOURCES.Keys)
        {
            if (BridgeStore.Lines(name) is { } content) cached[name] = content;
        }

        // Combined keeps no cache of its own, so it is rebuilt here for the
        // same reason it is rebuilt on connect: its value is that it can be
        // merged.
        var combined = ParallelTorManager.MergeBridgeLists(
            ParallelTorManager.CombinedSources
                .Where(cached.ContainsKey)
                .Select(s => cached[s])
                .ToList());
        if (!string.IsNullOrEmpty(combined)) cached[ParallelTorManager.TransportCombined] = combined;

        var written = new List<string>();
        var dir = AppPaths.ExportDir;
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(file); }
            catch { /* a locked previous export must not block a new one */ }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(dir, $"deltator-bridges-{stamp}.zip");

        var total = 0;
        using (var stream = File.Create(zipPath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, file) in Lists)
            {
                if (!cached.TryGetValue(name, out var content)) continue;
                var body = ParallelTorManager.SplitLines(content)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                if (body.Count == 0) continue;
                WriteEntry(zip, $"bridges/{file}", string.Join("\n", body));
                written.Add($"bridges/{file} ({body.Count})");
                if (name != ParallelTorManager.TransportCombined) total += body.Count;
            }

            // The memory pools are stored as fingerprints only, so the lines
            // have to be looked up in the lists again. A remembered bridge
            // whose list has since been replaced is simply dropped: its
            // fingerprint alone is not something Tor could use.
            foreach (var (name, file) in Memory)
            {
                var pool = BridgeMemory.Count(name);
                if (pool == 0) continue;
                var lines = BridgeMemory.BridgeLinesFor(cached, name);
                if (lines == null) continue;
                var body = ParallelTorManager.SplitLines(lines)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                if (body.Count == 0) continue;
                WriteEntry(zip, $"memory/{file}", string.Join("\n", body));
                written.Add($"memory/{file} ({body.Count} of {pool} remembered)");
            }

            WriteEntry(zip, "README.txt", Readme(written, total));
        }

        AppLog.I(TAG, $"export: {Path.GetFileName(zipPath)}, {written.Count} file(s)");
        if (written.Count == 0)
        {
            AppLog.W(TAG, "nothing to export: no cached bridges and no remembered ones");
            try { File.Delete(zipPath); }
            catch { /* nothing useful to keep anyway */ }
            return null;
        }
        return zipPath;
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        var bytes = Utf8NoBom.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Readme(IReadOnlyList<string> written, int total)
    {
        var sb = new StringBuilder();
        sb.Append("Delta Tor bridge export\n");
        sb.Append("Every bridge list the app is holding, one file each.\n");
        sb.Append('\n');
        sb.Append("bridges/ -- the lists Tor races, exactly as used.\n");
        sb.Append("memory/  -- the bridges that already worked, as bridge lines.\n");
        sb.Append("            These are the ones a reconnect starts from.\n");
        sb.Append("            A remembered bridge whose list has since changed is\n");
        sb.Append("            left out: only its fingerprint was stored, and a\n");
        sb.Append("            fingerprint is not something Tor can connect to.\n");
        sb.Append('\n');
        sb.Append("Files in this archive:\n");
        foreach (var line in written) sb.Append($"  {line}\n");
        sb.Append('\n');
        sb.Append("Any bridges/*.txt file can be added to Tor Browser as-is.\n");
        sb.Append($"{total} bridge(s) across the cached lists.\n");
        return sb.ToString();
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
}

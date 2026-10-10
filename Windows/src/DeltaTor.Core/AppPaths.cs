namespace DeltaTor.Core;

/// <summary>
/// Where the app keeps its data: the folder it was extracted to, next to
/// DeltaTor.exe. A portable app writes nothing outside its own folder — this
/// is the Windows counterpart of Android's app-private filesDir.
///
/// Everything stateful lives under <see cref="Root"/>: prefs.json,
/// torrc.template, the bridge caches, the memory pool, the route snapshot
/// and the per-runner tor data. Subfolders (bridges/, tor/, cache/) keep the
/// top level from filling up.
///
/// <see cref="MigrateLegacy"/> copies the pre-portable locations
/// (%APPDATA%\DeltaTor, %LOCALAPPDATA%\DeltaTor) in on first launch, so an
/// install that used the AppData locations keeps its settings and caches.
/// </summary>
public static class AppPaths
{
    /// <summary>Portable data root: the directory the exe runs from.</summary>
    public static readonly string Root = AppContext.BaseDirectory;

    /// <summary>Cached bridge list files (Android: filesDir/bridges).</summary>
    public static readonly string BridgesDir = Path.Combine(Root, "bridges");

    /// <summary>BridgeMemory pool file (Android: prefs file "deltator_bridge_memory").</summary>
    public static readonly string MemoryFile = Path.Combine(Root, "bridge-memory.json");

    /// <summary>InstallCounter one-shot state (Android: prefs file "deltator_install").</summary>
    public static readonly string InstallStateFile = Path.Combine(Root, "install-state.json");

    /// <summary>Bridge export zips (Android: cacheDir/exports).</summary>
    public static readonly string ExportDir = Path.Combine(Root, "cache", "exports");

    /// <summary>Per-Tor-instance data directories (stage 2, TorRunner).</summary>
    public static string TorDataDir(string runnerName)
    {
        var dir = Path.Combine(Root, "tor", runnerName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// One-time copy from the pre-portable locations into the app folder.
    /// Every item is copied only while its portable counterpart is missing,
    /// and directories merge file-by-file, so nothing the portable copy has
    /// already written is ever overwritten. Best-effort: a failed copy means
    /// a fresh start, never a failed launch.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaTor");
            CopyFileIfMissing(
                Path.Combine(roaming, "prefs.json"),
                Path.Combine(Root, "prefs.json"));

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaTor");
            if (!Directory.Exists(local)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(local))
            {
                var target = Path.Combine(Root, Path.GetFileName(entry));
                if (File.Exists(entry)) CopyFileIfMissing(entry, target);
                else if (Directory.Exists(entry)) MergeDirectory(entry, target);
            }
        }
        catch
        {
            // Migration is best-effort; the app must start either way.
        }
    }

    private static void CopyFileIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
    }

    private static void MergeDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            MergeDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}

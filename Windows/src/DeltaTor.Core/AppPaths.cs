namespace DeltaTor.Core;

/// <summary>
/// Where the app keeps its files: the <c>data</c> folder next to DeltaTor.exe.
/// A portable app writes nothing outside its own folder — this is the Windows
/// counterpart of Android's app-private filesDir.
///
/// The folder is the single movers' lever: the exe (plus the small files the
/// .NET host must read before any app code runs) sits at the root, and every
/// other shipped file — the managed assemblies, the Tor bundle, the bundled
/// assets, the Wintun data plane — lives under <c>data</c> too, so moving the
/// runtime's base directory at startup (see <c>Program.PrepareAppDirectory</c>)
/// makes the whole app follow.
///
/// Everything stateful lives under <see cref="Root"/>: prefs.json,
/// torrc.template, the bridge caches, the memory pool, the route snapshot,
/// the recorded logs and the per-runner tor data. Subfolders (bridges/, tor/,
/// cache/, logs/) keep the top level from filling up.
///
/// <see cref="MigratePortableLayout"/> moves the state an older build kept next
/// to the exe into <see cref="Root"/>, and <see cref="MigrateLegacy"/> copies
/// the pre-portable locations (%APPDATA%\DeltaTor, %LOCALAPPDATA%\DeltaTor) in
/// on first launch, so an install that used either older layout keeps its
/// settings and caches.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Portable data root: the <c>data</c> folder next to the exe. Set through
    /// APP_CONTEXT_BASE_DIRECTORY at startup, so this is just a read of it.
    /// </summary>
    public static readonly string Root = AppContext.BaseDirectory;

    /// <summary>Cached bridge list files (Android: filesDir/bridges).</summary>
    public static readonly string BridgesDir = Path.Combine(Root, "bridges");

    /// <summary>BridgeMemory pool file (Android: prefs file "deltator_bridge_memory").</summary>
    public static readonly string MemoryFile = Path.Combine(Root, "bridge-memory.json");

    /// <summary>InstallCounter one-shot state (Android: prefs file "deltator_install").</summary>
    public static readonly string InstallStateFile = Path.Combine(Root, "install-state.json");

    /// <summary>Bridge export and error-report zips (Android: cacheDir/exports).</summary>
    public static readonly string ExportDir = Path.Combine(Root, "cache", "exports");

    /// <summary>
    /// A crash report written before the process died and turned into a
    /// "send the log" prompt on the next launch (Android: filesDir/crash-pending.txt).
    /// </summary>
    public static readonly string PendingCrashFile = Path.Combine(Root, "crash-pending.txt");

    /// <summary>
    /// Recorded connection logs, one subfolder per transport category and one
    /// file per day inside each. Written only while recording is on.
    /// </summary>
    public static readonly string LogsDir = Path.Combine(Root, "logs");

    /// <summary>Per-Tor-instance data directories (stage 2, TorRunner).</summary>
    public static string TorDataDir(string runnerName)
    {
        var dir = Path.Combine(Root, "tor", runnerName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// One-time copy of the state an older build kept next to the exe into
    /// <see cref="Root"/>. Between that layout and the pre-portable AppData
    /// one, this is the third place the app has stored itself; a plain
    /// upgrade must not look like a fresh install.
    ///
    /// Only missing files are copied and directories merge file-by-file, so a
    /// fresh install (whose data/ already holds the shipped defaults) is left
    /// untouched. Runs before <see cref="MigrateLegacy"/> so the AppData copy
    /// cannot shadow the newer state that was actually next to the exe.
    /// </summary>
    public static void MigratePortableLayout()
    {
        try
        {
            // The exe sits at the root and the entry assembly next to it; using
            // its location (not ProcessPath) also covers `dotnet DeltaTor.dll`.
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(entry)) return;
            var oldRoot = Path.GetDirectoryName(entry);
            if (string.IsNullOrEmpty(oldRoot)) return;
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var name in LegacyRootFiles)
                CopyFileIfMissing(Path.Combine(oldRoot, name), Path.Combine(Root, name));
            foreach (var name in LegacyRootDirs)
            {
                var from = Path.Combine(oldRoot, name);
                if (Directory.Exists(from)) MergeDirectory(from, Path.Combine(Root, name));
            }
        }
        catch
        {
            // Migration is best-effort; the app must start either way.
        }
    }

    /// <summary>State files an older build wrote directly next to the exe.</summary>
    private static readonly string[] LegacyRootFiles =
    {
        "prefs.json",
        "bridge-memory.json",
        "install-state.json",
        "crash-pending.txt",
        "proxy_chain.json",
        "torrc.template",
        "tor_last.torrc",
        "tunlog.txt"
    };

    /// <summary>State folders an older build kept next to the exe.</summary>
    private static readonly string[] LegacyRootDirs = { "bridges", "logs", "cache", "tor" };

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

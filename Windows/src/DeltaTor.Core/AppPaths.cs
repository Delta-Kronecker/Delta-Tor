namespace DeltaTor.Core;

/// <summary>
/// Where the app keeps its private data on Windows (the counterpart of
/// Android's context.filesDir / getSharedPreferences paths).
///
/// Preferences live in Roaming (%APPDATA%\DeltaTor\prefs.json, see
/// <see cref="Config"/>); caches and pools are machine-local, like
/// filesDir.
/// </summary>
public static class AppPaths
{
    /// <summary>Machine-local data root (bridge caches, memory pool, tor data).</summary>
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaTor");

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
}

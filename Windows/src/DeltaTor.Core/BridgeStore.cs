using System.Net.Http.Headers;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Caches the bridge lists (vanilla / obfs4 / webtunnel / snowflake / fresh) on
/// disk and keeps them fresh (Windows counterpart of Android BridgeStore).
/// Lists are downloaded from the Tor-Bridges-Collector repository; a download
/// atomically replaces the cached files. Auto-update runs when no cache exists
/// yet or more than a day has passed since the last update.
/// </summary>
public static class BridgeStore
{
    private const string TAG = "BridgeStore";
    private const long UpdateIntervalMs = 24L * 60 * 60 * 1000;
    private const int ConnectTimeoutMs = 20_000;
    private const int ReadTimeoutMs = 20_000;

    /// <summary>
    /// Bumped when the shape or the names of the sources changed, so an upgrade
    /// refreshes the cache once instead of keeping a list written by older code.
    /// </summary>
    private const string KeyLastUpdate = "bridges_last_update_v4";

    private static int _updateInProgress;

    public static string Dir() => AppPaths.BridgesDir;

    public static string FileFor(string name) => Path.Combine(Dir(), name + ".txt");

    /// <summary>Cached bridge lines, or null when not cached yet (or cached empty).</summary>
    public static string? Lines(string name)
    {
        var f = FileFor(name);
        if (!File.Exists(f)) return null;
        var text = File.ReadAllText(f).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Persist bridge lines (blank content is ignored).</summary>
    public static void SaveLines(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        var target = FileFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            File.WriteAllText(target, content);
            File.Delete(tmp);
        }
    }

    private static int CountLines(string content) =>
        ParallelTorManager.SplitLines(content)
            .Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#", StringComparison.Ordinal));

    public static IReadOnlyDictionary<string, int> Stats() =>
        ParallelTorManager.BRIDGE_SOURCES.Keys.ToDictionary(
            name => name,
            name => Lines(name) is { } c ? CountLines(c) : 0,
            StringComparer.Ordinal);

    public static long LastUpdatedMillis()
    {
        var raw = Config.GetRaw(KeyLastUpdate);
        return long.TryParse(raw, out var v) ? v : 0L;
    }

    /// <summary>
    /// How many bridges Combined-Bridge covers: every cached list except fresh,
    /// counted once each.
    /// </summary>
    public static int CombinedCount()
    {
        var counts = Stats();
        return ParallelTorManager.CombinedSources.Sum(s => counts.TryGetValue(s, out var n) ? n : 0);
    }

    /// <summary>True when no bridge cache exists yet or it is older than a day.</summary>
    public static bool ShouldAutoUpdate()
    {
        if (Stats().Values.Any(v => v <= 0)) return true;
        var last = LastUpdatedMillis();
        return last == 0L || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last >= UpdateIntervalMs;
    }

    /// <summary>
    /// Download every bridge file and atomically replace the cached lists. A
    /// transport backed by several files (webtunnel) gets the merged result.
    /// </summary>
    private static IReadOnlyDictionary<string, int> UpdateInternal()
    {
        var ok = 0;
        foreach (var (name, urls) in ParallelTorManager.BRIDGE_SOURCES)
        {
            var bodies = new List<string>();
            foreach (var url in urls)
            {
                var body = DownloadText(url);
                if (!string.IsNullOrWhiteSpace(body)) bodies.Add(body);
                else AppLog.W(TAG, $"Empty download for {url}");
            }
            if (bodies.Count == 0) continue;
            var merged = ParallelTorManager.MergeBridgeLists(bodies);
            if (!string.IsNullOrWhiteSpace(merged))
            {
                SaveLines(name, merged);
                ok++;
            }
        }
        if (ok > 0)
        {
            Config.SetRaw(KeyLastUpdate, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        return Stats();
    }

    /// <summary>Push the cached stats into <see cref="AppState"/> for the UI.</summary>
    public static void RefreshState()
    {
        // Counted once: Stats() re-reads and re-counts every cached file on each
        // call, and the copy below asks for one count per transport.
        var counts = Stats();
        int Get(string transport) => counts.TryGetValue(transport, out var n) ? n : 0;
        AppState.UpdateBridge(b => b with
        {
            Updating = false,
            LastUpdateMillis = LastUpdatedMillis(),
            Vanilla = Get(ParallelTorManager.TransportVanilla),
            Obfs4 = Get(ParallelTorManager.TransportObfs4),
            Webtunnel = Get(ParallelTorManager.TransportWebtunnel),
            Snowflake = Get(ParallelTorManager.TransportSnowflake),
            Fresh = Get(ParallelTorManager.TransportFresh),
            Combined = CombinedCount(),
            Memory = BridgeMemory.CountsByTransport(),
            Error = null
        });
    }

    /// <summary>
    /// Push only the memory counts, for when a connect has just proven bridges.
    ///
    /// <see cref="RefreshState"/> re-counts every cached list, which is wasted
    /// work here: the lists did not change, only the pools did. And the pool
    /// numbers are the ones written during a connect, so reading them from the
    /// next app start onwards means the mem figure on screen sits at its old
    /// value for the whole session in which it actually grew.
    /// </summary>
    public static void RefreshMemory() =>
        AppState.UpdateBridge(b => b with { Memory = BridgeMemory.CountsByTransport() });

    /// <summary>Kick off a background bridge update; the UI observes progress via <see cref="AppState"/>.</summary>
    public static void Update()
    {
        if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0) return;
        AppState.UpdateBridge(b => b with { Updating = true, Error = null });
        Task.Run(() =>
        {
            string? failure = null;
            try
            {
                UpdateInternal();
            }
            catch (Exception e)
            {
                AppLog.E(TAG, "Bridge update failed", e);
                failure = string.IsNullOrEmpty(e.Message) ? "Update failed" : e.Message;
            }
            Interlocked.Exchange(ref _updateInProgress, 0);
            RefreshState();
            if (failure != null)
            {
                AppState.UpdateBridge(b => b with { Error = failure });
            }
        });
    }

    /// <summary>Auto-update once a day (or when no cache exists); best-effort, never throws.</summary>
    public static void AutoUpdateIfStale()
    {
        try
        {
            if (ShouldAutoUpdate())
            {
                AppLog.I(TAG, "Bridges are stale; auto-updating");
                Update();
            }
        }
        catch (Exception e)
        {
            AppLog.E(TAG, "Auto-update check failed", e);
        }
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(ReadTimeoutMs) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaTor/1.0");
        return c;
    }

    private static string DownloadText(string url)
    {
        AppLog.I(TAG, $"Downloading {url}");
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeoutMs);
            var resp = Http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
            if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            }
            AppLog.E(TAG, $"HTTP {(int)resp.StatusCode} for {url}");
            return "";
        }
        catch (Exception e)
        {
            AppLog.E(TAG, $"download failed for {url}: {e.Message}");
            return "";
        }
    }
}

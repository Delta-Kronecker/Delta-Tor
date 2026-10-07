using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Remembers the bridges that actually worked, per transport, so the next
/// connect can start from them instead of grinding through the whole list
/// again (Windows counterpart of Android BridgeMemory).
///
/// A bridge counts as healthy once Tor fetched its descriptor, which is the
/// point where the pluggable-transport handshake and the descriptor download
/// both succeeded. TorRunner extracts those fingerprints from its own log while
/// it runs; <see cref="Remember"/> stores them when the runner reaches 100%.
///
/// The pool is a union across runs and is never shrunk, because a warm restart
/// (cached microdescriptors) no longer logs new bridge descriptors — the pool
/// is what carries that knowledge forward.
/// </summary>
public static class BridgeMemory
{
    private const string TAG = "BridgeMemory";

    /// <summary>
    /// How many proven bridges one transport keeps: three hundred, which is two
    /// full attempts' worth — TorRunner is given at most a hundred and fifty
    /// lines and every attempt draws a different hundred and fifty from here.
    /// Older entries fall off the end; age is the only ranking available.
    /// </summary>
    private const int MaxPerTransport = 300;

    private const int FingerprintHexLength = 40;
    private const string KeyPrefix = "healthy_";

    private static readonly object Lock = new();
    private static Dictionary<string, string> _pool = Load();

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(AppPaths.MemoryFile))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(AppPaths.MemoryFile));
                if (data != null) return data;
            }
        }
        catch
        {
            // A corrupt pool file must not brick the app; start empty.
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.MemoryFile)!);
            var tmp = AppPaths.MemoryFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_pool, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.MemoryFile, overwrite: true);
        }
        catch
        {
            // Losing a pool write is not worth crashing over.
        }
    }

    private static string Key(string name) => KeyPrefix + name;

    /// <summary>
    /// Fingerprints remembered for one transport, in the order they were
    /// proven. Lower-cased on the way out so comparisons in the file are
    /// case-insensitive without the caller having to remember.
    /// </summary>
    public static IReadOnlyList<string> Healthy(string name)
    {
        lock (Lock)
        {
            if (!_pool.TryGetValue(Key(name), out var raw) || string.IsNullOrEmpty(raw))
                return Array.Empty<string>();
            return raw.Split(',')
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToArray();
        }
    }

    /// <summary>Every remembered fingerprint across all transports.</summary>
    public static IReadOnlyList<string> AllHealthy() =>
        Transports.SelectMany(Healthy).Distinct(StringComparer.Ordinal).ToArray();

    public static int Count(string name) => Healthy(name).Count;

    /// <summary>Every pool's size at once, for the stats.</summary>
    public static IReadOnlyDictionary<string, int> CountsByTransport() =>
        Transports.ToDictionary(t => t, Count, StringComparer.Ordinal);

    public static int CountAll() => AllHealthy().Count;

    /// <summary>
    /// Add fingerprints proven by <paramref name="name"/> to its pool, keeping
    /// the most recently proven entries. Returns how many were new to the pool.
    ///
    /// Every bridge proven this time goes to the front, whether or not it was
    /// already in there: a bridge that works again today has just been shown to
    /// work, and leaving it where it was could push it out — a bridge discarded
    /// for being old that was working an hour ago. The order is the order of
    /// proof; the tail past the ceiling falls off (least recently proven).
    /// Duplicates within one call are collapsed.
    /// </summary>
    public static int Remember(string name, IEnumerable<string> fingerprints)
    {
        var proven = fingerprints
            .Select(fp => fp.Trim().ToLowerInvariant())
            .Where(IsFingerprint)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (proven.Count == 0) return 0;

        lock (Lock)
        {
            var current = HealthyLocked(name);
            var provenSet = proven.ToHashSet(StringComparer.Ordinal);
            var currentSet = current.ToHashSet(StringComparer.Ordinal);
            var fresh = proven.Count(fp => !currentSet.Contains(fp));
            var rest = current.Where(fp => !provenSet.Contains(fp)).ToList();
            var merged = proven.Concat(rest).Take(MaxPerTransport).ToList();
            if (merged.SequenceEqual(current)) return 0;
            _pool[Key(name)] = string.Join(",", merged);
            SaveLocked();
            AppLog.I(TAG,
                $"memory[{name}]: +{fresh} new, {proven.Count - fresh} re-proven, pool={merged.Count}");
            return fresh;
        }
    }

    private static IReadOnlyList<string> HealthyLocked(string name)
    {
        if (!_pool.TryGetValue(Key(name), out var raw) || string.IsNullOrEmpty(raw))
            return Array.Empty<string>();
        return raw.Split(',')
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Forget every remembered bridge. Swept by key prefix rather than by the
    /// transports listed in <see cref="Transports"/>: a pool is written under
    /// whichever transport the runner was named after (custom mode has bridges
    /// and remembers them, but it is not a transport anyone selects).
    /// </summary>
    public static void Clear()
    {
        lock (Lock)
        {
            var stale = _pool.Keys.Where(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal)).ToList();
            foreach (var k in stale) _pool.Remove(k);
            SaveLocked();
            AppLog.I(TAG, $"memory cleared ({stale.Count} pool(s))");
        }
    }

    /// <summary>
    /// Bridge lines for a memory runner. With <paramref name="transport"/> the
    /// pool and the source list are both restricted to that one transport
    /// (what a `webtunnel-memory` twin needs); without it every remembered
    /// bridge from every transport is merged (what the auto-mode runner wants).
    /// Returns null when nothing has been proven yet, so the runner is skipped.
    /// </summary>
    public static string? BridgeLinesFor(
        IReadOnlyDictionary<string, string> sources,
        string? transport = null)
    {
        var remembered = transport != null ? Healthy(transport) : AllHealthy();
        if (remembered.Count == 0) return null;

        var order = remembered.Select((fp, i) => (fp, i))
            .ToDictionary(t => t.fp, t => t.i, StringComparer.Ordinal);
        var kept = new List<(int Rank, string Line)>();

        IEnumerable<KeyValuePair<string, string>> scan;
        if (transport != null)
        {
            scan = sources.TryGetValue(transport, out var only)
                ? new[] { new KeyValuePair<string, string>(transport, only) }
                : Array.Empty<KeyValuePair<string, string>>();
        }
        else
        {
            scan = sources;
        }

        foreach (var (_, content) in scan)
        {
            foreach (var raw in ParallelTorManager.SplitLines(content))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (transport != null && !MixedTransports.Contains(transport) &&
                    !MatchesTransport(line, transport)) continue;
                var fp = FingerprintOf(line);
                if (fp == null || !order.TryGetValue(fp, out var rank)) continue;
                kept.Add((rank, line));
            }
        }

        if (kept.Count == 0) return null;
        return string.Join("\n", kept.OrderBy(k => k.Rank).Select(k => k.Line));
    }

    /// <summary>
    /// True when <paramref name="line"/> is a bridge line for
    /// <paramref name="transport"/>. Plain `ip:port fp` lines belong to vanilla
    /// and carry no pluggable-transport prefix.
    /// </summary>
    private static bool MatchesTransport(string line, string transport)
    {
        var first = Whitespace.Split(line).FirstOrDefault(t => t.Length > 0)?.ToLowerInvariant();
        if (first == null) return false;
        return transport == ParallelTorManager.TransportVanilla
            ? !PluggablePrefixes.Contains(first)
            : first == transport.ToLowerInvariant();
    }

    /// <summary>
    /// A bridge identity digest: exactly 40 hex characters, as Tor writes it
    /// and as TorRunner requires of a line before it is allowed near torrc.
    /// Exactly, not 32 to 40 — a shorter string is not a bridge fingerprint.
    /// </summary>
    private static bool IsFingerprint(string s) =>
        s.Length == FingerprintHexLength && s.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// The identity fingerprint of a bridge line. Prefixed lines
    /// (`obfs4 &lt;ip:port&gt; &lt;fp&gt; ...`, webtunnel, snowflake) carry it
    /// as the third token; plain vanilla lines as the second. Detected by
    /// looking at the first token so every format is handled.
    /// </summary>
    public static string? FingerprintOf(string line)
    {
        var parts = Whitespace.Split(line).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return null;
        var first = parts[0].ToLowerInvariant();
        var idx = PluggablePrefixes.Contains(first) ? 2 : 1;
        if (idx >= parts.Length) return null;
        var candidate = parts[idx].ToLowerInvariant();
        return IsFingerprint(candidate) ? candidate : null;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> PluggablePrefixes = new(StringComparer.Ordinal)
        { "obfs4", "webtunnel", "snowflake", "meek_lite", "meek" };

    /// <summary>Transports that keep their own memory pool.</summary>
    public static readonly IReadOnlyList<string> Transports = new[]
    {
        ParallelTorManager.TransportVanilla,
        ParallelTorManager.TransportObfs4,
        ParallelTorManager.TransportWebtunnel,
        ParallelTorManager.TransportSnowflake,
        ParallelTorManager.TransportFresh,
        ParallelTorManager.TransportCombined,
        ParallelTorManager.TransportMemory
    };

    /// <summary>
    /// Transports whose own bridge list already mixes types. A
    /// `webtunnel-memory` twin must only carry webtunnel lines; fresh/combined
    /// have no single type to match against, so filtering their twins by name
    /// would leave them with nothing.
    /// </summary>
    private static readonly HashSet<string> MixedTransports = new(StringComparer.Ordinal)
    {
        ParallelTorManager.TransportFresh,
        ParallelTorManager.TransportCombined
    };
}

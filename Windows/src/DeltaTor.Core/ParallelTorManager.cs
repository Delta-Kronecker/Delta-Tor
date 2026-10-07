using System.Net;
using System.Net.Sockets;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Races several Tor clients against each other (Windows counterpart of the
/// Android ParallelTorManager; the race engine itself is added together with
/// TorRunner — this part holds the shared vocabulary every module needs):
///
///  - vanilla: direct (no pluggable transport) bridges
///  - obfs4: obfs4 bridges via lyrebird
///  - webtunnel: webtunnel (HTTP CONNECT over TLS) bridges via lyrebird
///  - memory: the bridges that provably worked last time, in any transport
///    (see <see cref="BridgeMemory"/>)
///
/// Bridge lists are fetched from the Tor-Bridges-Collector repository at
/// runtime (the bundled copies ship with the app as the first fallback).
/// </summary>
public static partial class ParallelTorManager
{
    private const string TAG = "ParallelTorManager";

    public const string TransportVanilla = "vanilla";
    public const string TransportObfs4 = "obfs4";
    public const string TransportWebtunnel = "webtunnel";
    public const string TransportSnowflake = "snowflake";
    public const string TransportFresh = "fresh";
    public const string TransportCombined = "combined";
    public const string TransportDirect = "direct";
    public const string TransportMemory = "memory";

    /// <summary>Selectable connect modes. <see cref="TransportMemory"/> is a runner, not a mode.</summary>
    public const string TransportAuto = "auto";
    public const string TransportCustom = "custom";

    public static readonly IReadOnlyList<string> Modes = new[]
    {
        TransportAuto,
        TransportFresh,
        TransportCombined,
        TransportVanilla,
        TransportObfs4,
        TransportWebtunnel,
        TransportSnowflake,
        TransportDirect,
        TransportCustom
    };

    /// <summary>
    /// The transports auto mode may race, and the fallback for it when the
    /// stored set is blank or stale. Deliberately not <see cref="BRIDGE_SOURCES"/>
    /// keys: fresh is in there, but auto racing it would be pointless — fresh is
    /// every list at once, just a slower copy of the others racing each other.
    /// </summary>
    public static readonly IReadOnlyList<string> AutoSources = new[]
    {
        TransportVanilla,
        TransportObfs4,
        TransportWebtunnel,
        TransportSnowflake
    };

    /// <summary>
    /// What <see cref="TransportCombined"/> is built from: vanilla, obfs4 and
    /// webtunnel. Not fresh (the small 72-hour set and a mode of its own) and
    /// not snowflake (its list is two lines of documentation placeholders, so
    /// including it adds a runner with nothing to offer and a transport lyrebird
    /// cannot serve from it). Unlike the others this is not a cached list of its
    /// own — it is resolved from the other lists at connect time and its count
    /// in the bridge stats is their sum.
    /// </summary>
    public static readonly IReadOnlyList<string> CombinedSources = new[]
    {
        TransportVanilla,
        TransportObfs4,
        TransportWebtunnel
    };

    /// <summary>
    /// Suffix of the memory twin runner. Every transport a user can select on
    /// its own also gets a `&lt;transport&gt;-memory` companion, so choosing
    /// e.g. webtunnel races the full list *and* the bridges that provably worked
    /// before, instead of leaving that knowledge unused.
    /// </summary>
    public const string MemorySuffix = "-memory";

    /// <summary>
    /// How many remembered bridges a memory runner connects on: the newest
    /// fifty, in the order they were proven, deliberately not shuffled. A pool
    /// is already ranked — BridgeMemory puts each connect's proven bridges at
    /// the front — so shuffling throws away the only information it carries.
    /// </summary>
    public const int MemoryRunnerLines = 50;

    public static string MemoryNameFor(string transport) => transport + MemorySuffix;

    /// <summary>
    /// Whether this runner exists only to reuse remembered bridges: the mixed
    /// `memory` runner or any `&lt;transport&gt;-memory` twin. Those are the
    /// runners the shuffled order does not apply to.
    /// </summary>
    public static bool IsMemoryRunner(string name) =>
        name == TransportMemory || name.EndsWith(MemorySuffix, StringComparison.Ordinal);

    /// <summary>The newest <see cref="MemoryRunnerLines"/> of a pool, which is what it arrives as.</summary>
    public static string MemoryRunnerLinesOf(string lines) =>
        string.Join("\n", SplitLines(lines).Where(l => !string.IsNullOrWhiteSpace(l)).Take(MemoryRunnerLines));

    /// <summary>The real transport behind a runner name (`webtunnel-memory` -> `webtunnel`).</summary>
    public static string BaseTransportOf(string name)
    {
        var idx = name.IndexOf(MemorySuffix, StringComparison.Ordinal);
        return idx < 0 ? name : name[..idx];
    }

    private const string BridgeBaseUrl =
        "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge";

    /// <summary>
    /// Remote bridge lists per transport. webtunnel is published as two files
    /// and both are used: they are merged into a single list before anything is
    /// handed to Tor. Every list is also bundled with the app, so the bundled
    /// copy is the first fallback before the network is tried.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BRIDGE_SOURCES =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [TransportVanilla] = new[] { $"{BridgeBaseUrl}/vanilla.txt" },
            [TransportObfs4] = new[] { $"{BridgeBaseUrl}/obfs4.txt" },
            [TransportWebtunnel] = new[] { $"{BridgeBaseUrl}/webtunnel.txt", $"{BridgeBaseUrl}/webtunnel_ipv6.txt" },
            [TransportSnowflake] = new[] { $"{BridgeBaseUrl}/snowflake.txt" },
            [TransportFresh] = new[]
            {
                $"{BridgeBaseUrl}/webtunnel_72h.txt",
                $"{BridgeBaseUrl}/vanilla_72h.txt",
                $"{BridgeBaseUrl}/obfs4_72h.txt",
                $"{BridgeBaseUrl}/obfs4_ipv6_72h.txt",
                $"{BridgeBaseUrl}/webtunnel_ipv6_72h.txt",
                $"{BridgeBaseUrl}/vanilla_ipv6_72h.txt"
            }
        };

    /// <summary>
    /// The same files are bundled with the app under bridges/, so a connect
    /// works on the first launch and while the network is unavailable. The asset
    /// name is the last path segment of its URL, so the two lists can never
    /// drift apart.
    /// </summary>
    public const string BundledAssetDir = "bridges";

    /// <summary>
    /// Modes with a bridge list of their own, which are exactly the modes that
    /// get a memory twin. Combined has no list on disk but does have bridges, so
    /// it belongs here even though it is not in <see cref="BRIDGE_SOURCES"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> TwinnedModes =
        new HashSet<string>(BRIDGE_SOURCES.Keys, StringComparer.Ordinal) { TransportCombined };

    public static string BundledAssetName(string url) => url[(url.LastIndexOf('/') + 1)..];

    public const long RaceTimeoutMs = 1_800_000L;

    /// <summary>A recovery restart is expected to be quick: proven bridges, same port.</summary>
    public const long RecoveryTimeoutMs = 120_000L;

    public const long PollIntervalMs = 1_000L;
    public const long PortFreeTimeoutMs = 15_000L;
    public const long PortFreePollMs = 250L;

    /// <summary>Runners take basePort+1 .. basePort+<see cref="MaxPortOffset"/>.</summary>
    public const int MaxPortOffset = 15;

    /// <summary>Split any text into lines the way Kotlin's lineSequence() does (\r\n, \n, \r).</summary>
    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// The same lines in a new order. Blank lines are dropped instead of being
    /// shuffled about, since nothing reads them. Each runner is shuffled on its
    /// own, so two runners racing in the same connect do not hand Tor the same
    /// sequence. Memory runners are not passed through here at all; see
    /// <see cref="IsMemoryRunner"/>.
    /// </summary>
    public static string Shuffled(string lines)
    {
        var list = SplitLines(lines).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return string.Join("\n", list);
    }

    /// <summary>
    /// The fixed runner&lt;-&gt;port assignment. It is a function of
    /// <paramref name="basePort"/> only, so a runner keeps the same port across
    /// a recovery, and a runner that was not part of the last race still has a
    /// known, reserved port to be restarted on.
    /// </summary>
    public static IReadOnlyDictionary<string, int> RunnerPorts(int basePort) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [TransportVanilla] = basePort + 1,
            [TransportObfs4] = basePort + 2,
            [TransportWebtunnel] = basePort + 3,
            [TransportMemory] = basePort + 4,
            [TransportSnowflake] = basePort + 5,
            [TransportCustom] = basePort + 6,
            [TransportDirect] = basePort + 7,
            [MemoryNameFor(TransportVanilla)] = basePort + 8,
            [MemoryNameFor(TransportObfs4)] = basePort + 9,
            [MemoryNameFor(TransportWebtunnel)] = basePort + 10,
            [MemoryNameFor(TransportSnowflake)] = basePort + 11,
            [TransportFresh] = basePort + 12,
            [MemoryNameFor(TransportFresh)] = basePort + 13,
            [TransportCombined] = basePort + 14,
            [MemoryNameFor(TransportCombined)] = basePort + 15
        };

    /// <summary>
    /// Merge several bridge files into one list, keeping every bridge exactly
    /// once (identified by its fingerprint) and alternating between the sources
    /// so a transport backed by two files still draws from both once Tor's
    /// per-runner line cap is applied. Comment and blank lines are dropped.
    ///
    /// A bridge is republished whenever the client protocol changes, so the same
    /// fingerprint shows up with an older and a newer `ver=`. The newest one
    /// wins, because a stale protocol version is rejected during the handshake.
    /// </summary>
    public static string MergeBridgeLists(IReadOnlyList<string> bodies)
    {
        var lists = bodies
            .Select(body => SplitLines(body)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Select(l => (Line: l, Fp: BridgeMemory.FingerprintOf(l)))
                .ToList())
            .ToList();
        if (lists.Count == 0) return "";
        if (lists.Count == 1) return string.Join("\n", lists[0].Select(t => t.Line));

        // Dictionary preserves insertion order while nothing is removed, and a
        // replacement of an existing key keeps its position — the order the
        // bridges were first seen in is preserved.
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var longest = lists.Max(l => l.Count);
        for (var i = 0; i < longest; i++)
        {
            foreach (var list in lists)
            {
                if (i >= list.Count) continue;
                var (line, fp) = list[i];
                var key = fp ?? line;
                if (!merged.TryGetValue(key, out var kept) || NewerWebtunnelVersion(line, kept))
                {
                    merged[key] = line;
                }
            }
        }
        AppLog.I(TAG, $"merged {lists.Count} bridge files into {merged.Count} unique bridge(s)");
        return string.Join("\n", merged.Values);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> carries a webtunnel protocol
    /// version newer than the one in <paramref name="kept"/>. Lines without a
    /// `ver=` never replace a kept line, so every other transport keeps the
    /// first-seen-wins behaviour.
    /// </summary>
    private static bool NewerWebtunnelVersion(string candidate, string kept)
    {
        var a = WebtunnelVersion(candidate);
        var b = WebtunnelVersion(kept);
        if (a == null || b == null) return false;
        if (a.Value.A != b.Value.A) return a.Value.A > b.Value.A;
        if (a.Value.B != b.Value.B) return a.Value.B > b.Value.B;
        return a.Value.C > b.Value.C;
    }

    /// <summary>The `ver=` of a webtunnel line, or null when the line has none.</summary>
    private static (int A, int B, int C)? WebtunnelVersion(string line)
    {
        var raw = line.Split(' ').FirstOrDefault(t => t.StartsWith("ver=", StringComparison.Ordinal));
        if (raw == null) return null;
        var parts = raw["ver=".Length..].Split('.')
            .Select(p => int.TryParse(p, out var v) ? v : (int?)null)
            .Where(v => v != null)
            .Select(v => v!.Value)
            .ToArray();
        if (parts.Length == 0) return null;
        return (parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2));
    }

    /// <summary>
    /// Block until <paramref name="port"/> on <paramref name="host"/> can
    /// actually be bound, or the timeout expires. A killed process keeps its
    /// listening socket until the kernel reaps it, so anything that binds right
    /// after a teardown has to wait for this. Returns true when the port is free.
    /// </summary>
    public static bool AwaitPortFree(string host, int port, long timeoutMs = PortFreeTimeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + timeoutMs;
        var attempt = 0;
        while (true)
        {
            var free = false;
            try
            {
                using var l = new TcpListener(IPAddress.Parse(host), port);
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
                l.Stop();
                free = true;
            }
            catch
            {
                free = false;
            }
            if (free)
            {
                if (attempt > 0) AppLog.I(TAG, $"port {port} free after {attempt} wait(s)");
                return true;
            }
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
            {
                AppLog.E(TAG, $"port {port} still in use after {timeoutMs}ms");
                return false;
            }
            attempt++;
            AppLog.W(TAG, $"port {port} still held, waiting (attempt {attempt})");
            Thread.Sleep((int)PortFreePollMs);
        }
    }
}

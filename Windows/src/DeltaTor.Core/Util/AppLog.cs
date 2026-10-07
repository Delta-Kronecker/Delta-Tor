using System.Globalization;
using System.Text.RegularExpressions;

namespace DeltaTor.Core.Util;

/// <summary>One log line. <paramref name="Session"/> 0 = before the first connection.</summary>
public sealed record LogEntry(long Id, string Raw, char Level, int Session = 0, string? Transport = null);

/// <summary>
/// One connect attempt. Every log line is stamped with the session it belongs to
/// so the log screen can show each connection separately, with its own header
/// and outcome.
/// </summary>
public sealed record LogSession(int Id, string Label, long StartedAtMillis, string? Outcome = null);

/// <summary>
/// In-memory log buffer that wraps <see cref="Trace"/>.
///
/// Every call forwards to the trace listener AND appends to a ring buffer that
/// the log screen observes by polling (mirror of the Android AppLog that wrapped
/// android.util.Log + a StateFlow). The log screen calls <see cref="FlushIfDirty"/>
/// on its timer, so many rapid calls batch into a single snapshot copy.
/// </summary>
public static class AppLog
{
    public const int MaxLines = 4500;

    /// <summary>Transports raced in parallel; the log screen lets the user pick one.</summary>
    public static readonly IReadOnlyList<string> Transports = new[]
    {
        "vanilla", "obfs4", "webtunnel", "snowflake", "memory", "direct", "custom"
    };

    private static readonly object BufferLock = new();
    private static readonly object SessionLock = new();

    private static long _nextId;
    private static readonly List<LogEntry> Buffer = new(MaxLines + 64);

    /// <summary>Live entry count per transport ("" = shared lines), used for fair trimming.</summary>
    private static readonly Dictionary<string, int> PerTransport = new();

    /// <summary>When true, sensitive config details are redacted from the in-app log buffer.</summary>
    public static volatile bool RedactSensitive;

    /// <summary>
    /// Whether lines are recorded at all (Config.loggingEnabled). A bootstrap is a
    /// few thousand Tor lines; off means nothing is kept. This is about recording,
    /// not about Tor — Tor's output is still read (bootstrap %, proven bridges).
    /// </summary>
    public static volatile bool Enabled;

    private static long _sessionIds;
    private static readonly List<LogSession> _sessions = new();
    private static IReadOnlyList<LogSession> _sessionsSnapshot = Array.Empty<LogSession>();
    private static int _currentSession;

    /// <summary>Session headers for the log screen (last 12), replace-and-notify.</summary>
    public static IReadOnlyList<LogSession> Sessions => Volatile.Read(ref _sessionsSnapshot);

    private static IReadOnlyList<LogEntry> _linesSnapshot = Array.Empty<LogEntry>();

    /// <summary>Lazy snapshot — only rebuilt when somebody observes (log screen open).</summary>
    public static IReadOnlyList<LogEntry> Lines => Volatile.Read(ref _linesSnapshot);

    private static int _observerCount;
    private static int _dirty;

    /// <summary>Number of active log-screen observers (enables timestamps + snapshots).</summary>
    public static int ObserverCount => Volatile.Read(ref _observerCount);

    private static readonly Regex RunnerTag = new(@"TorRunner\[(\w+)]", RegexOptions.Compiled);

    // --- connect sessions -----------------------------------------------------

    /// <summary>
    /// Begin a new connection session. Subsequent lines are tagged with its id
    /// until the next <see cref="BeginSession"/>. The returned id is stamped on
    /// every entry.
    /// </summary>
    public static int BeginSession(string label)
    {
        lock (SessionLock)
        {
            var id = (int)Interlocked.Increment(ref _sessionIds);
            Volatile.Write(ref _currentSession, id);
            lock (SessionLock)
            {
                var next = new List<LogSession>(_sessions) { new(id, label, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) };
                if (next.Count > 12) next = next[^12..];
                _sessions = next;
                Volatile.Write(ref _sessionsSnapshot, _sessions);
            }
            Append('=', "DeltaTor", $"=== connection #{id} · {label} ===");
            return id;
        }
    }

    /// <summary>Mark the current session as finished and record the outcome on its header.</summary>
    public static void EndSession(string outcome)
    {
        lock (SessionLock)
        {
            var id = Volatile.Read(ref _currentSession);
            if (id == 0) return;
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].Id == id)
                    _sessions[i] = _sessions[i] with { Outcome = outcome };
            }
            Volatile.Write(ref _sessionsSnapshot, _sessions);
            Append('=', "DeltaTor", $"=== connection #{id} · {outcome} ===");
        }
    }

    private static int SessionOf() => Volatile.Read(ref _currentSession);

    private static void Append(char level, string tag, string msg) =>
        AppendFor(level, tag, msg, TransportOf(tag));

    private static string? TransportOf(string tag)
    {
        var m = RunnerTag.Match(tag);
        if (!m.Success) return null;
        var t = m.Groups[1].Value;
        return Transports.Contains(t) ? t : null;
    }

    private static void AppendFor(char level, string tag, string msg, string? transport)
    {
        if (!Enabled) return;
        var id = Interlocked.Increment(ref _nextId) - 1;
        var ts = Volatile.Read(ref _observerCount) > 0
            ? DateTime.Now.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " "
            : "";
        var entry = new LogEntry(id, $"{ts}{level}/{tag}: {msg}", level, SessionOf(), transport);
        lock (BufferLock)
        {
            Buffer.Add(entry);
            var key = transport ?? "";
            PerTransport[key] = PerTransport.TryGetValue(key, out var n) ? n + 1 : 1;
            TrimLocked();
        }
        Trace.WriteLine(entry.Raw);
        if (Volatile.Read(ref _observerCount) > 0)
            Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Keep the buffer bounded without letting one noisy transport (vanilla emits
    /// thousands of Tor lines) evict the others: always drop the oldest line of
    /// whichever transport currently holds the most of them.
    /// </summary>
    private static void TrimLocked()
    {
        while (Buffer.Count > MaxLines)
        {
            var victim = "";
            var worst = 0;
            foreach (var (t, n) in PerTransport)
            {
                if (n > worst)
                {
                    worst = n;
                    victim = t;
                }
            }
            var idx = Buffer.FindIndex(e => (e.Transport ?? "") == victim);
            if (idx < 0)
            {
                // Should not happen; fall back to plain FIFO so we never spin.
                var dropped = Buffer[0];
                Buffer.RemoveAt(0);
                var dk = dropped.Transport ?? "";
                PerTransport[dk] = PerTransport.TryGetValue(dk, out var dn) ? dn - 1 : 0;
                continue;
            }
            Buffer.RemoveAt(idx);
            PerTransport[victim] = PerTransport.TryGetValue(victim, out var vn) ? vn - 1 : 0;
        }
    }

    /// <summary>
    /// Copy the buffer to <see cref="Lines"/> if anything changed since the last
    /// flush. Called by the log screen on its periodic timer so many rapid log
    /// calls batch into a single snapshot copy + repaint.
    /// </summary>
    public static void FlushIfDirty()
    {
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) == 1)
        {
            lock (BufferLock)
            {
                Volatile.Write(ref _linesSnapshot, Buffer.ToArray());
            }
        }
    }

    /// <summary>Call from the log screen open/close to enable/disable snapshots.</summary>
    public static void AddObserver()
    {
        Interlocked.Increment(ref _observerCount);
        // Immediately snapshot current buffer for the new observer.
        lock (BufferLock)
        {
            Volatile.Write(ref _linesSnapshot, Buffer.ToArray());
        }
    }

    public static void RemoveObserver()
    {
        var c = Interlocked.Decrement(ref _observerCount);
        if (c < 0) Interlocked.CompareExchange(ref _observerCount, 0, c);
    }

    /// <summary>Tags whose messages contain sensitive config details (hosts, ports, credentials).</summary>
    private static readonly HashSet<string> SensitiveTags = new()
    {
        "HevSocks5Tunnel", "SlipstreamSocksBridge", "DnsttSocksBridge", "SshTunnelBridge",
        "SlipNetVpnService", "KotlinTunnelManager", "NaiveSocksBridge", "TorSocksBridge",
        "SlipstreamBridge", "DnsttBridge", "NaiveBridge", "VpnRepositoryImpl", "VaydnsBridge",
        "DnsResolverProber", "DohBridge", "HttpProxyServer", "ProxyHttpConnect",
        "ProxyWebSocket", "TlsSocketFactory", "NaiveSocksProxy", "PayloadSocketFactory",
        "DomainRouter", "DnsDoHProxy"
    };

    /// <summary>Tag prefixes for dynamic tags (e.g. SshTunnel[default], Socks5Proxy[0]).</summary>
    private static readonly string[] SensitiveTagPrefixes = { "SshTunnel[", "Socks5Proxy[" };

    /// <summary>
    /// Check if this log line should be redacted from the in-app buffer. All
    /// messages from sensitive tags are suppressed from the buffer (still
    /// forwarded to trace, which on Windows is only visible with a listener).
    /// </summary>
    private static bool ShouldRedact(string tag)
    {
        if (!RedactSensitive) return false;
        if (SensitiveTags.Contains(tag)) return true;
        foreach (var p in SensitiveTagPrefixes)
            if (tag.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    public static void V(string tag, string msg) => Write('V', tag, msg);
    public static void D(string tag, string msg) => Write('D', tag, msg);
    public static void I(string tag, string msg) => Write('I', tag, msg);

    public static void W(string tag, string msg) => Write('W', tag, msg);

    public static void W(string tag, string msg, Exception? tr) =>
        Write('W', tag, tr != null ? $"{msg}\n{tr}" : msg);

    public static void E(string tag, string msg) => Write('E', tag, msg);

    public static void E(string tag, string msg, Exception? tr) =>
        Write('E', tag, tr != null ? $"{msg}\n{tr}" : msg);

    private static void Write(char level, string tag, string msg)
    {
        if (!ShouldRedact(tag)) Append(level, tag, msg);
        Trace.WriteLine($"{level}/{tag}: {msg}");
    }

    public static void Clear()
    {
        lock (BufferLock)
        {
            Buffer.Clear();
            PerTransport.Clear();
            Volatile.Write(ref _linesSnapshot, Array.Empty<LogEntry>());
        }
    }

    /// <summary>
    /// Log an event tied to a specific connect session, used by the transport
    /// runner threads so their lines land in the right section even though they
    /// are not the connection owner.
    /// </summary>
    public static void Session(int sessionId, char level, string tag, string msg)
    {
        lock (SessionLock)
        {
            var prev = Volatile.Read(ref _currentSession);
            Volatile.Write(ref _currentSession, sessionId);
            try
            {
                Append(level, tag, msg);
            }
            finally
            {
                Volatile.Write(ref _currentSession, prev);
            }
        }
    }

    /// <summary>Same as <see cref="Session"/> but also attributes the line to one transport.</summary>
    public static void Transport(int sessionId, string transport, char level, string tag, string msg)
    {
        lock (SessionLock)
        {
            var prev = Volatile.Read(ref _currentSession);
            Volatile.Write(ref _currentSession, sessionId);
            try
            {
                AppendFor(level, tag, msg, transport);
            }
            finally
            {
                Volatile.Write(ref _currentSession, prev);
            }
        }
    }
}

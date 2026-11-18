using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeltaTor.Core.Util;

/// <summary>
/// Logging facade for the app: every call is forwarded to <see cref="Trace"/>
/// and, while recording is on (<see cref="Enabled"/>), appended to the daily
/// file of its category by <see cref="LogFiles"/>.
///
/// There is no in-memory buffer or log screen any more; the files under
/// <see cref="LogFiles.Root"/> are the log. Session headers still delimit every
/// connect attempt so a file can be read as a timeline.
/// </summary>
public static class AppLog
{
    /// <summary>Transports raced in parallel; also the category folders on disk.</summary>
    public static readonly IReadOnlyList<string> Transports = new[]
    {
        "vanilla", "obfs4", "webtunnel", "snowflake", "memory", "direct", "custom"
    };

    private static readonly object SessionLock = new();

    /// <summary>When true, sensitive config details are redacted from the recorded files.</summary>
    public static volatile bool RedactSensitive;

    /// <summary>
    /// Whether lines are recorded at all (Config.loggingEnabled). A bootstrap is a
    /// few thousand Tor lines; off means nothing is written. This is about
    /// recording, not about Tor — Tor's output is still read (bootstrap %,
    /// proven bridges).
    /// </summary>
    public static volatile bool Enabled;

    private static long _sessionIds;
    private static int _currentSession;

    private static readonly Regex RunnerTag = new(@"TorRunner\[(\w+)]", RegexOptions.Compiled);

    // --- connect sessions -----------------------------------------------------

    /// <summary>
    /// Begin a new connection session. Subsequent lines are stamped with its id
    /// until the next <see cref="BeginSession"/>, and a header line opens the
    /// section in the recorded file.
    /// </summary>
    public static int BeginSession(string label)
    {
        lock (SessionLock)
        {
            var id = (int)Interlocked.Increment(ref _sessionIds);
            Volatile.Write(ref _currentSession, id);
            Append('=', "DeltaTor", $"=== connection #{id} · {label} ===");
            return id;
        }
    }

    /// <summary>Mark the current session as finished with an outcome line.</summary>
    public static void EndSession(string outcome)
    {
        lock (SessionLock)
        {
            var id = Volatile.Read(ref _currentSession);
            if (id == 0) return;
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
        LogFiles.Write(transport, SessionOf(), level, tag, msg);
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
    /// Check if this log line should be redacted from the recorded files. All
    /// messages from sensitive tags are suppressed (still forwarded to trace,
    /// which on Windows is only visible with a listener).
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

        // Automatically log any TUN-related diagnostic tags straight to tunlog.txt.
        // The proxy chain counts: its server list, its routes and every SSTP/PPP
        // step are the only record of why a chain attempt failed.
        if (tag is "TunEngine" or "RouteManager" or "HevTunnelApi" or "TorVpnService" or "ExitNode"
            or "Chain" or "ChainNet" or "ChainList" or "TorDns")
        {
            TunLog.Write(tag, $"[{level}] {msg}");
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
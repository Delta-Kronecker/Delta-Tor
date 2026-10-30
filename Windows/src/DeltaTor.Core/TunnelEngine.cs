namespace DeltaTor.Core;

/// <summary>Byte counters of the tun2socks data path, as published by the tunnel.</summary>
public sealed record TunnelStats(long TxBytes, long RxBytes);

/// <summary>
/// The tun2socks data path behind the local SOCKS listener — the Windows
/// counterpart of Android's HevSocks5Tunnel JNI surface. On Windows this is
/// hev-socks5-tunnel over a Wintun adapter; route/DNS/bypass bookkeeping is
/// owned by RouteManager, not by the data plane.
/// </summary>
public interface ISocksTunnel
{
    /// <summary>Start the tunnel toward the SOCKS listener. Null on success.</summary>
    Exception? Start(string socksAddress, int socksPort);

    /// <summary>Tear the tunnel and its adapter down.</summary>
    void Stop();

    /// <summary>
        /// Current traffic counters, or null while the tunnel is down.
        TunnelStats? GetStats();

        /// <summary>Wait until the tun2socks data path is actively routing traffic through the TUN interface.
        /// Returns true if ready within the timeout, false if the tunnel started but is still initializing.
        /// This is critical for Se7enTun which needs time to establish the SOCKS proxy connection.</summary>
        bool WaitForTraffic(TimeSpan timeout);
    }

/// <summary>
/// Static facade the service state machine calls, mirroring the shape of the
/// Android HevSocks5Tunnel object (start/stop/getStats).
/// </summary>
public static class TunnelEngine
{
    /// <summary>The real implementation; the app sets it at startup. Null = tunnel unavailable.</summary>
    public static ISocksTunnel? Impl { get; set; }

    // Start returns null on SUCCESS, so the "engine unavailable" fallback must
    // test Impl for null explicitly: `Impl?.Start(...) ?? new ...` would treat a
    // successful (null) start as "no engine" and fail the connect.
    public static Exception? Start(string socksAddress, int socksPort) =>
        Impl is null
            ? new InvalidOperationException("Tunnel engine is not available")
            : Impl.Start(socksAddress, socksPort);

    public static void Stop() => Impl?.Stop();

    public static TunnelStats? GetStats() => Impl?.GetStats();

    public static bool WaitForTraffic(TimeSpan timeout) =>
        Impl?.WaitForTraffic(timeout) ?? false;
}

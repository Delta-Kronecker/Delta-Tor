namespace DeltaTor.Core;

/// <summary>Byte counters of the tun2socks data path, as published by the tunnel.</summary>
public sealed record TunnelStats(long TxBytes, long RxBytes);

/// <summary>
/// The tun2socks data path behind the local SOCKS listener — the Windows
/// counterpart of Android's HevSocks5Tunnel JNI surface. On Windows this is
/// Se7enTun (a Wintun adapter plus a separate tun2socks.exe), which creates
/// the adapter and manages its routes, DNS and split rules itself, so there is
/// no file descriptor or route bookkeeping on this side of the boundary.
/// </summary>
public interface ISocksTunnel
{
    /// <summary>Start the tunnel toward the SOCKS listener. Null on success.</summary>
    Exception? Start(string socksAddress, int socksPort);

    /// <summary>Tear the tunnel and its adapter down.</summary>
    void Stop();

    /// <summary>Current traffic counters, or null while the tunnel is down.</summary>
    TunnelStats? GetStats();
}

/// <summary>
/// Static facade the service state machine calls, mirroring the shape of the
/// Android HevSocks5Tunnel object (start/stop/getStats).
/// </summary>
public static class TunnelEngine
{
    /// <summary>The real implementation; the app sets it at startup. Null = tunnel unavailable.</summary>
    public static ISocksTunnel? Impl { get; set; }

    public static Exception? Start(string socksAddress, int socksPort) =>
        Impl?.Start(socksAddress, socksPort)
        ?? new InvalidOperationException("Tunnel engine is not available");

    public static void Stop() => Impl?.Stop();

    public static TunnelStats? GetStats() => Impl?.GetStats();
}

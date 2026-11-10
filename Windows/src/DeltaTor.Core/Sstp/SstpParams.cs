namespace DeltaTor.Core.Sstp;

/// <summary>What one SSTP session is asked to do: run the machine's real VPN or
/// only establish the negotiation up to IPCP as a reachability probe.</summary>
public enum SstpMode
{
    Run,
    Probe
}

/// <summary>
/// Every value the vendored SSTP core reads (SstpParams.kt). This replaces the
/// SharedPreferences access of the original app so the core can be driven from
/// DeltaTor's own settings engine.
/// </summary>
public sealed class SstpParams
{
    /// <summary>Whether this session runs the machine's VPN or is only a probe.</summary>
    public SstpMode Mode { get; init; } = SstpMode.Run;

    /// <summary>VPN Gate server hostname, never its IP: the name travels into the
    /// SOCKS5 CONNECT so Tor resolves it remotely, exactly like every other hop.</summary>
    public required string Hostname { get; init; }

    /// <summary>Server TLS port; a host line without a port arrives here as the default.</summary>
    public int Port { get; init; } = 443;

    /// <summary>The local hop the tunnel is chained through: Tor's SOCKS bridge.</summary>
    public required string ProxyHostname { get; init; }

    public required int ProxyPort { get; init; }

    /// <summary>VPN Gate's well-known credentials for its Virtual Hub "VPN".</summary>
    public string Username { get; init; } = "vpn";

    public string Password { get; init; } = "vpn";

    public int Mru { get; init; } = 1400;

    public int Mtu { get; init; } = 1400;

    public IReadOnlySet<string> AuthProtocols { get; init; } =
        new HashSet<string> { AuthProtocol.Pap, AuthProtocol.MsChapV2 };

    public long AuthTimeout { get; init; } = 30_000;

    public bool PppIPv4Enabled { get; init; } = true;

    public bool PppIPv6Enabled { get; init; }

    /// <summary>VPN Gate serves self-signed certificates; verification stays off by
    /// default, mirroring the original client.</summary>
    public bool DoVerifyCert { get; init; }

    public bool DoVerifyHostname { get; init; }
}

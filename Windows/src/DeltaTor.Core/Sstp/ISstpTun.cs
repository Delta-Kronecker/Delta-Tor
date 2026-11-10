namespace DeltaTor.Core.Sstp;

/// <summary>
/// The Windows counterpart of Android's <c>VpnService.Builder</c> + the
/// established tunnel file descriptor: the surface <see cref="IpTerminal"/>
/// drives. Keeping it an interface means the SSTP protocol core never touches a
/// platform API — the Wintun adapter implementation lives in DeltaTor.Native
/// and is registered into the chain engine exactly like <c>TunnelEngine.Impl</c>.
///
/// In <see cref="SstpMode.Probe"/> no builder is passed at all, so probe
/// sessions negotiate up to IPCP and never touch the machine's routing.
/// </summary>
public interface ISstpTun
{
    /// <summary>Configure the adapter with the address IPCP handed out (/32).</summary>
    void AddAddress(byte[] address, int prefixLength);

    /// <summary>Configure the adapter's resolver (the VPN Gate DNS, or the fallback).</summary>
    void AddDnsServer(byte[] address);

    /// <summary>Route 0.0.0.0/0 (and, on Windows, the IPv6 blackholes) into the adapter.</summary>
    void AddRoute(byte[] network, int prefixLength);

    void SetMtu(int mtu);

    void SetBlocking(bool blocking);

    /// <summary>Bring the adapter up; null when the session could not be created.</summary>
    ISstpTunSession? Establish();
}

/// <summary>The live packet pipe of an established adapter.</summary>
public interface ISstpTunSession
{
    /// <summary>Read one IP packet; returns the number of bytes, 0 or negative when closed.</summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Write one IP packet.</summary>
    void Write(byte[] buffer, int offset, int count);

    void Close();
}

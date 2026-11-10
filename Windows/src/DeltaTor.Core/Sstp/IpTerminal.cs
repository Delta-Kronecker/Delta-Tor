namespace DeltaTor.Core.Sstp;

/// <summary>
/// The Windows counterpart of kittoku's <c>IPTerminal</c>: it configures the
/// chain tunnel adapter from the IPCP results (a /32 address, the server's
/// resolver or a public fallback, a default route, the MTU) and then becomes the
/// packet pipe Incoming/OutgoingManager read and write.
///
/// Where the Android build calls VpnService.Builder and holds a
/// ParcelFileDescriptor, this drives <see cref="ISstpTun"/> — the platform
/// adapter lives in DeltaTor.Native and is injected via the chain engine.
/// </summary>
internal sealed class IpTerminal
{
    private readonly SharedBridge _bridge;
    private ISstpTunSession? _session;

    public IpTerminal(SharedBridge bridge) => _bridge = bridge;

    public async Task InitializeAsync()
    {
        if (IsZero(_bridge.CurrentIPv4))
        {
            _bridge.SendControlMessage(new ControlMessage(Where.IPv4, Result.ERR_INVALID_ADDRESS));
            return;
        }

        var builder = _bridge.Builder!;
        builder.AddAddress(_bridge.CurrentIPv4, 32);

        if (!IsZero(_bridge.CurrentProposedDns))
        {
            builder.AddDnsServer(_bridge.CurrentProposedDns);
        }
        else
        {
            // Some hubs hand out no resolver; a public one keeps the tunnel
            // usable instead of silently producing an unresolvable VPN.
            builder.AddDnsServer(new byte[] { 9, 9, 9, 9 });
        }

        builder.AddRoute(new byte[] { 0, 0, 0, 0 }, 0);
        builder.SetMtu(_bridge.PppMtu);
        builder.SetBlocking(true);

        _session = builder.Establish();
        if (_session is null)
        {
            _bridge.SendControlMessage(
                new ControlMessage(Where.IP, Result.ERR_UNEXPECTED_MESSAGE, "tunnel adapter could not be established"));
            return;
        }

        _bridge.SendControlMessage(new ControlMessage(Where.IP, Result.PROCEEDED));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void WritePacket(int start, int size, ByteBuffer buffer)
    {
        // nothing will be written until initialized
        // the position won't be changed
        _session?.Write(buffer.Array, start, size);
    }

    public void ReadPacket(ByteBuffer buffer)
    {
        buffer.Clear();
        var read = _session?.Read(buffer.Array, 0, _bridge.PppMtu) ?? 0;
        if (read <= 0)
        {
            throw new IOException("chain tunnel closed");
        }

        buffer.Position = read;
        buffer.Flip();
    }

    public void Close()
    {
        try
        {
            _session?.Close();
        }
        catch
        {
            // already gone
        }

        _session = null;
    }

    private static bool IsZero(byte[] value)
    {
        foreach (var b in value)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}

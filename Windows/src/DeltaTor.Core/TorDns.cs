using System.Net;
using System.Net.Sockets;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Every DNS query the machine makes, answered by Tor itself — in the system
/// proxy, TUN and proxy chain modes alike.
///
/// Tor already has a resolver of its own: torrc <c>DNSPort</c> (see
/// <see cref="TorRunner.DnsPort"/>). It is per runner and dynamic, because the
/// same three processes race and a fixed port would be claimed by whoever
/// started first. Windows can only be pointed at "address, port 53", so this
/// class runs a tiny loopback relay on port 53 that forwards the raw DNS
/// payload to whichever runner is currently winning, and it is the relay's
/// address that <see cref="SystemDns"/> installs on every adapter.
///
/// The relay never resolves anything itself: a query goes out to Tor's DNSPort
/// (and therefore through a Tor circuit) and the real answer comes back
/// untouched, including Tor's own synthetic answers for .onion names.
/// </summary>
public static class TorDns
{
    private const string Tag = "TorDns";
    private const long GuardIntervalMs = 10_000L;
    private static readonly object Gate = new();
    private static DnsRelay? _relay;
    private static long _lastGuardAt;

    /// <summary>True while the relay is serving (i.e. resolvers point at Tor).</summary>
    public static bool IsRunning
    {
        get { lock (Gate) return _relay != null; }
    }

    /// <summary>The loopback address the machine's resolvers are pointed at.</summary>
    public static string? AddressV4
    {
        get { lock (Gate) return _relay?.AddressV4; }
    }

    public static string? AddressV6
    {
        get { lock (Gate) return _relay?.AddressV6; }
    }

    /// <summary>
    /// The relay's IPv4 address as raw bytes, for the chain adapter builder
    /// (which takes an <see cref="System.Net.IPAddress"/>-shaped array). Null
    /// until the relay is up, so a probe that never touches the machine keeps
    /// its IPCP resolver.
    /// </summary>
    public static byte[]? AddressV4Bytes
    {
        get
        {
            lock (Gate)
            {
                var a = _relay?.AddressV4;
                return a is null ? null : IPAddress.Parse(a).GetAddressBytes();
            }
        }
    }

    /// <summary>
    /// Bring the relay up (once per session) and aim it at this runner's
    /// DNSPort. Called every time a winner is known, so it doubles as the
    /// recovery repoint without restarting anything.
    /// </summary>
    public static void Start(int dnsPort)
    {
        lock (Gate)
        {
            if (_relay is null)
            {
                _relay = DnsRelay.TryStart(dnsPort);
                if (_relay is null)
                {
                    AppLog.W(Tag, "DNS relay could not bind loopback:53; resolvers left untouched");
                    return;
                }

                AppLog.I(Tag, $"DNS relay up on {_relay.AddressV4}:53 (DNS goes to Tor)");
            }

            _relay.PointTo(dnsPort);
        }
    }

    /// <summary>Re-aim the relay at a fresh winner without touching the OS.</summary>
    public static void Repoint(int dnsPort)
    {
        lock (Gate) _relay?.PointTo(dnsPort);
    }

    /// <summary>Point every physical adapter's resolver at the relay.</summary>
    public static void ApplyPhysical()
    {
        string v4;
        string? v6;
        lock (Gate)
        {
            v4 = _relay?.AddressV4 ?? "";
            v6 = _relay?.AddressV6;
        }

        if (v4.Length == 0) return;
        SystemDns.ApplyPhysical(v4, v6);
    }

    /// <summary>Point a mode's own adapter (TUN or chain) at the relay.</summary>
    public static void ApplyTunnelAdapter(string adapterName)
    {
        string v4;
        string? v6;
        lock (Gate)
        {
            v4 = _relay?.AddressV4 ?? "";
            v6 = _relay?.AddressV6;
        }

        if (v4.Length == 0) return;
        SystemDns.ApplyTunnel(adapterName, v4, v6);
    }

    /// <summary>Slow-tick guard: re-assert the resolvers if something reset them.</summary>
    public static void KeepApplied()
    {
        // The stats loop ticks as fast as once a second; reading every adapter's
        // resolver list that often is pointless churn. Ten seconds still catches
        // a data plane that re-asserts its own DNS well before anyone notices.
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastGuardAt);
        if (now - last < GuardIntervalMs) return;
        Interlocked.Exchange(ref _lastGuardAt, now);
        SystemDns.VerifyAndReapply();
    }

    /// <summary>Put every resolver back and stop the relay. The full teardown.</summary>
    public static void Stop()
    {
        DnsRelay? relay;
        lock (Gate)
        {
            relay = _relay;
            _relay = null;
        }

        SystemDns.Restore();
        relay?.Dispose();
        SystemDns.Flush();
        if (relay != null) AppLog.I(Tag, "DNS relay stopped; resolvers restored");
    }
}

/// <summary>
/// The loopback forwarder behind <see cref="TorDns"/>: a UDP and TCP listener
/// on port 53 of a loopback address, passing each query to the active DNSPort
/// and the answer straight back. Nothing is parsed, so .onion lookups and every
/// record type behave exactly as Tor's resolver does.
/// </summary>
internal sealed class DnsRelay : IDisposable
{
    private const string Tag = "TorDns";
    private const int MaxQuery = 4096;
    private const int ForwardTimeoutMs = 8_000;

    // 127.0.0.53 first, mirroring systemd-resolved's habit on Linux: it is out
    // of the way of anything already holding 127.0.0.1:53. The whole 127/8
    // range is local on Windows, so any of these binds without an address being
    // assigned. The choice is made by probing, never assumed.
    private static readonly string[] CandidatesV4 =
    {
        "127.0.0.53", "127.0.0.54", "127.0.0.55", "127.0.0.1"
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _handlers = new(64, 64);
    private readonly List<Socket> _sockets = new();
    private volatile int _targetPort;
    private volatile bool _disposed;

    public string AddressV4 { get; }
    public string? AddressV6 { get; private set; }

    private DnsRelay(string addressV4, string? addressV6, int targetPort)
    {
        AddressV4 = addressV4;
        AddressV6 = addressV6;
        _targetPort = targetPort;
    }

    /// <summary>
    /// Bind the first free loopback candidate, IPv4 always and IPv6 when the
    /// stack allows it. Null when no candidate could be bound; the caller then
    /// leaves the machine's resolvers alone rather than blackholing DNS.
    /// </summary>
    public static DnsRelay? TryStart(int targetPort)
    {
        foreach (var candidate in CandidatesV4)
        {
            var udp = BindUdp(candidate);
            if (udp is null) continue;
            var tcp = BindTcp(candidate);
            if (tcp is null)
            {
                udp.Dispose();
                continue;
            }

            var relay = new DnsRelay(candidate, null, targetPort);
            relay._sockets.Add(udp);
            relay._sockets.Add(tcp);
            relay.BindV6();
            relay.StartLoops();
            return relay;
        }

        return null;
    }

    private void BindV6()
    {
        var udp = BindUdp("::1");
        if (udp is null) return;
        var tcp = BindTcp("::1");
        if (tcp is null)
        {
            udp.Dispose();
            return;
        }

        _sockets.Add(udp);
        _sockets.Add(tcp);
        // Set once, before the caller publishes this instance under its lock.
        AddressV6 = "::1";
    }

    private static Socket? BindUdp(string address)
    {
        Socket? socket = null;
        try
        {
            var ip = IPAddress.Parse(address);
            socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(ip, 53));
            return socket;
        }
        catch
        {
            socket?.Dispose();
            return null;
        }
    }

    private static Socket? BindTcp(string address)
    {
        Socket? socket = null;
        try
        {
            var ip = IPAddress.Parse(address);
            socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(ip, 53));
            socket.Listen(32);
            return socket;
        }
        catch
        {
            socket?.Dispose();
            return null;
        }
    }

    public void PointTo(int dnsPort)
    {
        if (dnsPort > 0) _targetPort = dnsPort;
    }

    private void StartLoops()
    {
        foreach (var socket in _sockets)
        {
            if (socket.SocketType == SocketType.Dgram) _ = Task.Run(() => UdpLoopAsync(socket));
            else _ = Task.Run(() => TcpLoopAsync(socket));
        }
    }

    private async Task UdpLoopAsync(Socket listener)
    {
        var buffer = new byte[MaxQuery];
        var any = listener.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            try
            {
                var result = await listener.ReceiveFromAsync(
                    buffer.AsMemory(), SocketFlags.None, any, _cts.Token);
                var query = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                var from = result.RemoteEndPoint;
                await _handlers.WaitAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    try { await ForwardUdpAsync(listener, query, from); }
                    finally { ReleaseHandler(); }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                if (_disposed) return;
                AppLog.D(Tag, $"udp receive: {e.Message}");
            }
        }
    }

    private async Task ForwardUdpAsync(Socket listener, byte[] query, EndPoint from)
    {
        var port = _targetPort;
        if (port <= 0) return;
        try
        {
            using var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var timeout = new CancellationTokenSource(ForwardTimeoutMs);
            await upstream.SendToAsync(query.AsMemory(), SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, port), timeout.Token);

            var buffer = new byte[MaxQuery];
            var result = await upstream.ReceiveFromAsync(
                buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            var response = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            if (_disposed) return;
            await listener.SendToAsync(response.AsMemory(), SocketFlags.None, from);
        }
        catch
        {
            // No answer: the client's own retry schedule takes over.
        }
    }

    private async Task TcpLoopAsync(Socket listener)
    {
        while (!_disposed)
        {
            Socket? client = null;
            try
            {
                client = await listener.AcceptAsync(_cts.Token);
                var accepted = client;
                await _handlers.WaitAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    try { await ForwardTcpAsync(accepted); }
                    finally { ReleaseHandler(); }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                client?.Dispose();
                if (_disposed) return;
                AppLog.D(Tag, $"tcp accept: {e.Message}");
            }
        }
    }

    private async Task ForwardTcpAsync(Socket client)
    {
        try
        {
            using (client)
            using (var timeout = new CancellationTokenSource(ForwardTimeoutMs))
            {
                var header = new byte[2];
                if (!await ReadExactlyAsync(client, header, timeout.Token)) return;
                var length = (header[0] << 8) | header[1];
                if (length <= 0 || length > MaxQuery) return;

                var query = new byte[length];
                if (!await ReadExactlyAsync(client, query, timeout.Token)) return;

                var port = _targetPort;
                if (port <= 0) return;

                using var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await upstream.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
                await upstream.SendAsync(query.AsMemory(), SocketFlags.None, timeout.Token);

                var replyHeader = new byte[2];
                if (!await ReadExactlyAsync(upstream, replyHeader, timeout.Token)) return;
                var replyLength = (replyHeader[0] << 8) | replyHeader[1];
                if (replyLength <= 0 || replyLength > MaxQuery) return;

                var reply = new byte[replyLength];
                if (!await ReadExactlyAsync(upstream, reply, timeout.Token)) return;

                var framed = new byte[2 + replyLength];
                framed[0] = replyHeader[0];
                framed[1] = replyHeader[1];
                Array.Copy(reply, 0, framed, 2, replyLength);
                await client.SendAsync(framed.AsMemory(), SocketFlags.None, timeout.Token);
            }
        }
        catch
        {
            // Broken pipe or timeout: the client retries over UDP/TCP.
        }
    }

    private static async Task<bool> ReadExactlyAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct);
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    private void ReleaseHandler()
    {
        try { _handlers.Release(); } catch (ObjectDisposedException) { /* stopping */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* already gone */ }
        foreach (var socket in _sockets)
        {
            try { socket.Dispose(); } catch { /* already gone */ }
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// SOCKS5 bridge sitting between the local tunnel side and Tor's SOCKS5 proxy:
///
///  - CONNECT (0x01): chains to Tor SOCKS5 (no auth)
///  - FWD_UDP (0x05) DNS (port 53): DNS-over-TCP through Tor SOCKS5 CONNECT
///  - FWD_UDP (0x05) non-DNS: dropped silently (client falls back to TCP CONNECT)
///
/// DNS optimization: up to 8 concurrent resolutions, answers cached for 5
/// minutes, several public resolvers tried in turn — all reached over Tor.
///
/// Traffic flow:
/// App (local SOCKS clients) -&gt; TorSocksBridge (listenPort)
///   TCP: -&gt; SOCKS5 CONNECT (no auth) -&gt; Tor SOCKS5 (torSocksPort) -&gt; circuit
///   DNS: -&gt; FWD_UDP -&gt; DNS-over-TCP via Tor SOCKS5 CONNECT -&gt; resolver:53
///
/// <see cref="Router"/> carries the dormant domain router (plan decision 1,
/// split tunnelling CUT): it ships as <see cref="DomainRouter.Disabled"/>, so
/// every CONNECT takes the plain tunnel path, exactly as if routing code were
/// not present.
/// </summary>
public static class TorSocksBridge
{
    private const string TAG = "TorSocksBridge";

    public static volatile bool DebugLogging;

    private static volatile DomainRouter _router = DomainRouter.Disabled;

    /// <summary>Domain router in force; dormant (Disabled) on Windows v1.</summary>
    public static DomainRouter Router
    {
        get => _router;
        set => _router = value;
    }

    private static void LogD(string msg)
    {
        if (DebugLogging) AppLog.D(TAG, msg);
    }

    private const int BindMaxRetries = 10;
    private const int BindRetryDelayMs = 200;
    private const int BufferSize = 32768;
    private const int TcpConnectTimeoutMs = 30_000;
    private const int DnsTimeoutMs = 15_000;
    private const long DnsCacheTtlMs = 300_000L;

    private static string _torHost = "127.0.0.1";
    private static int _torSocksPort;
    private static string? _localAuthUsername;
    private static string? _localAuthPassword;
    private static volatile TcpListener? _serverSocket;
    private static Thread? _acceptorThread;
    private static volatile bool _running;

    private static readonly object ConnectionThreadsLock = new();
    private static readonly List<Thread> _connectionThreads = new();

    // Every socket this bridge handed out, so stop() can unblock the reader
    // threads instead of leaving them parked on a client that never speaks.
    private static readonly object ActiveSocketsLock = new();
    private static readonly HashSet<Socket> ActiveSockets = new();

    private static SemaphoreSlim _dnsSlots = new(8, 8);

    private sealed record DnsCacheEntry(byte[] Response, long ExpiresAt);

    private static readonly ConcurrentDictionary<string, DnsCacheEntry> DnsCache =
        new(StringComparer.Ordinal);

    public static Exception? Start(
        int torSocksPort,
        int listenPort,
        string torHost = "127.0.0.1",
        string listenHost = "127.0.0.1",
        string? localAuthUsername = null,
        string? localAuthPassword = null)
    {
        AppLog.I(TAG, "========================================");
        AppLog.I(TAG, "Starting Tor SOCKS5 bridge");
        AppLog.I(TAG, $"  Tor SOCKS5: {torHost}:{torSocksPort}");
        AppLog.I(TAG, $"  Listen: {listenHost}:{listenPort}");
        AppLog.I(TAG, $"  Local auth: {(!string.IsNullOrEmpty(localAuthUsername) ? "enabled" : "disabled")}");
        AppLog.I(TAG, "========================================");

        Stop();
        _torHost = torHost;
        _torSocksPort = torSocksPort;
        _localAuthUsername = localAuthUsername;
        _localAuthPassword = localAuthPassword;

        try
        {
            var ss = BindServerSocket(listenHost, listenPort);
            _serverSocket = ss;
            _running = true;

            _acceptorThread = new Thread(() =>
            {
                LogD("Acceptor thread started");
                while (_running)
                {
                    try
                    {
                        var clientSocket = ss.AcceptSocket();
                        HandleConnection(clientSocket);
                    }
                    catch (Exception e)
                    {
                        if (_running) AppLog.W(TAG, $"Accept error: {e.Message}");
                    }
                }
                LogD("Acceptor thread exited");
            })
            {
                IsBackground = true,
                Name = "tor-bridge-acceptor"
            };
            _acceptorThread.Start();

            AppLog.I(TAG, $"Bridge started on {listenHost}:{listenPort}");
            return null;
        }
        catch (Exception e)
        {
            AppLog.E(TAG, "Failed to start bridge", e);
            Stop();
            return e;
        }
    }

    public static void Stop()
    {
        if (!_running && _serverSocket == null) return;
        LogD("Stopping bridge...");

        _running = false;
        try { _serverSocket?.Stop(); } catch { /* already down */ }
        _serverSocket = null;
        _acceptorThread = null;

        // Closing the tracked sockets is what actually unblocks the per
        // connection reader threads parked in Read().
        Socket[] toClose;
        lock (ActiveSocketsLock)
        {
            toClose = ActiveSockets.ToArray();
            ActiveSockets.Clear();
        }
        foreach (var s in toClose)
        {
            try { s.Close(); } catch { /* already gone */ }
        }

        lock (ConnectionThreadsLock)
        {
            _connectionThreads.Clear();
        }

        DnsCache.Clear();
        LogD("Bridge stopped");
    }

    public static bool IsRunning() => _running;

    /// <summary>
    /// Send new connections to a different Tor SOCKS port without rebinding.
    /// The listener keeps its own port, so every client socket that is already
    /// chained stays untouched: only connections opened from here on follow the
    /// new runner. This is what makes a recovery invisible — the bridge never
    /// goes down while a replacement Tor boots.
    /// </summary>
    public static void Repoint(int torSocksPort)
    {
        if (_torSocksPort != torSocksPort)
        {
            AppLog.I(TAG, $"Repointing bridge: {_torHost}:{_torSocksPort} -> {_torHost}:{torSocksPort}");
            _torSocksPort = torSocksPort;
        }
        // Cached answers were resolved over the circuit that just went away, so
        // drop them: a recovery reuses the same runner port, and returning early
        // on an unchanged port used to keep up to five minutes of name mappings
        // that were resolved by the dead Tor.
        DnsCache.Clear();
    }

    public static bool IsClientHealthy()
    {
        var ss = _serverSocket;
        return _running && ss != null;
    }

    private static TcpListener BindServerSocket(string host, int port)
    {
        SocketException? lastException = null;
        for (var attempt = 0; attempt < BindMaxRetries; attempt++)
        {
            var ss = new TcpListener(System.Net.IPAddress.Parse(host), port);
            try
            {
                ss.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                ss.Start();
                if (attempt > 0) AppLog.I(TAG, $"Port {port} bound after {attempt + 1} attempts");
                return ss;
            }
            catch (SocketException e)
            {
                lastException = e;
                try { ss.Stop(); } catch { /* nothing bound */ }
                if (attempt < BindMaxRetries - 1)
                {
                    AppLog.W(TAG,
                        $"Port {port} in use, retrying in {BindRetryDelayMs}ms " +
                        $"(attempt {attempt + 1}/{BindMaxRetries})");
                    Thread.Sleep(BindRetryDelayMs);
                }
            }
        }
        throw lastException ?? new SocketException();
    }

    private static void Track(Socket s)
    {
        lock (ActiveSocketsLock) ActiveSockets.Add(s);
    }

    private static void Untrack(Socket s)
    {
        lock (ActiveSocketsLock) ActiveSockets.Remove(s);
    }

    private static void HandleConnection(Socket clientSocket)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var socket = clientSocket;
                Track(clientSocket);
                try
                {
                    socket.ReceiveTimeout = 30_000;
                    socket.NoDelay = true;
                    var input = socket.GetStream();
                    var output = input;

                    // SOCKS5 greeting.
                    var version = input.ReadByte();
                    if (version == -1)
                    {
                        // EOF — readiness probe or client closed immediately;
                        // not an error.
                        return;
                    }
                    if (version != 0x05)
                    {
                        AppLog.W(TAG, $"Invalid SOCKS5 version: {version}");
                        return;
                    }
                    var nMethods = input.ReadByte();
                    if (nMethods < 0) return;
                    var methods = ReadFully(input, nMethods);

                    // Authenticate local client.
                    if (!LocalProxyAuth.HandleGreeting(
                            methods, input, output, _localAuthUsername, _localAuthPassword))
                    {
                        return;
                    }

                    // SOCKS5 request.
                    var ver = input.ReadByte();
                    var cmd = input.ReadByte();
                    input.ReadByte(); // reserved

                    if (ver != 0x05 || (cmd != 0x01 && cmd != 0x05))
                    {
                        WriteAll(output, new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                        return;
                    }

                    // Parse address.
                    var addrType = input.ReadByte();
                    string destHost;
                    byte[] rawAddr;

                    switch (addrType)
                    {
                        case 0x01: // IPv4
                        {
                            var addr = ReadFully(input, 4);
                            destHost = string.Join(".", addr.Select(b => (b & 0xFF).ToString()));
                            rawAddr = Cat(new byte[] { 0x01 }, addr);
                            break;
                        }
                        case 0x03: // Domain name
                        {
                            var len = input.ReadByte();
                            if (len < 0) return;
                            var domain = ReadFully(input, len);
                            destHost = Encoding.UTF8.GetString(domain);
                            rawAddr = Cat(new byte[] { 0x03, (byte)len }, domain);
                            break;
                        }
                        case 0x04: // IPv6
                        {
                            var addr = ReadFully(input, 16);
                            destHost = FormatIpv6(addr);
                            rawAddr = Cat(new byte[] { 0x04 }, addr);
                            break;
                        }
                        default:
                            WriteAll(output, new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                            return;
                    }

                    var portHigh = input.ReadByte();
                    var portLow = input.ReadByte();
                    if (portHigh < 0 || portLow < 0) return;
                    var destPort = (portHigh << 8) | portLow;
                    var portBytes = new byte[] { (byte)portHigh, (byte)portLow };

                    // Handle FWD_UDP (cmd 0x05).
                    if (cmd == 0x05)
                    {
                        socket.ReceiveTimeout = 0;
                        HandleFwdUdp(input, output);
                        return;
                    }

                    // Handle CONNECT (cmd 0x01) — chain through Tor SOCKS5.
                    HandleConnect(destHost, destPort, rawAddr, portBytes, socket, input, output);
                }
                finally
                {
                    Untrack(clientSocket);
                }
            }
            catch (Exception e)
            {
                if (_running) LogD($"Connection handler error: {e.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "tor-bridge-handler"
        };
        lock (ConnectionThreadsLock)
        {
            _connectionThreads.Add(thread);
            _connectionThreads.RemoveAll(t => !t.IsAlive);
        }
        thread.Start();
    }

    /// <summary>
    /// Handle SOCKS5 CONNECT by chaining through Tor's SOCKS5 proxy (no auth).
    /// With domain routing: sniffs TLS SNI / HTTP Host to decide bypass vs
    /// tunnel. Router ships disabled, so the plain path runs.
    /// </summary>
    private static void HandleConnect(
        string destHost,
        int destPort,
        byte[] rawAddr,
        byte[] portBytes,
        Socket clientSocket,
        Stream clientInput,
        Stream clientOutput)
    {
        var router = _router;
        if (router.Enabled)
        {
            HandleConnectWithRouting(
                router, destHost, destPort, rawAddr, portBytes, clientSocket, clientInput, clientOutput);
            return;
        }

        // Original flow — no domain routing.
        ConnectViaTor(destHost, destPort, rawAddr, portBytes, clientSocket, clientInput, clientOutput,
            sendReply: true);
    }

    private static void HandleConnectWithRouting(
        DomainRouter router,
        string destHost,
        int destPort,
        byte[] rawAddr,
        byte[] portBytes,
        Socket clientSocket,
        Stream clientInput,
        Stream clientOutput)
    {
        var effectiveHost = destHost;
        byte[]? sniffBuffer = null;
        var sniffLen = 0;
        var wasEarlyReply = false;

        if (DomainRouter.IsIpAddress(destHost))
        {
            // IP address — sniff to recover domain.
            WriteAll(clientOutput, SuccessReply());
            clientSocket.ReceiveTimeout = 3000;
            wasEarlyReply = true;

            var result = ProtocolSniffer.Sniff(clientInput);
            if (result.Domain != null)
            {
                effectiveHost = result.Domain;
                LogD($"CONNECT: sniffed domain={effectiveHost} from IP={destHost}");
            }
            sniffBuffer = result.BufferedData;
            sniffLen = result.BufferedLength;
        }

        if (router.ShouldBypass(effectiveHost))
        {
            // Direct connection — bypass tunnel.
            LogD($"CONNECT: bypassing tunnel for {effectiveHost}:{destPort}");
            try
            {
                var directSocket = DomainRouter.CreateDirectConnection(destHost, destPort);
                if (!wasEarlyReply) WriteAll(clientOutput, SuccessReply());
                clientSocket.ReceiveTimeout = 0;
                var effectiveInput = sniffLen > 0
                    ? new PrefixedStream(sniffBuffer!, sniffLen, clientInput)
                    : clientInput;
                BridgeDirect(effectiveInput, clientOutput, directSocket);
            }
            catch (Exception e)
            {
                LogD($"CONNECT: direct connection failed for {effectiveHost}:{destPort}: {e.Message}");
                if (!wasEarlyReply)
                {
                    try { WriteAll(clientOutput, FailReply(0x05)); } catch { /* client gone */ }
                }
            }
            return;
        }

        // Tunnel path.
        clientSocket.ReceiveTimeout = 0;
        var input = sniffLen > 0 ? new PrefixedStream(sniffBuffer!, sniffLen, clientInput) : clientInput;
        ConnectViaTor(destHost, destPort, rawAddr, portBytes, clientSocket, input, clientOutput,
            sendReply: !wasEarlyReply);
    }

    /// <summary>
    /// Connect through Tor SOCKS5 and bridge bidirectionally. When
    /// <paramref name="sendReply"/> is false the success reply was already sent
    /// (early reply for sniffing).
    /// </summary>
    private static void ConnectViaTor(
        string destHost,
        int destPort,
        byte[] rawAddr,
        byte[] portBytes,
        Socket clientSocket,
        Stream clientInput,
        Stream clientOutput,
        bool sendReply)
    {
        Socket remoteSocket;
        try
        {
            remoteSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            if (!remoteSocket.ConnectAsync(_torHost, _torSocksPort).Wait(TcpConnectTimeoutMs))
            {
                throw new TimeoutException("connect timed out");
            }
            remoteSocket.NoDelay = true;
            Track(remoteSocket);
        }
        catch (Exception e)
        {
            LogD($"CONNECT: failed to connect to Tor SOCKS5: {e.Message}");
            if (sendReply)
            {
                try { WriteAll(clientOutput, FailReply(0x05)); } catch { /* client gone */ }
            }
            return;
        }

        try
        {
            var remoteInput = remoteSocket.GetStream();
            var remoteOutput = remoteInput;

            // SOCKS5 greeting to Tor (no auth).
            WriteAll(remoteOutput, new byte[] { 0x05, 0x01, 0x00 });

            var greetResp = ReadFully(remoteInput, 2);
            if (greetResp[0] != 0x05 || greetResp[1] == 0xFF)
            {
                AppLog.W(TAG, "CONNECT: Tor rejected greeting");
                if (sendReply)
                {
                    try { WriteAll(clientOutput, FailReply(0x01)); } catch { /* client gone */ }
                }
                remoteSocket.Close();
                return;
            }

            // SOCKS5 CONNECT request to Tor.
            WriteAll(remoteOutput, Cat(new byte[] { 0x05, 0x01, 0x00 }, rawAddr, portBytes));

            // Read CONNECT response header.
            var connRespHeader = ReadFully(remoteInput, 4);
            if (connRespHeader[1] != 0x00)
            {
                LogD($"CONNECT: Tor rejected to {destHost}:{destPort} (rep={connRespHeader[1]})");
                if (sendReply)
                {
                    try { WriteAll(clientOutput, FailReply(connRespHeader[1])); } catch { /* client gone */ }
                }
                remoteSocket.Close();
                return;
            }

            // Read remaining response bytes based on address type.
            switch (connRespHeader[3])
            {
                case 0x01:
                    ReadFully(remoteInput, 6);
                    break;
                case 0x03:
                {
                    var len = remoteInput.ReadByte();
                    if (len >= 0) ReadFully(remoteInput, len + 2);
                    break;
                }
                case 0x04:
                    ReadFully(remoteInput, 18);
                    break;
            }

            LogD($"CONNECT: {destHost}:{destPort} OK (via Tor)");

            // Send success to the tunnel side (if not already sent).
            if (sendReply) WriteAll(clientOutput, SuccessReply());

            clientSocket.ReceiveTimeout = 0;

            // Bridge bidirectionally.
            using (remoteSocket)
            {
                var t1 = new Thread(() =>
                {
                    try
                    {
                        CopyStream(clientInput, remoteOutput);
                    }
                    catch { /* either side closed */ }
                    finally
                    {
                        try { remoteOutput.Close(); } catch { /* already gone */ }
                    }
                })
                {
                    IsBackground = true,
                    Name = "tor-bridge-c2s"
                };
                t1.Start();

                try
                {
                    CopyStream(remoteInput, clientOutput);
                }
                catch { /* either side closed */ }
                finally
                {
                    try { remoteSocket.Close(); } catch { /* already gone */ }
                }
            }
        }
        catch (Exception e)
        {
            LogD($"CONNECT: chain error for {destHost}:{destPort}: {e.Message}");
            if (sendReply)
            {
                try { WriteAll(clientOutput, FailReply(0x01)); } catch { /* client gone */ }
            }
            try { remoteSocket.Close(); } catch { /* already gone */ }
        }
        finally
        {
            Untrack(remoteSocket);
        }
    }

    /// <summary>
    /// Handle FWD_UDP (cmd 0x05) — same wire format as the other bridges. DNS
    /// (port 53): DNS-over-TCP through Tor SOCKS5 CONNECT, so no query ever
    /// leaves the machine in the clear. Non-DNS UDP: dropped silently.
    ///
    /// DNS queries are dispatched for concurrent resolution and responses are
    /// cached for 5 minutes to keep repeat lookups off the wire.
    /// </summary>
    private static void HandleFwdUdp(Stream input, Stream output)
    {
        WriteAll(output, SuccessReply());
        LogD("FWD_UDP session established");

        while (_running)
        {
            var hdr = ReadFully(input, 3);

            var datLen = (hdr[0] << 8) | hdr[1];
            var hdrLen = hdr[2];
            var addrLen = hdrLen - 3;

            if (addrLen <= 0 || datLen <= 0)
            {
                AppLog.W(TAG, $"FWD_UDP: invalid header (datLen={datLen}, hdrLen={hdrLen})");
                break;
            }

            var addrBytes = ReadFully(input, addrLen);
            var payload = ReadFully(input, datLen);

            var dest = ParseSocksAddress(addrBytes);
            if (dest == null)
            {
                AppLog.W(TAG, "FWD_UDP: failed to parse address");
                continue;
            }

            if (dest.Value.Port != 53)
            {
                // Non-DNS UDP: drop silently.
                continue;
            }

            // Dispatch DNS query for concurrent resolution.
            var addrCopy = addrBytes;
            var payloadCopy = payload;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _dnsSlots.Wait();
                    try
                    {
                        // Check cache first.
                        var cached = GetCachedDns(payloadCopy);
                        byte[]? response;
                        if (cached != null)
                        {
                            LogD("DNS: cache hit");
                            response = cached;
                        }
                        else
                        {
                            response = ResolveThroughTor(payloadCopy);
                            if (response != null) CacheDnsResponse(payloadCopy, response);
                        }

                        if (response != null && response.Length > 0)
                        {
                            var respHdr = new byte[3];
                            respHdr[0] = (byte)((response.Length >> 8) & 0xFF);
                            respHdr[1] = (byte)(response.Length & 0xFF);
                            respHdr[2] = (byte)(3 + addrCopy.Length);

                            lock (output)
                            {
                                WriteAll(output, respHdr);
                                WriteAll(output, addrCopy);
                                WriteAll(output, response);
                            }
                        }
                    }
                    finally
                    {
                        _dnsSlots.Release();
                    }
                }
                catch (Exception e)
                {
                    LogD($"FWD_UDP: DNS forward failed: {e.Message}");
                }
            });
        }

        LogD("FWD_UDP session ended");
    }

    // --- DNS cache ---

    /// <summary>
    /// Cache key: hex of DNS query bytes after the 2-byte transaction ID, so
    /// the same query with different TXIDs hits the cache.
    /// </summary>
    private static string DnsCacheKey(byte[] query)
    {
        if (query.Length <= 2) return "";
        return Convert.ToHexString(query, 2, query.Length - 2).ToLowerInvariant();
    }

    private static byte[]? GetCachedDns(byte[] query)
    {
        var key = DnsCacheKey(query);
        if (key.Length == 0) return null;
        if (!DnsCache.TryGetValue(key, out var entry)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > entry.ExpiresAt)
        {
            DnsCache.TryRemove(key, out _);
            return null;
        }
        // Copy cached response and replace transaction ID with query's.
        var response = entry.Response.ToArray();
        if (response.Length >= 2 && query.Length >= 2)
        {
            response[0] = query[0];
            response[1] = query[1];
        }
        return response;
    }

    private static void CacheDnsResponse(byte[] query, byte[] response)
    {
        var key = DnsCacheKey(query);
        if (key.Length == 0) return;
        DnsCache[key] = new DnsCacheEntry(
            response.ToArray(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DnsCacheTtlMs);
        // Evict expired entries once the cache grows large.
        if (DnsCache.Count > 500)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var kv in DnsCache.Where(kv => now > kv.Value.ExpiresAt).ToList())
            {
                DnsCache.TryRemove(kv.Key, out _);
            }
        }
    }

    // --- DNS-over-TCP via Tor SOCKS5 ---

    /// <summary>
    /// Resolvers tried in order, all of them reached over Tor. One being slow,
    /// blocked at the exit or simply broken should not take name resolution
    /// down with it.
    /// </summary>
    private static readonly byte[][] DnsFallbackResolvers =
    {
        new byte[] { 8, 8, 8, 8 },   // Google
        new byte[] { 1, 1, 1, 1 },   // Cloudflare
        new byte[] { 9, 9, 9, 9 }    // Quad9
    };

    /// <summary>Ask each resolver in turn over Tor; null when none answers.</summary>
    private static byte[]? ResolveThroughTor(byte[] payload)
    {
        foreach (var resolver in DnsFallbackResolvers)
        {
            var response = ForwardDnsTcp(resolver, payload);
            if (response != null) return response;
        }
        AppLog.W(TAG, "DNS: no resolver answered over Tor");
        return null;
    }

    /// <summary>
    /// Forward DNS query as DNS-over-TCP through Tor's SOCKS5 CONNECT to
    /// <paramref name="resolver"/>:53. Each call opens a new SOCKS5 connection.
    /// </summary>
    private static byte[]? ForwardDnsTcp(byte[] resolver, byte[] payload)
    {
        Socket? socket = null;
        try
        {
            // Connect to Tor SOCKS5.
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            if (!socket.ConnectAsync(_torHost, _torSocksPort).Wait(DnsTimeoutMs))
            {
                return null;
            }
            socket.ReceiveTimeout = DnsTimeoutMs;
            socket.SendTimeout = DnsTimeoutMs;
            socket.NoDelay = true;

            var stream = socket.GetStream();
            var input = stream;
            var output = stream;

            // SOCKS5 greeting (no auth).
            WriteAll(output, new byte[] { 0x05, 0x01, 0x00 });

            var greetResp = ReadFully(input, 2);
            if (greetResp[0] != 0x05)
            {
                LogD("DNS: Tor SOCKS5 greeting failed");
                return null;
            }

            // SOCKS5 CONNECT to the resolver on port 53.
            WriteAll(output, Cat(new byte[] { 0x05, 0x01, 0x00, 0x01 }, resolver, new byte[] { 0x00, 0x35 }));

            // Read CONNECT response.
            var connResp = ReadFully(input, 4);
            if (connResp[1] != 0x00)
            {
                LogD($"DNS: SOCKS5 CONNECT rejected (rep={connResp[1]})");
                return null;
            }

            // Skip remaining response bytes based on address type.
            switch (connResp[3])
            {
                case 0x01:
                    ReadFully(input, 6);
                    break;
                case 0x03:
                {
                    var len = input.ReadByte();
                    if (len >= 0) ReadFully(input, len + 2);
                    break;
                }
                case 0x04:
                    ReadFully(input, 18);
                    break;
            }

            // Send DNS query as TCP (2-byte length prefix per RFC 1035 4.2.2).
            WriteAll(output, new byte[]
            {
                (byte)((payload.Length >> 8) & 0xFF),
                (byte)(payload.Length & 0xFF)
            });
            WriteAll(output, payload);

            // Read DNS response (2-byte length prefix).
            var respLenBuf = ReadFully(input, 2);
            var respLen = (respLenBuf[0] << 8) | respLenBuf[1];
            if (respLen <= 0 || respLen > 65535)
            {
                LogD($"DNS: invalid response length: {respLen}");
                return null;
            }

            var response = ReadFully(input, respLen);
            LogD($"DNS: resolved via Tor ({response.Length} bytes)");
            return response;
        }
        catch (Exception e)
        {
            LogD($"DNS-over-TCP failed: {e.Message}");
            return null;
        }
        finally
        {
            try { socket?.Close(); } catch { /* already gone */ }
        }
    }

    private static (string Host, int Port)? ParseSocksAddress(byte[] addrBytes)
    {
        if (addrBytes.Length == 0) return null;

        switch (addrBytes[0])
        {
            case 0x01:
            {
                if (addrBytes.Length < 7) return null;
                var host = $"{addrBytes[1]}.{addrBytes[2]}.{addrBytes[3]}.{addrBytes[4]}";
                var port = (addrBytes[5] << 8) | addrBytes[6];
                return (host, port);
            }
            case 0x03:
            {
                var len = addrBytes[1];
                if (addrBytes.Length < 2 + len + 2) return null;
                var host = Encoding.UTF8.GetString(addrBytes, 2, len);
                var port = (addrBytes[2 + len] << 8) | addrBytes[3 + len];
                return (host, port);
            }
            case 0x04:
            {
                if (addrBytes.Length < 19) return null;
                var parts = new List<string>();
                for (var i = 0; i < 16; i += 2)
                {
                    var value = (addrBytes[1 + i] << 8) | addrBytes[2 + i];
                    parts.Add(value.ToString("x"));
                }
                var host = string.Join(":", parts);
                var port = (addrBytes[17] << 8) | addrBytes[18];
                return (host, port);
            }
            default:
                return null;
        }
    }

    private static void BridgeDirect(Stream clientInput, Stream clientOutput, Socket directSocket)
    {
        using var remote = directSocket;
        remote.NoDelay = true;
        var remoteInput = remote.GetStream();
        var remoteOutput = remoteInput;

        var t1 = new Thread(() =>
        {
            try
            {
                CopyStream(clientInput, remoteOutput);
            }
            catch { /* either side closed */ }
            finally
            {
                try { remoteOutput.Close(); } catch { /* already gone */ }
            }
        })
        {
            IsBackground = true,
            Name = "tor-bridge-direct-c2s"
        };
        t1.Start();

        try
        {
            CopyStream(remoteInput, clientOutput);
        }
        catch { /* either side closed */ }
        finally
        {
            try { remote.Close(); } catch { /* already gone */ }
        }
    }

    private static void CopyStream(Stream input, Stream output)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = input.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                break;
            }
            if (bytesRead <= 0) break;
            try
            {
                output.Write(buffer, 0, bytesRead);
                output.Flush();
            }
            catch
            {
                break;
            }
        }
        try { output.Flush(); } catch { /* already closed */ }
    }

    private static byte[] ReadFully(Stream input, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = input.Read(buffer, offset, count - offset);
            if (read <= 0) throw new IOException("Unexpected end of stream");
            offset += read;
        }
        return buffer;
    }

    private static void WriteAll(Stream output, byte[] data) =>
        output.Write(data, 0, data.Length);

    private static byte[] Cat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] SuccessReply() => new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    private static byte[] FailReply(int rep) =>
        new byte[] { 0x05, (byte)rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    private static string FormatIpv6(byte[] addr)
    {
        var parts = new List<string>();
        for (var i = 0; i < 16; i += 2)
        {
            var value = (addr[i] << 8) | addr[i + 1];
            parts.Add(value.ToString("x"));
        }
        return string.Join(":", parts);
    }

    /// <summary>
    /// Replay of sniffed bytes before the live stream: the sniffer consumed the
    /// first read, and those exact bytes must reach whichever socket the
    /// connection was finally routed to.
    /// </summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly MemoryStream _prefix;
        private readonly Stream _inner;

        public PrefixedStream(byte[] data, int length, Stream inner)
        {
            _prefix = new MemoryStream(data, 0, length, writable: false);
            _inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var fromPrefix = _prefix.Read(buffer, offset, count);
            if (fromPrefix > 0) return fromPrefix;
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override void Flush() => _inner.Flush();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _prefix.Dispose();
            base.Dispose(disposing);
        }
    }
}

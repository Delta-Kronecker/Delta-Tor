using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace DeltaTor.Core.Sstp;

/// <summary>
/// The Windows counterpart of kittoku's <c>SSLTerminal</c>: dials Tor's SOCKS5
/// bridge, runs a raw SOCKS5 CONNECT (the destination is sent as a domain so the
/// far side resolves it), wraps the socket in TLS with optional verification,
/// and issues the SSTP_DUPLEX_POST handshake. Everything after this is plain
/// SSTP framing over <see cref="SslStream"/> — the Windows stand-in for the
/// SSLEngine the Android build drives by hand.
/// </summary>
internal sealed class SslTerminal
{
    private const string HttpDelimiter = "\r\n";
    private const string HttpSuffix = "\r\n\r\n";

    /// <summary>Slightly above the original: the TCP hop goes through Tor first, so
    /// the whole TLS+HTTP+SSTP negotiation carries the extra circuit latency.</summary>
    public const int SslRequestInterval = 15_000;

    private const int ProxyConnectTimeout = 20_000;

    /// <summary>Stand-in for SSLEngine's negotiated application buffer size. The
    /// core only uses it to size its working buffers, so a generous TLS-record
    /// ceiling keeps every read/write single-shot.</summary>
    private const int ApplicationBufferSize = 16 * 1024;

    private readonly SharedBridge _bridge;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _sslHostname;
    private readonly int _sslPort;

    private Socket? _socket;
    private NetworkStream? _network;
    private SslStream? _ssl;

    public SslTerminal(SharedBridge bridge)
    {
        _bridge = bridge;
        _sslHostname = bridge.Params.Hostname;
        _sslPort = bridge.Params.Port;
    }

    public void Initialize() => _bridge.Launch(InitializeAsync);

    private async Task InitializeAsync()
    {
        if (!await EstablishSslAsync().ConfigureAwait(false))
        {
            return;
        }

        if (!await EstablishHttpAsync().ConfigureAwait(false))
        {
            return;
        }

        _bridge.SendControlMessage(new ControlMessage(Where.SSL, Result.PROCEEDED));
    }

    private bool Fatal(Where where, Result result, string? supplement = null)
    {
        _bridge.SendControlMessage(new ControlMessage(where, result, supplement));
        return false;
    }

    private async Task ReadFullyAsync(byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await _network!.ReadAsync(buffer.AsMemory(offset), CancellationToken.None).ConfigureAwait(false);
            if (n < 0 || n == 0)
            {
                throw new IOException("proxy closed the connection");
            }

            offset += n;
        }
    }

    private async Task WriteAllAsync(byte[] buffer)
    {
        await _network!.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        await _network.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>SOCKS5 handshake against Tor (no authentication), keep-alive tunnel.</summary>
    private async Task<bool> EstablishProxyAsync()
    {
        // method negotiation: client says "I offer NO-AUTH only"
        await WriteAllAsync(new byte[] { 0x05, 0x01, 0x00 }).ConfigureAwait(false);

        var greeting = new byte[2];
        await ReadFullyAsync(greeting).ConfigureAwait(false);
        if (greeting[0] != 0x05)
        {
            return Fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, "not a SOCKS5 proxy");
        }

        if (greeting[1] != 0x00)
        {
            return Fatal(Where.PROXY, Result.ERR_AUTHENTICATION_FAILED, "SOCKS5 did not accept NO-AUTH");
        }

        // CONNECT to the VPN Gate endpoint; the hostname is never resolved
        // locally, Tor does it on the far side of the circuit.
        var request = new List<byte> { 0x05, 0x01, 0x00 };
        if (IsIPv4Literal(_sslHostname))
        {
            request.Add(0x01);
            foreach (var part in _sslHostname.Split('.'))
            {
                request.Add((byte)int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else if (_sslHostname.Contains(':'))
        {
            request.Add(0x04);
            request.AddRange(System.Net.IPAddress.Parse(_sslHostname).GetAddressBytes());
        }
        else
        {
            var name = System.Text.Encoding.ASCII.GetBytes(_sslHostname);
            request.Add(0x03);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }

        request.Add((byte)(_sslPort >> 8));
        request.Add((byte)_sslPort);

        await WriteAllAsync(request.ToArray()).ConfigureAwait(false);

        var head = new byte[4];
        await ReadFullyAsync(head).ConfigureAwait(false);
        if (head[0] != 0x05)
        {
            return Fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, "broken SOCKS5 reply");
        }

        if (head[1] != 0x00)
        {
            return Fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, $"SOCKS5 CONNECT refused (rep={head[1]})");
        }

        // skim the bound address so the stream sits exactly at the TLS bytes
        switch (head[3])
        {
            case 0x01:
                await ReadFullyAsync(new byte[4]).ConfigureAwait(false);
                break;
            case 0x03:
                var length = new byte[1];
                await ReadFullyAsync(length).ConfigureAwait(false);
                await ReadFullyAsync(new byte[length[0]]).ConfigureAwait(false);
                break;
            case 0x04:
                await ReadFullyAsync(new byte[16]).ConfigureAwait(false);
                break;
        }

        await ReadFullyAsync(new byte[2]).ConfigureAwait(false);

        return true;
    }

    private static bool IsIPv4Literal(string host) =>
        System.Text.RegularExpressions.Regex.IsMatch(host, "^[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3}$");

    private async Task<bool> EstablishSslAsync()
    {
        var token = _bridge.Token;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectCts.CancelAfter(ProxyConnectTimeout);
            try
            {
                await _socket.ConnectAsync(_bridge.Params.ProxyHostname, _bridge.Params.ProxyPort, connectCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return Fatal(Where.PROXY, Result.ERR_TIMEOUT, "SOCKS5 connect timed out");
            }
        }

        _network = new NetworkStream(_socket, ownsSocket: false);

        if (!await EstablishProxyAsync().ConfigureAwait(false))
        {
            return false;
        }

        var verifyCert = _bridge.Params.DoVerifyCert;
        _ssl = new SslStream(_network, leaveInnerStreamOpen: false, (_, _, _, errors) =>
            !verifyCert || errors == SslPolicyErrors.None);

        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = _sslHostname,
                EnabledSslProtocols = SslProtocols.None
            };
            await _ssl.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
        }
        catch (AuthenticationException e)
        {
            return Fatal(Where.CERT_PATH, Result.ERR_VERIFICATION_FAILED, e.Message);
        }

        return true;
    }

    private async Task<bool> EstablishHttpAsync()
    {
        var token = _bridge.Token;

        var request = string.Join(
            HttpDelimiter,
            "SSTP_DUPLEX_POST /sra_{BA195980-CD49-458b-9E23-C84EE0ADCD75}/ HTTP/1.1",
            "Content-Length: 18446744073709551615",
            $"Host: {_sslHostname}",
            $"SSTPCORRELATIONID: {{{_bridge.Guid}}}")
            + HttpSuffix;

        var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
        await _ssl!.WriteAsync(requestBytes, CancellationToken.None).ConfigureAwait(false);
        await _ssl.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var response = new System.Text.StringBuilder();
        var buffer = new byte[4096];
        var done = false;
        while (!done)
        {
            var n = await _ssl.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (n <= 0)
            {
                return Fatal(Where.SSL, Result.ERR_UNEXPECTED_MESSAGE, "SSTP_DUPLEX_POST response closed");
            }

            for (var i = 0; i < n && !done; i++)
            {
                response.Append((char)(buffer[i] & 0xFF));
                if (response.Length >= HttpSuffix.Length &&
                    response.ToString(response.Length - HttpSuffix.Length, HttpSuffix.Length) == HttpSuffix)
                {
                    done = true;
                }
            }
        }

        var firstLine = response.ToString().Split(HttpDelimiter)[0];
        if (!firstLine.Contains("200"))
        {
            return Fatal(Where.SSL, Result.ERR_UNEXPECTED_MESSAGE, firstLine);
        }

        _socket!.ReceiveTimeout = 1_000;
        _bridge.Protect(_socket);
        return true;
    }

    public int ApplicationBuffer => ApplicationBufferSize;

    public byte[] GetServerCertificate()
    {
        var certificate = _ssl?.RemoteCertificate;
        return certificate is null ? System.Array.Empty<byte>() : certificate.GetRawCertData();
    }

    /// <summary>
    /// True for the IOException a socket read throws when its ReceiveTimeout
    /// elapses (WSAETIMEDOUT on Windows, WouldBlock elsewhere). Android's
    /// <c>receive</c> catches <c>SocketTimeoutException</c> for exactly this
    /// reason: one quiet second on a Tor circuit means "nothing yet", not
    /// "the session is dead".
    /// </summary>
    private static bool IsReceiveTimeout(IOException e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket &&
                (socket.SocketErrorCode == SocketError.TimedOut ||
                 socket.SocketErrorCode == SocketError.WouldBlock))
            {
                return true;
            }
        }

        return false;
    }

    public void Receive(ByteBuffer buffer)
    {
        var start = buffer.Position;
        var writeAt = buffer.Limit;

        if (writeAt >= buffer.Capacity)
        {
            buffer.Slide();
            start = buffer.Position;
            writeAt = buffer.Limit;
        }

        var max = buffer.Capacity - writeAt;

        int n;
        try
        {
            n = _ssl!.Read(buffer.Array, writeAt, max);
        }
        catch (IOException e) when (IsReceiveTimeout(e))
        {
            // Return with no bytes rather than failing the session: the
            // incoming loop keeps ticking its SSTP/PPP echo timers while it
            // waits, and reads again on the next pass. This is the Windows
            // counterpart of Android's swallowed SocketTimeoutException — and
            // it also means a genuinely silent server is caught by the echo
            // deadline, not by a one-second hiccup.
            return;
        }

        if (n <= 0)
        {
            throw new IOException("SSTP TLS stream closed");
        }

        buffer.Limit = writeAt + n;
        buffer.Position = start;
    }

    public async Task SendAsync(ByteBuffer buffer)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var length = buffer.Remaining;
            if (length > 0)
            {
                await _ssl!.WriteAsync(buffer.Array.AsMemory(buffer.Position, length), CancellationToken.None)
                    .ConfigureAwait(false);
                await _ssl.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                buffer.Position += length;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close()
    {
        try
        {
            _ssl?.Dispose();
        }
        catch
        {
            // already gone
        }

        try
        {
            _network?.Dispose();
        }
        catch
        {
            // already gone
        }

        try
        {
            _socket?.Close();
        }
        catch
        {
            // already gone
        }
    }
}

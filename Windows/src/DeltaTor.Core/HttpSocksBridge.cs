using System.Net;
using System.Net.Sockets;
using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// The HTTP side of System proxy mode. Windows writes the machine proxy as a
/// bare "host:port" (the HTTP-proxy form: address 127.0.0.1, no scheme, no
/// "socks=" prefix) and WinINET/Chromium speak the plain HTTP proxy protocol
/// to it — CONNECT for HTTPS, absolute-form requests for plain HTTP. Tor only
/// speaks SOCKS5, so this is the bridge between the two: it accepts both
/// forms on 127.0.0.1:<see cref="Port"/>, opens a SOCKS5 stream to Tor at the
/// winner's listener (remote DNS through Tor) and relays the bytes both ways.
/// Started by <see cref="SystemProxy.Apply"/>, stopped by
/// <see cref="SystemProxy.Clear"/>.
/// </summary>
public static class HttpSocksBridge
{
    /// <summary>Where the machine proxy points; Tor's SOCKS stays on Config.ProxyPort.</summary>
    public const int Port = 9070;

    private static readonly object Lock = new();
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;

    public static bool IsRunning
    {
        get { lock (Lock) return _listener != null; }
    }

    /// <summary>Start listening (idempotent). False only when the port is taken.</summary>
    public static bool Start()
    {
        lock (Lock)
        {
            if (_listener != null) return true;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                var cts = new CancellationTokenSource();
                _listener = listener;
                _cts = cts;
                _ = Task.Run(() => AcceptLoop(listener, cts.Token));
                AppLog.I("HttpProxy", $"HTTP→SOCKS bridge listening on 127.0.0.1:{Port}");
                return true;
            }
            catch (Exception e)
            {
                AppLog.W("HttpProxy", $"Bridge failed to start: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Stop listening for new requests (idempotent). Tunnels that
    /// are already open finish on their own — their peers close them.</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            _cts?.Cancel();
            _cts = null;
            try { _listener?.Stop(); } catch { /* already down */ }
            _listener = null;
        }
    }

    private static void AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch
            {
                return; // Stop() closed the listener
            }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    // ---- one request --------------------------------------------------------

    private static void HandleClient(TcpClient client)
    {
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 30_000;
            stream.WriteTimeout = 30_000;

            // Read the request head up to the blank line (cap: 64 KiB).
            var head = new List<byte>(1024);
            var chunk = new byte[4096];
            while (!HasHeaderEnd(head))
            {
                var n = stream.Read(chunk, 0, chunk.Length);
                if (n <= 0) return;
                for (var i = 0; i < n; i++) head.Add(chunk[i]);
                if (head.Count > 65_536) return;
            }

            var headEnd = IndexOfHeaderEnd(head);
            var headText = Encoding.ASCII.GetString(head.ToArray(), 0, headEnd);
            var leftover = new byte[head.Count - (headEnd + 4)];
            for (var i = 0; i < leftover.Length; i++)
                leftover[i] = head[headEnd + 4 + i];

            var lineEnd = headText.IndexOf("\r\n", StringComparison.Ordinal);
            var requestLine = lineEnd > 0 ? headText[..lineEnd] : headText;
            var parts = requestLine.Split(' ', 3);
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();

            string host;
            int port;
            byte[]? toUpstream; // bytes to send right after the SOCKS stream opens

            if (method == "CONNECT")
            {
                var authority = parts[1];
                var colon = authority.LastIndexOf(':');
                host = colon > 0 ? authority[..colon] : authority;
                port = colon > 0 && int.TryParse(authority[(colon + 1)..], out var p) && p > 0
                    ? p
                    : 443;
                toUpstream = leftover.Length > 0 ? leftover : null;
            }
            else
            {
                if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttp)
                    return;
                host = uri.Host;
                port = uri.Port;
                // Origin-form for Tor; drop the proxy hop headers.
                var origin = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
                var sb = new StringBuilder();
                sb.Append(parts[0]).Append(' ').Append(origin).Append(' ').Append(version)
                    .Append("\r\n");
                var rawLines = headText.Split("\r\n");
                for (var i = 1; i < rawLines.Length; i++)
                {
                    var l = rawLines[i];
                    if (l.Length == 0) continue;
                    if (l.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                        l.StartsWith("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.Append(l).Append("\r\n");
                }
                sb.Append("\r\n");
                var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                if (leftover.Length > 0)
                {
                    var all = new byte[headBytes.Length + leftover.Length];
                    headBytes.CopyTo(all, 0);
                    leftover.CopyTo(all, headBytes.Length);
                    toUpstream = all;
                }
                else
                {
                    toUpstream = headBytes;
                }
            }

            upstream = new TcpClient();
            upstream.NoDelay = true;
            if (!SocksOpen(upstream, host, port)) return;

            var up = upstream.GetStream();
            // Handshake done: give the tunnel all the time it needs (an idle
            // HTTPS tab must not be killed by a receive timeout).
            stream.ReadTimeout = 0;
            stream.WriteTimeout = 0;
            upstream.ReceiveTimeout = 0;
            upstream.SendTimeout = 0;

            if (method == "CONNECT")
            {
                var ok = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 Connection Established\r\n\r\n");
                stream.Write(ok, 0, ok.Length);
            }
            if (toUpstream is { Length: > 0 })
                up.Write(toUpstream, 0, toUpstream.Length);

            Relay(client, upstream);
        }
        catch (Exception e)
        {
            AppLog.D("HttpProxy", $"client dropped: {e.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
        }
    }

    /// <summary>SOCKS5 to Tor: no auth, CONNECT, domain stays a domain so the
    /// remote DNS resolves through Tor. False on any protocol failure.</summary>
    private static bool SocksOpen(TcpClient up, string host, int port)
    {
        try
        {
            up.ReceiveTimeout = 30_000;
            up.SendTimeout = 30_000;
            up.Connect("127.0.0.1", Config.ProxyPort);
            var s = up.GetStream();
            s.Write(new byte[] { 0x05, 0x01, 0x00 });
            var greet = ReadExact(s, 2);
            if (greet == null || greet[0] != 0x05 || greet[1] != 0x00) return false;

            using var req = new MemoryStream();
            req.WriteByte(0x05);
            req.WriteByte(0x01);
            req.WriteByte(0x00);
            if (IPAddress.TryParse(host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                req.WriteByte(bytes.Length == 16 ? (byte)0x04 : (byte)0x01);
                req.Write(bytes);
            }
            else
            {
                var hb = Encoding.ASCII.GetBytes(host);
                if (hb.Length == 0 || hb.Length > 255) return false;
                req.WriteByte(0x03);
                req.WriteByte((byte)hb.Length);
                req.Write(hb);
            }
            req.WriteByte((byte)(port >> 8));
            req.WriteByte((byte)(port & 0xFF));
            var reqBytes = req.ToArray();
            s.Write(reqBytes, 0, reqBytes.Length);

            var reply = ReadExact(s, 4);
            if (reply == null || reply[1] != 0x00) return false;
            switch (reply[3])
            {
                case 0x01: ReadExact(s, 4); break;
                case 0x03:
                    var len = ReadExact(s, 1);
                    if (len != null) ReadExact(s, len[0]);
                    break;
                case 0x04: ReadExact(s, 16); break;
                default: return false;
            }
            return true;
        }
        catch (Exception e)
        {
            AppLog.W("HttpProxy", $"SOCKS open to {host}:{port} failed: {e.Message}");
            return false;
        }
    }

    // ---- byte plumbing ------------------------------------------------------

    private static byte[]? ReadExact(NetworkStream s, int count)
    {
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = s.Read(buf, got, count - got);
            if (n <= 0) return null;
            got += n;
        }
        return buf;
    }

    /// <summary>Two copies until one side is done. Each side then half-closes
    /// its own send direction, which is what makes the other copy see EOF too
    /// (Tor closes its half when ours closes, the browser closes when Tor's
    /// close reaches it) — no thread is left blocked on a read.</summary>
    private static void Relay(TcpClient client, TcpClient upstream)
    {
        var cs = client.GetStream();
        var us = upstream.GetStream();
        var t = new Thread(() =>
        {
            try
            {
                Copy(us, cs);
            }
            catch { /* broken pipe */ }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        })
        { IsBackground = true };
        t.Start();
        try
        {
            Copy(cs, us);
        }
        catch { /* broken pipe */ }
        finally
        {
            try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private static void Copy(NetworkStream from, NetworkStream to)
    {
        var buf = new byte[16 * 1024];
        int n;
        while ((n = from.Read(buf, 0, buf.Length)) > 0)
            to.Write(buf, 0, n);
    }

    private static bool HasHeaderEnd(List<byte> bytes) => IndexOfHeaderEnd(bytes) >= 0;

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (var i = 0; i + 3 < bytes.Count; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n' &&
                bytes[i + 2] == (byte)'\r' && bytes[i + 3] == (byte)'\n')
                return i;
        }
        return -1;
    }
}

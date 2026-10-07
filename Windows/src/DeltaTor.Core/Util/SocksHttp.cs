using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace DeltaTor.Core.Util;

/// <summary>
/// Minimal HTTP client that speaks SOCKS5 itself.
///
/// .NET's HttpClient only understands HTTP proxies, so a request that must go
/// through a SOCKS5 hop (the app's tunnel — the exit lookup deliberately asks
/// "where does MY traffic leave", which only works from inside the circuit)
/// needs its own handshake. The destination name is sent to the SOCKS server as
/// a domain (ATYP 0x03), so the DNS lookup happens at the far end too — the
/// same behaviour the Android client gets from java.net.Proxy.
/// </summary>
public static class SocksHttp
{
    /// <summary>
    /// GET <paramref name="url"/> through the SOCKS5 proxy at
    /// <paramref name="socksHost"/>:<paramref name="socksPort"/>. Redirects are
    /// not followed (parity with the Android https path). Throws on any
    /// transport or status failure.
    /// </summary>
    public static string Get(string socksHost, int socksPort, string url, int timeoutMs)
    {
        var uri = new Uri(url);
        using var tcp = new TcpClient();
        var connect = tcp.ConnectAsync(socksHost, socksPort);
        if (!connect.Wait(timeoutMs)) throw new TimeoutException("SOCKS connect timed out");
        tcp.NoDelay = true;
        tcp.Client.ReceiveTimeout = timeoutMs;
        tcp.Client.SendTimeout = timeoutMs;
        var stream = tcp.GetStream();

        // RFC 1928 greeting: version 5, one method, no-auth.
        stream.Write(new byte[] { 0x05, 0x01, 0x00 });
        var greet = ReadFully(stream, 2);
        if (greet[0] != 0x05) throw new IOException($"SOCKS: bad version {greet[0]}");
        if (greet[1] == 0xFF) throw new IOException("SOCKS: no acceptable auth method");
        if (greet[1] != 0x00) throw new IOException($"SOCKS: unexpected auth method {greet[1]}");

        // CONNECT with the hostname as a domain, so resolution happens far end.
        var hostBytes = Encoding.ASCII.GetBytes(uri.Host);
        var request = new List<byte>(7 + hostBytes.Length)
        {
            0x05, 0x01, 0x00, 0x03, (byte)hostBytes.Length
        };
        request.AddRange(hostBytes);
        request.Add((byte)(uri.Port >> 8));
        request.Add((byte)(uri.Port & 0xFF));
        stream.Write(request.ToArray());
        stream.Flush();

        var header = ReadFully(stream, 4);
        if (header[1] != 0x00) throw new IOException($"SOCKS: connect refused (rep={header[1]})");
        switch (header[3])
        {
            case 0x01:
                ReadFully(stream, 6);
                break;
            case 0x04:
                ReadFully(stream, 18);
                break;
            case 0x03:
                var len = ReadFully(stream, 1)[0];
                ReadFully(stream, len + 2);
                break;
        }

        Stream io = stream;
        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsClient(uri.Host);
            io = ssl;
        }

        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var head =
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {uri.Host}\r\n" +
            "User-Agent: DeltaTor/2.0\r\n" +
            "Accept: */*\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        io.Write(headBytes, 0, headBytes.Length);
        io.Flush();

        // Connection: close → read to EOF, then split headers/body.
        using var body = new MemoryStream();
        io.CopyTo(body);
        var all = body.ToArray();
        var split = IndexOfHeadersEnd(all);
        if (split < 0) throw new IOException($"HTTP: no header terminator from {url}");
        var text = Encoding.UTF8.GetString(all, 0, split);
        var statusLine = text.Split('\n')[0].Trim();
        if (!statusLine.Contains(" 200", StringComparison.Ordinal))
        {
            throw new IOException($"HTTP: {statusLine}");
        }
        var start = split + 4;
        return Encoding.UTF8.GetString(all, start, all.Length - start);
    }

    /// <summary>Index of the first byte after the "\r\n\r\n" header terminator.</summary>
    private static int IndexOfHeadersEnd(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static byte[] ReadFully(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) throw new IOException("SOCKS: unexpected end of stream");
            offset += read;
        }
        return buffer;
    }
}

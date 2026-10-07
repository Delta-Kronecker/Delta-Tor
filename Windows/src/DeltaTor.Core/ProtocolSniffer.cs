using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Sniffs TLS SNI and HTTP Host headers from the first bytes of a connection.
/// Used for domain-based routing when the TUN side sends SOCKS5 CONNECT with IP
/// addresses (the TUN only sees IPs, not domain names).
/// </summary>
public static class ProtocolSniffer
{
    private const string TAG = "ProtocolSniffer";
    private const int MaxSniffSize = 4096;

    /// <param name="Domain">The sniffed domain, when one could be read.</param>
    /// <param name="BufferedData">Bytes read from the client (must be prepended before forwarding).</param>
    /// <param name="BufferedLength">How many of BufferedData are valid.</param>
    public sealed record SniffResult(string? Domain, byte[] BufferedData, int BufferedLength);

    private static readonly string[] HttpMethods =
        { "GET ", "POST ", "PUT ", "DELETE ", "HEAD ", "OPTIONS ", "PATCH ", "CONNECT " };

    /// <summary>
    /// Read up to <see cref="MaxSniffSize"/> bytes from <paramref name="clientInput"/>
    /// and attempt to extract a domain name from a TLS ClientHello SNI or an
    /// HTTP Host header. The caller sets the socket read timeout; a timed-out
    /// read is reported like an empty one.
    /// </summary>
    public static SniffResult Sniff(Stream clientInput, int timeoutMs = 3000)
    {
        _ = timeoutMs; // enforced by the caller setting the socket timeout.
        var buffer = new byte[MaxSniffSize];
        var totalRead = 0;
        try
        {
            var bytesRead = clientInput.Read(buffer, 0, buffer.Length);
            if (bytesRead > 0) totalRead = bytesRead;
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"Sniff read error: {e.Message}");
        }

        if (totalRead == 0) return new SniffResult(null, buffer, 0);

        // Try TLS SNI first, then HTTP Host.
        var domain = ExtractTlsSni(buffer, totalRead) ?? ExtractHttpHost(buffer, totalRead);
        return new SniffResult(domain, buffer, totalRead);
    }

    /// <summary>
    /// Extract SNI hostname from a TLS ClientHello message. Record layout:
    /// ContentType 0x16, version, length, HandshakeType 0x01, ... extensions,
    /// with the SNI extension (type 0x0000) carrying the ASCII hostname.
    /// </summary>
    private static string? ExtractTlsSni(byte[] buf, int len)
    {
        if (len < 44) return null;
        if (buf[0] != 0x16) return null; // Not a Handshake record

        var recordLength = (buf[3] << 8) | buf[4];
        if (5 + recordLength > len) return null; // Incomplete record
        if (buf[5] != 0x01) return null; // Not ClientHello

        var pos = 43; // After fixed ClientHello fields.

        // Skip Session ID.
        if (pos >= len) return null;
        var sessionIdLen = buf[pos];
        pos += 1 + sessionIdLen;

        // Skip Cipher Suites.
        if (pos + 2 > len) return null;
        var cipherSuitesLen = (buf[pos] << 8) | buf[pos + 1];
        pos += 2 + cipherSuitesLen;

        // Skip Compression Methods.
        if (pos >= len) return null;
        var compMethodsLen = buf[pos];
        pos += 1 + compMethodsLen;

        // Extensions.
        if (pos + 2 > len) return null;
        var extensionsLen = (buf[pos] << 8) | buf[pos + 1];
        pos += 2;

        var extensionsEnd = pos + extensionsLen;
        if (extensionsEnd > len) return null;

        while (pos + 4 <= extensionsEnd)
        {
            var extType = (buf[pos] << 8) | buf[pos + 1];
            var extLen = (buf[pos + 2] << 8) | buf[pos + 3];
            pos += 4;

            if (extType == 0x0000 && extLen > 0)
            {
                // ServerNameList.
                if (pos + 2 > len) return null;
                var sniPos = pos + 2;

                if (sniPos + 3 > len) return null;
                var nameType = buf[sniPos];
                var nameLen = (buf[sniPos + 1] << 8) | buf[sniPos + 2];
                sniPos += 3;

                if (nameType == 0x00 && nameLen > 0 && sniPos + nameLen <= len)
                {
                    return Encoding.ASCII.GetString(buf, sniPos, nameLen).ToLowerInvariant();
                }
            }

            pos += extLen;
        }

        return null;
    }

    /// <summary>
    /// Extract Host header from an HTTP request. Checks for common HTTP method
    /// prefixes, then scans for the "Host:" header (case-insensitive).
    /// </summary>
    private static string? ExtractHttpHost(byte[] buf, int len)
    {
        if (len < 16) return null;

        var start = Encoding.ASCII.GetString(buf, 0, Math.Min(len, 10));
        if (!HttpMethods.Any(m => start.StartsWith(m, StringComparison.Ordinal))) return null;

        var text = Encoding.ASCII.GetString(buf, 0, len);
        var hostIdx = text.IndexOf("\r\nHost:", StringComparison.OrdinalIgnoreCase);
        if (hostIdx < 0) return null;

        var valueStart = hostIdx + 7; // length of "\r\nHost:"
        var lineEnd = text.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
        if (lineEnd < 0) return null;

        var host = text[valueStart..lineEnd].Trim();

        // Strip port if present (e.g., "example.com:8080" -> "example.com"),
        // unless it looks like an IPv6 literal.
        var colonIdx = host.LastIndexOf(':');
        if (colonIdx > 0)
        {
            var afterColon = host[(colonIdx + 1)..];
            if (afterColon.Length > 0 && afterColon.All(char.IsDigit))
            {
                host = host[..colonIdx];
            }
        }

        var lowered = host.ToLowerInvariant();
        return lowered.Length == 0 ? null : lowered;
    }
}

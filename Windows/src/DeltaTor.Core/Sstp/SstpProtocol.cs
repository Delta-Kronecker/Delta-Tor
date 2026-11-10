namespace DeltaTor.Core.Sstp;

/// <summary>Where a control message (or a failure) came from, mirroring
/// <c>kittoku.osc.Where</c>. Used for logging parity: the exact names appear in
/// the session log lines.</summary>
internal enum Where
{
    CERT,
    CERT_PATH,
    SSL,
    PROXY,
    SSTP_DATA,
    SSTP_CONTROL,
    SSTP_REQUEST,
    SSTP_HASH,
    PPP,
    PAP,
    CHAP,
    MSCHAPV2,
    EAP,
    LCP,
    LCP_MRU,
    LCP_AUTH,
    IPCP,
    IPCP_IP,
    IPV6CP,
    IPV6CP_IDENTIFIER,
    IP,
    IPv4,
    IPv6,
    ROUTE,
    INCOMING,
    OUTGOING
}

/// <summary>Mirror of <c>kittoku.osc.Result</c>.</summary>
internal enum Result
{
    PROCEEDED,

    // common errors
    ERR_TIMEOUT,
    ERR_COUNT_EXHAUSTED,
    ERR_UNKNOWN_TYPE, // the data cannot be parsed
    ERR_UNEXPECTED_MESSAGE, // the data can be parsed, but it's received in the wrong time
    ERR_PARSING_FAILED,
    ERR_VERIFICATION_FAILED,

    // for SSTP
    ERR_NEGATIVE_ACKNOWLEDGED,
    ERR_ABORT_REQUESTED,
    ERR_DISCONNECT_REQUESTED,

    // for PPP
    ERR_TERMINATE_REQUESTED,
    ERR_PROTOCOL_REJECTED,
    ERR_CODE_REJECTED,
    ERR_AUTHENTICATION_FAILED,
    ERR_ADDRESS_REJECTED,
    ERR_OPTION_REJECTED,

    // for IP
    ERR_INVALID_ADDRESS,

    // for INCOMING
    ERR_INVALID_PACKET_SIZE
}

/// <summary>Mirror of <c>kittoku.osc.ControlMessage</c>.</summary>
internal sealed record ControlMessage(Where From, Result Result, string? Supplement = null);

/// <summary>The three authentication protocols the core knows, valued exactly
/// as the Android preference constants (they are compared as strings during
/// LCP negotiation).</summary>
internal static class AuthProtocol
{
    public const string Pap = "PAP";
    public const string MsChapV2 = "MSCHAPv2";
    public const string EapMsChapV2 = "EAP-MSCHAPv2";
}

/// <summary>configuration.kt — MRU/MTU bounds and defaults.</summary>
internal static class Configuration
{
    public const int MaxMru = 2000;
    public const int MaxMtu = 2000;
    public const int MinMru = 68;
    public const int MinMtu = 68;
    public const int DefaultMru = 1500;
    public const int DefaultMtu = 1500;

    /// <summary>ConfigClient: PPP_REQUEST_INTERVAL * PPP_REQUEST_COUNT.</summary>
    public const int PppNegotiationTimeout = 3_000 * 10;
}

/// <summary>kittoku/osc/debug/exception.kt.</summary>
internal sealed class ParsingDataUnitException : Exception
{
    public ParsingDataUnitException() : base("Failed to parse data unit") { }
}

/// <summary>
/// <c>assertAlways</c>: an assertion failure is an Error in Kotlin, not an
/// Exception, so it is deliberately NOT caught by the parser's recovery path —
/// it propagates to the session's unexpected-error handler instead. The
/// dedicated type keeps that distinction visible in C#, where every throwable
/// derives from <see cref="Exception"/>.
/// </summary>
internal sealed class SstpAssertException : Exception
{
    public SstpAssertException() : base("SSTP assertion failed") { }
}

internal static class SstpDebug
{
    public static void AssertAlways(bool value)
    {
        if (!value)
        {
            throw new SstpAssertException();
        }
    }
}

/// <summary>kittoku/osc/unit/DataUnit.kt — the base of every packet the core
/// serialises and parses.</summary>
internal abstract class DataUnit
{
    public abstract int Length { get; }

    public abstract void Write(ByteBuffer buffer);

    public abstract void Read(ByteBuffer buffer);

    public ByteBuffer ToByteBuffer()
    {
        var buffer = ByteBuffer.Allocate(Length);
        Write(buffer);
        buffer.Flip();
        return buffer;
    }
}

/// <summary>kittoku/osc/extension/ByteArray.kt and String.kt helpers.</summary>
internal static class UnitExtensions
{
    public static string ToHexString(this byte[] bytes, bool parse = false)
    {
        var sb = new System.Text.StringBuilder();
        for (var index = 0; index < bytes.Length; index++)
        {
            sb.Append(bytes[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));

            if (parse)
            {
                sb.Append(index % 16 == 15 ? "\n" : " ");
            }
        }

        return sb.ToString();
    }

    public static byte[] ToHexByteArray(this string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new Exception("Fragmented Byte");
        }

        var arrayLength = hex.Length / 2;
        var output = new byte[arrayLength];

        for (var i = 0; i < arrayLength; i++)
        {
            var start = i * 2;
            output[i] = Convert.ToByte(hex.Substring(start, 2), 16);
        }

        return output;
    }

    /// <summary>The Kotlin <c>sum(vararg words: String)</c> concatenator.</summary>
    public static string Sum(params string[] words)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            sb.Append(word);
        }

        return sb.ToString();
    }
}

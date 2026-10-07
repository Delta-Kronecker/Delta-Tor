using System.Net.Sockets;
using System.Text.RegularExpressions;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>Country codes for geo-bypass routing.</summary>
public enum GeoBypassCountry
{
    Ir,
    Cn,
    Ru
}

public static class GeoBypassCountryInfo
{
    public static string Code(this GeoBypassCountry c) => c switch
    {
        GeoBypassCountry.Cn => "cn",
        GeoBypassCountry.Ru => "ru",
        _ => "ir"
    };

    public static string DisplayName(this GeoBypassCountry c) => c switch
    {
        GeoBypassCountry.Cn => "China",
        GeoBypassCountry.Ru => "Russia",
        _ => "Iran"
    };

    public static GeoBypassCountry FromCode(string code) =>
        code.ToLowerInvariant() switch
        {
            "cn" => GeoBypassCountry.Cn,
            "ru" => GeoBypassCountry.Ru,
            _ => GeoBypassCountry.Ir
        };
}

/// <summary>Loaded geo-bypass data: sorted CIDR ranges + domestic domain set.</summary>
public sealed class GeoBypassData
{
    public GeoBypassData(long[] ipRangeStarts, long[] ipRangeEnds, IReadOnlySet<string> domains)
    {
        IpRangeStarts = ipRangeStarts;
        IpRangeEnds = ipRangeEnds;
        Domains = domains;
    }

    public long[] IpRangeStarts { get; }
    public long[] IpRangeEnds { get; }
    public IReadOnlySet<string> Domains { get; }
}

/// <summary>
/// Decides whether a connection should bypass the tunnel based on domain name,
/// geo-bypass CIDR ranges, and geo-bypass domestic domain lists. Used together
/// with <see cref="ProtocolSniffer"/> to implement domain-based routing.
///
/// DORMANT on Windows (plan decision 1, split tunnelling CUT): the only
/// instance in use is <see cref="Disabled"/>, kept so TorSocksBridge compiles
/// and behaves exactly as if routing were off. The geo/ asset directory ships
/// nothing, and <see cref="LoadGeoData"/> fails soft, as on Android.
/// </summary>
public sealed class DomainRouter
{
    private const string TAG = "DomainRouter";
    private const int DirectConnectTimeoutMs = 10_000;

    public DomainRouter(
        bool enabled,
        DomainRoutingMode mode,
        IReadOnlySet<string> domains,
        bool geoBypassEnabled = false,
        GeoBypassData? geoBypass = null)
    {
        Enabled = enabled;
        Mode = mode;
        Domains = domains;
        GeoBypassEnabled = geoBypassEnabled;
        GeoBypass = geoBypass ?? EmptyData();
    }

    public bool Enabled { get; }
    public DomainRoutingMode Mode { get; }
    public IReadOnlySet<string> Domains { get; }
    public bool GeoBypassEnabled { get; }
    public GeoBypassData GeoBypass { get; }

    public static readonly DomainRouter Disabled = new(
        enabled: false,
        mode: DomainRoutingMode.Bypass,
        domains: new HashSet<string>(StringComparer.Ordinal));

    private static GeoBypassData EmptyData() =>
        new(Array.Empty<long>(), Array.Empty<long>(), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Check if <paramref name="host"/> is an IP address literal (IPv4 or IPv6).</summary>
    public static bool IsIpAddress(string host)
    {
        // IPv4: digits and dots only.
        if (Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$")) return true;
        // IPv6: contains colons.
        if (host.Contains(':')) return true;
        return false;
    }

    /// <summary>
    /// Load geo-bypass data (CIDR ranges + domain list) from the bundled geo/
    /// assets for the given country. The assets are not shipped today, so this
    /// logs and returns empty data — the same soft failure as on Android.
    /// </summary>
    public static GeoBypassData LoadGeoData(GeoBypassCountry country)
    {
        var starts = new List<long>();
        var ends = new List<long>();
        var domains = new HashSet<string>(StringComparer.Ordinal);

        // Load CIDR ranges.
        try
        {
            var path = AppAssets.Existing($"geo/{country.Code()}.cidr");
            if (path != null)
            {
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    var range = ParseCidr(trimmed);
                    if (range != null)
                    {
                        starts.Add(range.Value.Start);
                        ends.Add(range.Value.End);
                    }
                }
                AppLog.I(TAG, $"Loaded {starts.Count} CIDR ranges for {country.DisplayName()}");
            }
        }
        catch (Exception e)
        {
            AppLog.E(TAG, $"Failed to load CIDR ranges for {country.Code()}: {e.Message}");
        }

        // Load domain list.
        try
        {
            var path = AppAssets.Existing($"geo/{country.Code()}.domains");
            if (path != null)
            {
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim().ToLowerInvariant();
                    if (trimmed.Length > 0) domains.Add(trimmed);
                }
                AppLog.I(TAG, $"Loaded {domains.Count} domains for {country.DisplayName()}");
            }
        }
        catch (Exception e)
        {
            AppLog.E(TAG, $"Failed to load domains for {country.Code()}: {e.Message}");
        }

        // Sort ranges by start IP for binary search.
        var indices = Enumerable.Range(0, starts.Count).OrderBy(i => starts[i]).ToArray();
        var sortedStarts = indices.Select(i => starts[i]).ToArray();
        var sortedEnds = indices.Select(i => ends[i]).ToArray();
        return new GeoBypassData(sortedStarts, sortedEnds, domains);
    }

    /// <summary>
    /// Parse CIDR notation ("192.168.1.0/24") into a start/end IP range, or
    /// null when parsing fails.
    /// </summary>
    internal static (long Start, long End)? ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return null;
        var ip = IpUtil.Ipv4ToLong(parts[0]);
        if (ip == null) return null;
        if (!int.TryParse(parts[1], out var prefix)) return null;
        if (prefix < 0 || prefix > 32) return null;
        var mask = prefix == 0 ? 0L : (0xFFFFFFFFL << (32 - prefix)) & 0xFFFFFFFFL;
        var start = ip.Value & mask;
        var end = start | (~mask & 0xFFFFFFFFL);
        return (start, end);
    }

    /// <summary>
    /// Determine whether traffic to <paramref name="host"/> should bypass the
    /// tunnel. Checks in order: domain routing rules (BYPASS / ONLY_VPN mode),
    /// geo-bypass domestic domain suffix match, geo-bypass IP CIDR match.
    /// </summary>
    public bool ShouldBypass(string host)
    {
        // Check domain routing rules first.
        if (Enabled && Domains.Count > 0)
        {
            var matches = DomainMatchesList(host);
            var domainResult = Mode == DomainRoutingMode.Bypass ? matches : !matches;
            if (domainResult) return true;
        }

        // Check geo-bypass.
        if (GeoBypassEnabled)
        {
            if (!IsIpAddress(host) && GeoBypassDomainMatch(host))
            {
                AppLog.D(TAG, $"Geo-bypass domain match: {host}");
                return true;
            }
            if (IsIpAddress(host) && !host.Contains(':'))
            {
                var ipLong = IpUtil.Ipv4ToLong(host);
                if (ipLong != null && IpInRanges(ipLong.Value))
                {
                    AppLog.D(TAG, $"Geo-bypass IP match: {host}");
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Create a direct TCP connection bypassing the tunnel. On Windows this is
    /// a plain socket (no VPN "protect" semantics needed); dormant with the
    /// router disabled.
    /// </summary>
    public static Socket CreateDirectConnection(string host, int port, int timeoutMs = DirectConnectTimeoutMs)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        if (!socket.ConnectAsync(host, port).Wait(timeoutMs))
        {
            socket.Dispose();
            throw new TimeoutException($"direct connect to {host}:{port} timed out");
        }
        socket.NoDelay = true;
        AppLog.D(TAG, $"Direct connection to {host}:{port} established");
        return socket;
    }

    private bool DomainMatchesList(string input)
    {
        var normalizedInput = input.ToLowerInvariant().TrimEnd('.');
        return Domains.Any(rule =>
        {
            var normalizedRule = rule.ToLowerInvariant().TrimEnd('.');
            return normalizedInput == normalizedRule ||
                   normalizedInput.EndsWith("." + normalizedRule, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Suffix-match against the geo-bypass domain list. Handles both TLD
    /// entries (".ir") and full domains ("digikala.com").
    /// </summary>
    private bool GeoBypassDomainMatch(string host)
    {
        var normalized = host.ToLowerInvariant().TrimEnd('.');
        return GeoBypass.Domains.Any(rule =>
        {
            var normalizedRule = rule.TrimEnd('.');
            if (normalizedRule.StartsWith(".", StringComparison.Ordinal))
            {
                // TLD rule: ".ir" matches "example.ir" and "sub.example.ir".
                return normalized.EndsWith(normalizedRule, StringComparison.Ordinal) ||
                       normalized == normalizedRule[1..];
            }
            return normalized == normalizedRule ||
                   normalized.EndsWith("." + normalizedRule, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Binary search to check if an IP address falls within any of the CIDR
    /// ranges. Ranges must be sorted by start IP.
    /// </summary>
    private bool IpInRanges(long ip)
    {
        var starts = GeoBypass.IpRangeStarts;
        var ends = GeoBypass.IpRangeEnds;
        if (starts.Length == 0) return false;

        // Binary search: find the last range whose start <= ip.
        var low = 0;
        var high = starts.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (starts[mid] <= ip)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result >= 0 && ip <= ends[result];
    }
}

using System.Text.RegularExpressions;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>Where the traffic leaves the Tor network.</summary>
/// <param name="Ip">Exit IP as seen by the echo services.</param>
/// <param name="CountryCode">ISO country code, when known.</param>
/// <param name="CountryName">Country name, when known.</param>
/// <param name="City">City, from the network lookup only.</param>
/// <param name="Asn">Autonomous system number/name, network lookup only.</param>
/// <param name="FromNetwork">
/// True when the country came from ip2location rather than the offline GeoIP db.
/// </param>
public sealed record ExitInfo(
    string Ip,
    string CountryCode = "",
    string CountryName = "",
    string City = "",
    string Asn = "",
    bool FromNetwork = false)
{
    /// <summary>"BE · Belgium", or just the name when the code is unknown.</summary>
    public string Label() =>
        (CountryCode.Length > 0, CountryName.Length > 0) switch
        {
            (true, true) => $"{CountryCode} · {CountryName}",
            (false, true) => CountryName,
            (true, false) => CountryCode,
            _ => "unknown"
        };
}

/// <summary>
/// Resolves the public exit IP and country as seen through the app's SOCKS5
/// tunnel, i.e. through the Tor circuit rather than the machine's own network.
///
/// Primary source is ip2location.io; when that is rate limited or unreachable
/// it falls back to a plain-HTTP IP echo plus the bundled offline GeoIP
/// database.
/// </summary>
public static class ExitLocator
{
    private const string TAG = "ExitLocator";

    private const string GeoUrl = "https://api.ip2location.io/";

    private static readonly string[] EchoUrls =
    {
        "https://api.ipify.org/",
        "http://icanhazip.com/",
        "http://ifconfig.me/"
    };

    private static readonly Regex Ipv4 = new(@"^(?:\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);

    /// <summary>
    /// Full lookup: country straight from the network, or from the offline db.
    /// Blocking — call it from a background thread.
    /// </summary>
    public static ExitInfo? Locate(string socksHost, int socksPort, int timeoutMs = 15_000)
    {
        var geo = GeoLookup(socksHost, socksPort, timeoutMs);
        if (geo != null) return geo;

        foreach (var url in EchoUrls)
        {
            string? ip;
            try
            {
                var body = SocksHttp.Get(socksHost, socksPort, url, timeoutMs);
                ip = ParallelTorManager.SplitLines(body)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => Ipv4.IsMatch(l));
            }
            catch (Exception e)
            {
                AppLog.W(TAG, $"Echo probe {url} failed: {e.Message}");
                continue;
            }
            if (ip == null) continue;
            var offline = OfflineCountry(ip);
            return new ExitInfo(
                Ip: ip,
                CountryCode: offline?.Code ?? "",
                CountryName: offline?.Name ?? "",
                FromNetwork: false);
        }
        return null;
    }

    private static (string Code, string Name)? OfflineCountry(string ip)
    {
        try
        {
            return BridgeCountries.CountryInfo(ip);
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"offline country lookup failed: {e.Message}");
            return null;
        }
    }

    // --- ip2location.io -------------------------------------------------------

    private static ExitInfo? GeoLookup(string socksHost, int socksPort, int timeoutMs)
    {
        try
        {
            var body = SocksHttp.Get(socksHost, socksPort, GeoUrl, timeoutMs);
            var ip = Json(body, "ip");
            if (string.IsNullOrWhiteSpace(ip))
            {
                AppLog.W(TAG, $"ip2location returned no ip: {Truncate(body, 200)}");
                return null;
            }
            return new ExitInfo(
                Ip: ip,
                CountryCode: Json(body, "country_code") ?? "",
                CountryName: Json(body, "country_name") ?? "",
                City: Json(body, "city_name") ?? "",
                Asn: Json(body, "as") ?? "",
                FromNetwork: true);
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"ip2location lookup failed: {e.Message}");
            return null;
        }
    }

    private static string? Json(string body, string key)
    {
        var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

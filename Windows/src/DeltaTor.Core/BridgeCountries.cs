using System.IO.Compression;
using System.Text;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>One entry of the exit-country picker: an ISO country code and its name.</summary>
public sealed record ExitCountry(string Code, string Name);

/// <summary>
/// Bundled db-ip country-lite (CC BY 4.0) dataset: sorted IP ranges → country
/// code. Loaded lazily once per process from the geoip asset.
/// </summary>
internal sealed class CountryDb
{
    private readonly long[] _starts;
    private readonly long[] _ends;
    private readonly int[] _codeIdx;
    private readonly string[] _codeList;

    private CountryDb(long[] starts, long[] ends, int[] codeIdx, string[] codeList)
    {
        _starts = starts;
        _ends = ends;
        _codeIdx = codeIdx;
        _codeList = codeList;
    }

    public string? Country(long ip)
    {
        var lo = 0;
        var hi = _starts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (_starts[mid] <= ip) lo = mid + 1; else hi = mid - 1;
        }
        if (hi < 0) return null;
        return ip <= _ends[hi] ? _codeList[_codeIdx[hi]] : null;
    }

    public static CountryDb Load()
    {
        var startsRaw = new List<long>();
        var endsRaw = new List<long>();
        var idxRaw = new List<int>();
        var codes = new List<string>();
        var codeMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in CountryLines())
        {
            var p = line.Split(',');
            if (p.Length < 3) continue;
            // db-ip country-lite stores dotted quads, e.g. "1.0.0.0"
            var a = IpUtil.Ipv4ToLong(p[0].Trim());
            var b = IpUtil.Ipv4ToLong(p[1].Trim());
            if (a == null || b == null) continue;
            var cc = p[2].Trim().ToUpperInvariant();
            if (cc.Length != 2 || !cc.All(ch => ch is >= 'A' and <= 'Z')) continue;
            startsRaw.Add(a.Value);
            endsRaw.Add(b.Value);
            if (!codeMap.TryGetValue(cc, out var ci))
            {
                ci = codes.Count;
                codes.Add(cc);
                codeMap[cc] = ci;
            }
            idxRaw.Add(ci);
        }
        var n = startsRaw.Count;
        var order = Enumerable.Range(0, n).OrderBy(i => startsRaw[i]).ToArray();
        var s = new long[n];
        var e = new long[n];
        var ix = new int[n];
        for (var k = 0; k < n; k++)
        {
            s[k] = startsRaw[order[k]];
            e[k] = endsRaw[order[k]];
            ix[k] = idxRaw[order[k]];
        }
        AppLog.D("BridgeCountries", $"Loaded {n} IP blocks for geo lookup");
        return new CountryDb(s, e, ix, codes.ToArray());
    }

    /// <summary>Every line of the bundled country range table, gzip or plain.</summary>
    private static IEnumerable<string> CountryLines()
    {
        var gz = AppAssets.Existing("geoip/country.csv.gz");
        var plain = AppAssets.Existing("geoip/country.csv");
        if (gz != null)
        {
            using var fs = File.OpenRead(gz);
            using var gzStream = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gzStream, Encoding.ASCII);
            string? line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
        else if (plain != null)
        {
            foreach (var line in File.ReadLines(plain)) yield return line;
        }
    }
}

/// <summary>
/// Country list for the EXIT NODE picker, plus an offline IP → country lookup
/// used as a fallback when the online exit lookup is unavailable.
///
/// The picker is a plain list of every country shipped in the GeoIP dataset,
/// sorted alphabetically by name so it is easy to scan. It intentionally does
/// not depend on the bridge cache: the list must always be there, even on a
/// first run with no bridges downloaded yet.
/// </summary>
public static class BridgeCountries
{
    private const string TAG = "BridgeCountries";

    private static volatile CountryDb? _db;
    private static volatile IReadOnlyDictionary<string, string>? _cachedNames;

    private static IReadOnlyDictionary<string, string> CountryNames()
    {
        var cached = _cachedNames;
        if (cached != null) return cached;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = AppAssets.Existing("geoip/countries.tsv");
            if (path != null)
            {
                foreach (var line in File.ReadLines(path))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 2 && p[0].Length == 2)
                    {
                        map[p[0].Trim().ToUpperInvariant()] = p[1].Trim();
                    }
                }
            }
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"Country names unavailable: {e.Message}");
        }
        _cachedNames = map;
        return map;
    }

    /// <summary>
    /// Country (code, name) for an IP that exited through the tunnel, or null
    /// when it is not a resolvable IPv4. Synchronous — the caller is already on
    /// a background thread.
    /// </summary>
    public static (string Code, string Name)? CountryInfo(string ip)
    {
        var geo = _db ?? CountryDb.Load();
        _db = geo;
        var host = ip.Trim().Split(':')[0];
        var l = IpUtil.Ipv4ToLong(host);
        if (l == null) return null;
        var cc = geo.Country(l.Value);
        if (cc == null) return null;
        var names = CountryNames();
        return (cc, names.TryGetValue(cc, out var name) ? name : cc);
    }

    /// <summary>
    /// Every selectable exit country, alphabetical by name. Cheap: the name
    /// table is a few kB asset, no GeoIP parse and no bridge cache needed.
    /// </summary>
    public static IReadOnlyList<ExitCountry> TopSync()
    {
        try
        {
            return CountryNames()
                .Where(kv => kv.Key.Length == 2)
                .Select(kv => new ExitCountry(kv.Key, kv.Value))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"Country list failed: {e.Message}");
            return Array.Empty<ExitCountry>();
        }
    }
}

/// <summary>"1.2.3.4" → packed 32-bit value, or null when not a dotted quad.</summary>
internal static class IpUtil
{
    public static long? Ipv4ToLong(string host)
    {
        var p = host.Split('.');
        if (p.Length != 4) return null;
        long v = 0;
        foreach (var octet in p)
        {
            if (!int.TryParse(octet, out var n) || n < 0 || n > 255) return null;
            v = (v << 8) | (uint)n;
        }
        return v;
    }
}

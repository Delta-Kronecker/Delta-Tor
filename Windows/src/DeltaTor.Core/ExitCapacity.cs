using System.Globalization;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// How much exit capacity a country has.
/// </summary>
/// <param name="Exits">How many relays in that country carried the Exit flag.</param>
/// <param name="Weight">That country's share of all Tor exit bandwidth, 0..1.</param>
public sealed record ExitCapacity(int Exits, double Weight);

/// <summary>
/// Per-country exit capacity, read from the bundled table.
///
/// The country picker used to be every country in the bundled GeoIP file: 248
/// of them, of which 187 have no Tor exit at all and never did. Picking one of
/// those was not a slow choice, it was a choice that could not work, and
/// nothing on screen said so.
///
/// The table answers the question instead, from Tor's own relay database:
/// which countries have exits running right now, how many, and what share of
/// all exit bandwidth they hold. Share is the figure that predicts speed,
/// because a country can have a hundred slow exits or a few quick ones. The
/// spread is steep enough to be worth having: the top fifteen countries hold
/// about 97% of it.
///
/// It ships as an asset rather than a fetch on purpose. The answer moves on
/// the scale of months, an app update carries a new table as easily as a new
/// one, and a country list is not worth a network round trip and a cache
/// expiry on every launch. Regenerate assets/geoip/exit-capacity.tsv when the
/// numbers drift; the recipe is in its own header.
/// </summary>
public static class ExitCapacityIndex
{
    private const string TAG = "ExitCapacity";

    private static volatile IReadOnlyDictionary<string, ExitCapacity> _byCountry =
        new Dictionary<string, ExitCapacity>(StringComparer.Ordinal);

    private static int _started;

    /// <summary>Country code to capacity. Empty until the bundled table is read.</summary>
    public static IReadOnlyDictionary<string, ExitCapacity> ByCountry => _byCountry;

    /// <summary>Reads the table once, off the caller's thread. Never blocks the caller.</summary>
    public static void Load()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        var thread = new Thread(() =>
        {
            var table = Read();
            if (table.Count > 0) _byCountry = table;
            AppLog.I(TAG, $"exit capacity: {table.Count} countries with running exits");
        });
        thread.IsBackground = true;
        thread.Name = "deltator-exit-capacity";
        thread.Start();
    }

    private static IReadOnlyDictionary<string, ExitCapacity> Read()
    {
        var outMap = new Dictionary<string, ExitCapacity>(StringComparer.Ordinal);
        try
        {
            var path = AppAssets.Existing("geoip/exit-capacity.tsv");
            if (path == null)
            {
                AppLog.W(TAG, "exit capacity table unavailable: not shipped next to the exe");
                return outMap;
            }
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                var cc = p[0].Trim().ToUpperInvariant();
                if (!int.TryParse(p[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
                if (!double.TryParse(p[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) continue;
                if (cc.Length != 2 || n <= 0) continue;
                outMap[cc] = new ExitCapacity(n, w);
            }
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"exit capacity table unavailable: {e.Message}");
        }
        return outMap;
    }
}

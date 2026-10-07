namespace DeltaTor.Core;

/// <summary>
/// User-selected exit countries (optional, any number of them). Persisted and
/// applied over the Tor control port after bootstrap as a single
/// `ExitNodes {us},{nl},...` line plus `StrictNodes 0`, so the list steers the
/// exit country but can never make a circuit impossible to build. Selecting
/// nothing means "any country".
///
/// (Windows counterpart of Android ExitNodes; the LOCATION picker directory —
/// BridgeCountries/ExitCapacity — arrives with the drawer UI stage.)
/// </summary>
public static class ExitNodes
{
    private const string KeyCodes = "exit_ccs";
    private const string KeyNames = "exit_names";

    private static readonly object Lock = new();
    private static List<string> _codes = new();
    private static Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private static bool _inited;

    /// <summary>Selected country codes, in selection order.</summary>
    public static IReadOnlyList<string> Codes
    {
        get { lock (Lock) return _codes.ToArray(); }
    }

    /// <summary>Selected country display names, keyed by code.</summary>
    public static IReadOnlyDictionary<string, string> Names
    {
        get { lock (Lock) return new Dictionary<string, string>(_names, StringComparer.Ordinal); }
    }

    /// <summary>Load the persisted selection (idempotent; call once at startup).</summary>
    public static void Init()
    {
        lock (Lock)
        {
            if (_inited) return;
            _inited = true;
            var codes = (Config.GetRaw(KeyCodes) ?? "")
                .Split(',')
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var names = (Config.GetRaw(KeyNames) ?? "")
                .Split(',')
                .Select(n => n.Trim())
                .Where(n => n.Length > 0)
                .ToArray();
            _codes = codes;
            _names = codes.Select((cc, i) => (cc, i))
                .Where(t => t.i < names.Length)
                .ToDictionary(t => t.cc, t => names[t.i], StringComparer.Ordinal);
        }
    }

    public static bool IsSelected(string code) =>
        Codes.Contains(code.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    /// <summary>Add or remove one country; the order of selection is kept.</summary>
    public static void Toggle(string code, string name)
    {
        var cc = code.Trim().ToUpperInvariant();
        if (cc.Length != 2 || !cc.All(ch => ch is >= 'A' and <= 'Z')) return;
        lock (Lock)
        {
            if (_codes.Remove(cc)) _names.Remove(cc);
            else
            {
                _codes.Add(cc);
                _names[cc] = name;
            }
            PersistLocked();
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            _codes = new List<string>();
            _names = new Dictionary<string, string>(StringComparer.Ordinal);
            Config.RemoveRaw(KeyCodes);
            Config.RemoveRaw(KeyNames);
        }
    }

    private static void PersistLocked()
    {
        Config.SetRaw(KeyCodes, string.Join(",", _codes));
        Config.SetRaw(KeyNames, string.Join(",", _codes.Select(c => _names.TryGetValue(c, out var n) ? n : "")));
    }

    /// <summary>Synchronous read for torrc generation / control-port steering.</summary>
    public static IReadOnlyList<string> CurrentCodes() => Codes;
}

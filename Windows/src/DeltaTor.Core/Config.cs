using System.Text.Json;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Persistent user configuration, backed by a JSON key/value file
/// (%APPDATA%\DeltaTor\prefs.json) — the Windows counterpart of Android
/// SharedPreferences ("deltator"). Keys and defaults are identical; values are
/// written immediately on every setter, same as SharedPreferences.apply().
///
/// The Android split_tunnel_* keys are intentionally absent: per-app routing
/// (WFP) was cut from the Windows v1 (see PLAN.md, decision 1).
/// </summary>
public static class Config
{
    public const int DefaultProxyPort = 9050;

    /// <summary>
    /// Which transport a fresh install connects with: Combined-Bridge is every
    /// cached list in one runner (vanilla + obfs4 + webtunnel at the same time).
    /// Not auto (snowflake usually contributes nothing at start), not a single
    /// list (betting the first connect on one transport).
    /// </summary>
    public const string DefaultTransportMode = "combined";

    /// <summary>Which transports take part in auto mode. The memory runner is not listed.</summary>
    public static readonly IReadOnlyList<string> AutoTransportChoices =
        new[] { "vanilla", "obfs4", "webtunnel", "snowflake" };

    /// <summary>
    /// Snowflake is deliberately off by default: it is the slowest to bootstrap
    /// and not always reachable, so racing it by default pays for a fourth runner
    /// that mostly loses. An install whose stored set includes it keeps it.
    /// </summary>
    public static readonly IReadOnlySet<string> AutoTransportDefaults =
        new HashSet<string> { "vanilla", "obfs4", "webtunnel" };

    private static readonly object Lock = new();
    private static Dictionary<string, string> _prefs = new(StringComparer.Ordinal);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaTor", "prefs.json");

    /// <summary>Load the preference file (creates it on first run) and sync AppLog.</summary>
    public static void Init()
    {
        lock (Lock)
        {
            _prefs = LoadFile();
        }
        AppLog.Enabled = LoggingEnabled;
    }

    private static Dictionary<string, string> LoadFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null) return data;
            }
        }
        catch
        {
            // A corrupt prefs file must not brick the app; start from defaults.
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_prefs, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Losing a preference write is not worth crashing over.
        }
    }

    private static string GetString(string key, string def)
    {
        lock (Lock) return _prefs.TryGetValue(key, out var v) ? v : def;
    }

    private static void SetString(string key, string value)
    {
        lock (Lock)
        {
            _prefs[key] = value;
            SaveLocked();
        }
    }

    private static bool GetBool(string key, bool def) =>
        GetString(key, def ? "true" : "false") == "true";

    private static void SetBool(string key, bool value) => SetString(key, value ? "true" : "false");

    private static int GetInt(string key, int def) =>
        int.TryParse(GetString(key, def.ToString()), out var v) ? v : def;

    private static void SetInt(string key, int value) => SetString(key, value.ToString());

    // --- raw access for other pref-backed modules (TorrcSettings, ...) --------

    /// <summary>Read a raw string pref (null when unset), same file as everything else.</summary>
    internal static string? GetRaw(string key)
    {
        lock (Lock) return _prefs.TryGetValue(key, out var v) ? v : null;
    }

    internal static void SetRaw(string key, string value)
    {
        lock (Lock)
        {
            _prefs[key] = value;
            SaveLocked();
        }
    }

    internal static void RemoveRaw(string key)
    {
        lock (Lock)
        {
            _prefs.Remove(key);
            SaveLocked();
        }
    }

    public static int ProxyPort
    {
        get => GetInt("proxy_port", DefaultProxyPort);
        set => SetInt("proxy_port", Math.Clamp(value, 1024, 65535));
    }

    public static bool DebugMode
    {
        get => GetBool("debug_mode", false);
        set => SetBool("debug_mode", value);
    }

    /// <summary>
    /// Whether the connection log is recorded. Off by default: recording every
    /// Tor line of every connect attempt across three Tor cores is the most
    /// expensive thing the user never asked for. Bootstrap percentage and
    /// failures still show without it — Tor's output is read either way.
    /// </summary>
    public static bool LoggingEnabled
    {
        get => GetBool("logging_enabled", false);
        set
        {
            SetBool("logging_enabled", value);
            AppLog.Enabled = value;
        }
    }

    public static string TransportMode
    {
        // "auto","vanilla","obfs4","webtunnel","snowflake","direct","custom"
        get => GetString("transport_mode", DefaultTransportMode);
        set => SetString("transport_mode", value);
    }

    /// <summary>
    /// Whether the one-time "your first connect is the slow one" explainer has
    /// been shown. Persisted so it cannot come back on the next launch.
    /// </summary>
    public static bool FirstRunNoticeShown
    {
        get => GetBool("first_run_notice_v1", false);
        set => SetBool("first_run_notice_v1", value);
    }

    /// <summary>
    /// Run Tor as a plain local SOCKS5 proxy and never bring up the tunnel.
    /// The engine side is identical either way: Tor bootstraps through the same
    /// transports and listens on <see cref="ProxyPort"/>; nothing is captured.
    /// </summary>
    public static bool ProxyOnlyMode
    {
        get => GetBool("proxy_only_mode", false);
        set => SetBool("proxy_only_mode", value);
    }

    /// <summary>
    /// Whether a connect also races the bridges this app has already proven.
    /// A toggle about the runners, not the learning: bridges that bootstrapped
    /// are recorded either way; off just stops the memory runner from joining.
    /// On by default, because that is what made later connects faster.
    /// </summary>
    public static bool RunMemory
    {
        get => GetBool("run_memory", true);
        set => SetBool("run_memory", value);
    }

    /// <summary>
    /// Whether the app may change the plan by itself when a connect stops
    /// moving: a connect with no progress for a minute is assumed blocked, the
    /// runners stop, the mode switches to auto and the connect starts again.
    /// Off by default (rewriting a mode the user chose is a big thing to do).
    /// </summary>
    public static bool AutoRecovery
    {
        get => GetBool("auto_recovery", false);
        set => SetBool("auto_recovery", value);
    }

    public static string CustomBridges
    {
        get => GetString("custom_bridges", "");
        set => SetString("custom_bridges", value);
    }

    /// <summary>
    /// Which transports take part in auto mode, comma separated on disk. An
    /// older install that never wrote the key falls back to the full default;
    /// an empty or fully invalid value must not leave auto with nothing to race.
    /// </summary>
    public static IReadOnlySet<string> AutoTransports
    {
        get
        {
            var raw = GetString("auto_transports", "");
            if (raw.Length == 0) return AutoTransportDefaults;
            var stored = raw.Split(',')
                .Select(s => s.Trim())
                .Where(s => AutoTransportChoices.Contains(s))
                .ToHashSet(StringComparer.Ordinal);
            return stored.Count == 0 ? AutoTransportDefaults : stored;
        }
        set
        {
            var clean = value.Where(AutoTransportChoices.Contains).ToHashSet(StringComparer.Ordinal);
            SetString("auto_transports", string.Join(",", clean));
        }
    }
}

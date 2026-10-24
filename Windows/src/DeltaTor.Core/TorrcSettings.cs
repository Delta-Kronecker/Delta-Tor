using System.Text.RegularExpressions;

namespace DeltaTor.Core;

/// <summary>
/// User-editable torrc as a single, ready-made template (Windows counterpart
/// of Android TorrcSettings; SharedPreferences key "deltator" /
/// "torrc_template_v6" lives in the same prefs file as <see cref="Config"/>).
///
/// Only this one block is exposed for editing in Settings. On every connect it
/// is written to the real torrc verbatim, and the pluggable transports
/// (ClientTransportPlugin) plus the selected bridge lines (Bridge ...) are
/// appended automatically so bridges are always part of the generated file.
/// Later lines win in torrc, so the template overrides the forced defaults in
/// TorRunner when it declares them.
///
/// The prefs key carries a version suffix: bumping it hands every existing
/// install the new default template instead of leaving it on the values it was
/// shipped with.
///
/// The shipped template carries no comments, because it is edited as plain
/// text (the EDIT TORRC card opens it in Notepad). The reasoning lives here
/// instead:
///
///  - No fixed `SocksPort` / `HTTPTunnelPort` / `DNSPort` / `ControlPort`,
///    ever. Three Tor processes run in parallel and the app owns the only
///    SOCKS5 listener; a hard-coded port here would be claimed by whichever
///    started first and make the other two fail. `SocksPolicy` only. The
///    same goes for `DataDirectory` (per-runner, absolute, engine-assigned),
///    `GeoIPFile` / `GeoIPv6File` (absolute paths to the bundle's tables,
///    written by the engine) and `ClientTransportPlugin` (the engine wires
///    the bundled lyrebird through its socks5 CMETHOD lines; the fork
///    sample's `Transport/*.exe` exec lines point at a layout this app
///    does not ship).
///  - The bundled core is the Delta-Kronecker/tor fork, so the full Conflux
///    family is live here: `ConfluxEnabled 1` with `ConfluxClientUX
///    throughput`, 32 prebuilt and linked sets of 2 legs, and set selection
///    over the best-RTT 10%. On stock Tor the ConfluxNum* lines would abort
///    startup; with the fork they are the point.
///  - `SocksTimeout 120` and `CircuitBuildTimeout 40` pair with the probe
///    budget in BridgeRace (both are read back from this template), so a
///    slow first circuit is waited out instead of read as a dead link.
///  - `CircuitPadding`, `ConnectionPadding` and `UseMicrodescriptors` trade
///    cover traffic for throughput. That is a deliberate choice, not a default.
///  - `KeepalivePeriod 3600` overrides the engine's keepalive-30 default on
///    purpose: an hour between keepalives is enough for a live guard and
///    stops the hourly churn the short default causes.
///
///  On ordering: the file is assembled as forced defaults, then this template,
///  then `ClientTransportPlugin`, then `Bridge`, then `ControlPort` (see
///  TorRunner.WriteTorrc). Later wins, so the template overrides the forced
///  defaults, and the Snowflake runner's own `ConnectionPadding 1` /
///  `SafeLogging 0` / `NumEntryGuards 1` are in turn overridden by anything
///  this template declares for those keys.
/// </summary>
public static class TorrcSettings
{
    private const string KeyTemplate = "torrc_template_v6";

    public const string DefaultTemplate = @"SocksPolicy accept 127.0.0.1
SocksPolicy reject *

CircuitPadding 0
ConnectionPadding 0
UseMicrodescriptors 1

DormantOnFirstStartup 0
DormantCanceledByStartup 1
LearnCircuitBuildTimeout 0
MaxCircuitDirtiness 600
NumEntryGuards 10
NumDirectoryGuards 6
KeepalivePeriod 3600

ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
ClientBootstrapConsensusFallbackDownloadInitialDelay 0
ClientBootstrapConsensusAuthorityOnlyDownloadInitialDelay 0
ClientBootstrapConsensusMaxInProgressTries 6

FetchDirInfoEarly 1
FetchDirInfoExtraEarly 1

PathsNeededToBuildCircuits 0.25

DisableDebuggerAttachment 1
AvoidDiskWrites 1
SafeLogging 1

ConfluxEnabled 1
ConfluxClientUX throughput

CircuitsAvailableTimeout 4320
CircuitStreamTimeout 60
CircuitBuildTimeout 40
NumPrimaryGuards 15
Schedulers Vanilla
MaxClientCircuitsPending 128
CircuitPriorityHalflife 5
SocksTimeout 120

ConfluxNumSets 32
ConfluxNumLinkedSets 32
ConfluxNumLegs 2
ConfluxSetSelection 1
ConfluxSetRttPct 10";

    /// <summary>The editable torrc template as a plain file: EDIT TORRC opens
    /// it in Notepad, and while it exists it wins over the prefs copy — what
    /// was saved there is what the next connect writes to the real torrc.</summary>
    public static readonly string TemplateFile = Path.Combine(AppPaths.Root, "torrc.template");

    private static string? _fileCache;
    private static string _fileStamp = "";

    /// <summary>Current torrc template (file first, then prefs, then the bundled default).</summary>
    public static string Template()
    {
        try
        {
            if (File.Exists(TemplateFile))
            {
                var stamp = File.GetLastWriteTimeUtc(TemplateFile).Ticks.ToString();
                if (_fileCache != null && stamp == _fileStamp) return _fileCache;
                _fileCache = File.ReadAllText(TemplateFile);
                _fileStamp = stamp;
                return _fileCache;
            }
        }
        catch
        {
            // Notepad can hold the file open mid-save; prefs are the fallback.
        }
        return Config.GetRaw(KeyTemplate) ?? DefaultTemplate;
    }

    /// <summary>Create the template file from the current template, so
    /// Notepad always has something real to open.</summary>
    public static void EnsureTemplateFile()
    {
        if (File.Exists(TemplateFile)) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var text = Config.GetRaw(KeyTemplate) ?? DefaultTemplate;
            File.WriteAllText(TemplateFile, text);
            _fileCache = text;
            _fileStamp = File.GetLastWriteTimeUtc(TemplateFile).Ticks.ToString();
        }
        catch
        {
            // Seeding failed: Notepad offers to create the file itself.
        }
    }

    public static void SetTemplate(string text)
    {
        var trimmed = text.Trim();
        Config.SetRaw(KeyTemplate, trimmed);
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(TemplateFile, trimmed);
            _fileCache = trimmed;
            _fileStamp = File.GetLastWriteTimeUtc(TemplateFile).Ticks.ToString();
        }
        catch
        {
            // Prefs still hold the value for a connect without the file.
        }
    }

    public static void ResetTemplate()
    {
        Config.RemoveRaw(KeyTemplate);
        try
        {
            if (File.Exists(TemplateFile)) File.Delete(TemplateFile);
        }
        catch
        {
            // Open in Notepad: the user's editor wins; next seed re-reads prefs.
        }
        _fileCache = null;
        _fileStamp = "";
    }

    /// <summary>Non-blank, non-comment lines from the template, written to torrc on connect.</summary>
    public static IReadOnlyList<string> TemplateLines() =>
        Template()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToArray();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Integer value of a directive in the current template, so a caller stays
    /// consistent with a template the user can edit. The last occurrence wins,
    /// the same way Tor reads a torrc.
    /// </summary>
    public static int IntValue(string key, int fallback)
    {
        var found = fallback;
        foreach (var line in TemplateLines())
        {
            var parts = Whitespace.Split(line);
            if (parts.Length >= 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[1], out var v))
            {
                found = v;
            }
        }
        return found;
    }
}

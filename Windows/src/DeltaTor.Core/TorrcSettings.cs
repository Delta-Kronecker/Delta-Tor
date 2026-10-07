using System.Text.RegularExpressions;

namespace DeltaTor.Core;

/// <summary>
/// User-editable torrc as a single, ready-made template (Windows counterpart
/// of Android TorrcSettings; SharedPreferences key "deltator" /
/// "torrc_template_v5" lives in the same prefs file as <see cref="Config"/>).
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
/// The shipped template carries no comments, because it is shown verbatim in
/// an editable text field. The reasoning lives here instead:
///
///  - No fixed `SocksPort` / `HTTPTunnelPort` / `DNSPort` / `ControlPort`,
///    ever. Three Tor processes run in parallel and the app owns the only
///    SOCKS5 listener; a hard-coded port here would be claimed by whichever
///    started first and make the other two fail. `SocksPolicy` only.
///  - `ConfluxEnabled` / `ConfluxClientUX` are the only Conflux knobs this
///    Android tor binary has. The `ConfluxNum*` family is fork-only and would
///    abort startup here, so it is left out rather than parked as a comment.
///  - `CircuitPadding`, `ConnectionPadding` and `UseMicrodescriptors` trade
///    cover traffic for throughput. That is a deliberate choice, not a default.
///  - `MaxCircuitDirtiness`, `SocksTimeout`, `CircuitBuildTimeout` and
///    `MaxClientCircuitsPending` appear twice, and the repeat is intentional:
///    torrc is last-wins, so the tail block overrides the bootstrap block above
///    it. The tail is the reconnect tuning. 24h circuits kept feeding a circuit
///    Tor had not noticed was dead; a 2 minute `SocksTimeout` held stalled
///    tun2socks connections; and a 20s `CircuitBuildTimeout` threw away a build
///    that was about to succeed, so a returning network paid for several
///    complete attempts and every one of them read as a dead link to the
///    liveness probe.
///
///  On ordering: the file is assembled as forced defaults, then this template,
///  then `ClientTransportPlugin`, then `Bridge`, then `ControlPort` (see
///  TorRunner.WriteTorrc). Later wins, so the template overrides the forced
///  defaults, and the Snowflake runner's own `ConnectionPadding 1` /
///  `SafeLogging 0` / `NumEntryGuards 1` are in turn overridden by anything
///  this template declares for those keys.
///  - `NewCircuitPeriod 10` is how often Tor resets per-circuit failure counts
///    and expires aged circuits — not how often it builds one. Lowering it
///    churns circuits without warming anything.
///  - `Schedulers Vanilla`, measured faster on a phone than `KISTLite,Vanilla`.
///    KISTLite only exists to batch cells so Conflux can spread a set over
///    several circuits, and with `ConfluxEnabled 0` below there are no circuit
///    sets to spread over: the batching is pure overhead. Naming one scheduler
///    is what makes this explicit — Tor's rule is "first type in the list that
///    this build can use", so `KISTLite,Vanilla` silently runs Vanilla while
///    looking like it asks for something smarter.
///  - `ConfluxEnabled 0`, for the same reason. Conflux multiplexes several
///    circuits per set, a throughput feature; on a phone its cost is paid on
///    every single connect and it measurably delayed the first usable circuit.
///  - No `CircuitPriorityHalflife`. It is a cell-EWMA in tenths of the 10s
///    scheduler tick, so `5` meant "half a tick", and the consensus default
///    (30s) is a saner spread than anything worth pinning by hand.
///  - `PathsNeededToBuildCircuits 0.25`. Tor clamps anything under 0.25 back
///    up to 0.25 and logs a warning, so 0.1 bought nothing but a log_warn.
///  - `CircuitStreamTimeout 60`. 10 was Tor's floor
///    (`MIN_CIRCUIT_STREAM_TIMEOUT`), not a choice: it bounded the lifetime of
///    a stream on its circuit, so a page slower than 10s had its circuit torn
///    down mid-load and rebuilt from scratch.
///  - `NumEntryGuards 10` / `NumPrimaryGuards 15`. Guards are kept, not raced
///    fast: a bigger list is a better fingerprint, a longer first connect.
///    These two are also locked together — Tor refuses to start if
///    NumEntryGuards exceeds NumPrimaryGuards.
/// </summary>
public static class TorrcSettings
{
    private const string KeyTemplate = "torrc_template_v5";

    public const string DefaultTemplate = @"SocksPolicy accept 127.0.0.1
SocksPolicy reject *

CircuitPadding 0
ConnectionPadding 0
UseMicrodescriptors 1

DormantOnFirstStartup 0
DormantCanceledByStartup 1
LearnCircuitBuildTimeout 0
CircuitBuildTimeout 30
MaxCircuitDirtiness 3600
NumEntryGuards 10
NumDirectoryGuards 6
MaxClientCircuitsPending 64
SocksTimeout 60

ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
ClientBootstrapConsensusFallbackDownloadInitialDelay 0
ClientBootstrapConsensusAuthorityOnlyDownloadInitialDelay 0
ClientBootstrapConsensusMaxInProgressTries 6

FetchDirInfoEarly 1
FetchDirInfoExtraEarly 1

PathsNeededToBuildCircuits 0.25

DisableDebuggerAttachment 1
SafeLogging 1

ConfluxEnabled 0

MaxCircuitDirtiness 600
NewCircuitPeriod 10
SocksTimeout 30
CircuitsAvailableTimeout 4320
CircuitStreamTimeout 60
CircuitBuildTimeout 40
NumPrimaryGuards 15
Schedulers Vanilla
MaxClientCircuitsPending 128";

    /// <summary>Current torrc template (falls back to the bundled default).</summary>
    public static string Template() =>
        Config.GetRaw(KeyTemplate) ?? DefaultTemplate;

    public static void SetTemplate(string text) =>
        Config.SetRaw(KeyTemplate, text.Trim());

    public static void ResetTemplate() =>
        Config.RemoveRaw(KeyTemplate);

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

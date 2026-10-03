package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences

/**
 * User-editable torrc as a single, ready-made template.
 *
 * Only this one block is exposed for editing in Settings. On every connect it is
 * written to the real torrc verbatim, and the pluggable transports (ClientTransportPlugin)
 * plus the selected bridge lines (Bridge ...) are appended automatically so bridges
 * are always part of the generated file. Later lines win in torrc, so the template
 * overrides the forced defaults in [TorRunner] when it declares them.
 *
 * The prefs key carries a version suffix: bumping it hands every existing install
 * the new default template instead of leaving it on the values it was shipped with.
 *
 * The shipped template carries no comments, because it is shown verbatim in an
 * editable text field and prose about the config is not what someone editing a
 * config is looking at. The reasoning lives here instead, where it does not get
 * written into the user's torrc:
 *
 *  - No fixed `SocksPort` / `HTTPTunnelPort` / `DNSPort` / `ControlPort`, ever.
 *    Three Tor processes run in parallel and the app owns the only SOCKS5
 *    listener; a hard-coded port here would be claimed by whichever started
 *    first and make the other two fail. `SocksPolicy` only.
 *  - `ConfluxEnabled` / `ConfluxClientUX` are the only Conflux knobs this Android
 *    tor binary has. The `ConfluxNum*` family is fork-only and would abort
 *    startup here, so it is left out rather than parked as a comment.
 *  - `CircuitPadding`, `ConnectionPadding` and `UseMicrodescriptors` trade
 *    cover traffic for throughput. That is a deliberate choice, not a default.
 *  - `MaxCircuitDirtiness`, `SocksTimeout`, `CircuitBuildTimeout` and
 *    `MaxClientCircuitsPending` appear twice, and the repeat is intentional:
 *    torrc is last-wins, so the tail block overrides the bootstrap block above it.
 *    The tail is the reconnect tuning. 24h circuits kept feeding a circuit Tor
 *    had not noticed was dead; a 2 minute `SocksTimeout` held stalled tun2socks
 *    connections; and a 20s `CircuitBuildTimeout` threw away a build that was
 *    about to succeed, so a returning network paid for several complete attempts
 *    and every one of them read as a dead link to the liveness probe.
 *
 *  On ordering, which is easy to get backwards from the field names: the file is
 *  assembled as forced defaults, then this template, then `ClientTransportPlugin`,
 *  then `Bridge`, then `ControlPort` (see `TorRunner.writeTorrc`). Later wins, so
 *  the template overrides the forced defaults, and the Snowflake runner's own
 *  `ConnectionPadding 1` / `SafeLogging 0` / `NumEntryGuards 1` are in turn
 *  overridden by anything this template declares for those keys.
 *  - `NewCircuitPeriod 10` is how often Tor resets per-circuit failure counts and
 *    expires aged circuits -- not how often it builds one, which is a common and
 *    expensive misreading. Lowering it churns circuits without warming anything.
 *  - `Schedulers KISTLite,Vanilla`, and specifically not `KIST,Vanilla`. The list
 *    is ordered by priority and Tor stops at the first type it can use, so the
 *    KIST-first version fell straight through to Vanilla on Android: KIST needs
 *    the Linux `SIOCOUTQNSD` ioctl and is not compiled into the NDK build. KISTLite
 *    keeps the batching -- which is what makes Conflux actually use more than one
 *    circuit per set -- without the kernel dependency, and Vanilla stays as the
 *    fallback for a platform where even KISTLite is unusable.
 *  - No `CircuitPriorityHalflife`. It is a cell-EWMA in tenths of the 10s scheduler
 *    tick, so `5` meant "half a tick", and the consensus default (30s) is a saner
 *    spread than anything worth pinning by hand on a phone.
 *  - `PathsNeededToBuildCircuits 0.25`, exactly what the line above used to say less
 *    of. Tor clamps anything under 0.25 back up to 0.25 and logs a warning, so 0.1
 *    bought nothing but a `log_warn` on every start.
 *  - `CircuitStreamTimeout 60`. 10 was Tor's floor (`MIN_CIRCUIT_STREAM_TIMEOUT`),
 *    not a choice: it bounded the lifetime of a stream on its circuit, so a page
 *    slower than 10s had its circuit torn down mid-load and rebuilt from scratch.
 *  - `NumEntryGuards 10` / `NumPrimaryGuards 15`. Guards are kept, not raced fast:
 *    a bigger list is a better fingerprint, a longer first connect. These two are
 *    also locked together -- Tor refuses to start if NumEntryGuards exceeds
 *    NumPrimaryGuards.
 */
object TorrcSettings {

    private const val PREFS = "deltator"
    private const val KEY_TEMPLATE = "torrc_template_v5"
    private lateinit var prefs: SharedPreferences

    val defaultTemplate: String = """
        SocksPolicy accept 127.0.0.1
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

        ConfluxEnabled 1
        ConfluxClientUX throughput

        MaxCircuitDirtiness 600
NewCircuitPeriod 10
        SocksTimeout 30
        CircuitsAvailableTimeout 4320
        CircuitStreamTimeout 60
        CircuitBuildTimeout 40
        NumPrimaryGuards 15
        Schedulers KISTLite,Vanilla
        MaxClientCircuitsPending 128
    """.trimIndent()

    fun init(context: Context) {
        prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    }

    /** Current torrc template (falls back to the bundled default). */
    fun template(): String = prefs.getString(KEY_TEMPLATE, null) ?: defaultTemplate

    fun setTemplate(text: String) {
        prefs.edit().putString(KEY_TEMPLATE, text.trim()).apply()
    }

    fun resetTemplate() {
        prefs.edit().remove(KEY_TEMPLATE).apply()
    }

    /** Non-blank, non-comment lines from the template, written to torrc on connect. */
    fun templateLines(): List<String> = template().lines()
        .map { it.trim() }
        .filter { it.isNotBlank() && !it.startsWith("#") }

    /**
     * Integer value of a directive in the current template, so a caller stays
     * consistent with a template the user can edit. The last occurrence wins,
     * the same way Tor reads a torrc.
     */
    fun intValue(key: String, fallback: Int): Int {
        var found = fallback
        for (line in templateLines()) {
            val parts = line.split(Regex("\\s+"))
            if (parts.size >= 2 && parts[0].equals(key, ignoreCase = true)) {
                found = parts[1].toIntOrNull() ?: found
            }
        }
        return found
    }
}
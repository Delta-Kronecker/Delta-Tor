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
 * The default is the fork's own torrc-android, fetched from the
 * Delta-Kronecker/tor branch (refs/heads/main) into `assets/torrc` at build time
 * (Android/fetch-tor.sh), so the tuning the core was built with is the tuning the
 * app starts from, and it tracks the branch. [builtinTemplate] is only a
 * last-resort copy for a build that skipped the fetch.
 *
 * [init] strips blank and comment lines before the asset becomes the template: the
 * fetched torrc carries prose, and the template is shown verbatim in an editable
 * text field and written into the user's torrc, where that prose does not belong.
 * Tor ignores blanks and comments either way, so nothing is lost.
 *
 * It also strips the engine-owned directives below, whatever the fetched file
 * says. They either do not exist on Android, or [TorRunner] already supplies them
 * and a shipped copy would win and break them (torrc is last-wins):
 *
 *  - No fixed `SocksPort` / `HTTPTunnelPort` / `DNSPort` / `ControlPort` /
 *    `CookieAuthentication`, ever. Three Tor processes run in parallel and the app
 *    owns the only SOCKS5 listener; a hard-coded port here would be claimed by
 *    whichever started first and make the other two fail. `SocksPolicy` only.
 *    `ControlPort` is appended by the app with a free ephemeral port, and only
 *    when exit-country steering actually needs it.
 *  - No `DataDirectory`: the app writes the absolute per-runner path.
 *  - No `GeoIPFile` / `GeoIPv6File`: the app generates the database itself and
 *    passes absolute paths. A relative `data/geoip` here would shadow them and
 *    silently disable country steering.
 *  - No `ClientTransportPlugin`: the app appends lyrebird's `socks5` line for each
 *    transport it launched. The Windows pluggable-transport executables those
 *    directives point at do not exist here.
 *
 * The Android branch file avoids these already, so stripping normally removes
 * nothing; it stays a guard so a branch file that grows Windows-only engine lines
 * cannot leak into the engine's own config. Whatever survives is still run through
 * [TorCoreOptions] before being written, because the fork's own torrc ships options
 * its core does not implement and Tor refuses to start on an unknown one.
 *
 * The prefs key carries a version suffix: bumping it hands every existing install
 * the new default template instead of leaving it on the values it was shipped with.
 */
object TorrcSettings {

    private const val PREFS = "deltator"
    private const val KEY_TEMPLATE = "torrc_template_v8"
    private const val SHIPPED_ASSET = "torrc"

    /**
     * Directives the engine owns: [TorRunner] always writes its own line for these
     * into the real torrc (a dynamic port, an absolute path, the lyrebird CMETHOD)
     * and a shipped copy would win over it because torrc is last-wins. Stripped from
     * the shipped default only, never from a user's saved template.
     */
    private val ENGINE_OWNED = setOf(
        "socksport", "httptunnelport", "dnsport", "controlport",
        "cookieauthentication", "datadirectory", "geoipfile", "geoipv6file",
        "clienttransportplugin"
    )

    private val WHITESPACE = Regex("\\s+")

    private lateinit var prefs: SharedPreferences

    /** The fork's own torrc, fetched into assets at build time; null until [init]. */
    private var shippedTemplate: String? = null

    /**
     * The default torrc: the fork's torrc-android, fetched into `assets/torrc` by
     * Android/fetch-tor.sh, or [builtinTemplate] when that asset is absent.
     */
    val defaultTemplate: String
        get() = shippedTemplate ?: builtinTemplate

    /**
     * Last-resort copy for a build that skipped the fetch. Kept in sync with the
     * fork's torrc-android by hand; the fetched asset is what normally ships.
     */
    private val builtinTemplate: String = """
SocksPolicy accept 127.0.0.1
SocksPolicy reject *

CircuitPadding 0
ConnectionPadding 0
UseMicrodescriptors 1
HardwareAcceleration 1

DormantOnFirstStartup 0
DormantCanceledByStartup 1
LearnCircuitBuildTimeout 1
MaxCircuitDirtiness 1800
NumEntryGuards 10
NumDirectoryGuards 6
KeepalivePeriod 3600
ClientUseIPv6 1

ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
ClientBootstrapConsensusFallbackDownloadInitialDelay 0
ClientBootstrapConsensusAuthorityOnlyDownloadInitialDelay 0
ClientBootstrapConsensusMaxInProgressTries 10

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
# KISTLite is the best scheduler available on Android; Vanilla is only fallback.
Schedulers KIST,KISTLite,Vanilla
MaxClientCircuitsPending 256
CircuitPriorityHalflife 5
SocksTimeout 30

# Keep a pool of ready conflux sets. Restrict new streams to the fastest
# ConfluxSetRttPct% of candidate sets (33% keeps about 3 sets with the 12-set
# pool), then use the fewest-streams policy across those to balance load.

# With more than two legs per set (ConfluxNumLegs above), upstream's conflux
# scheduler wants cfx_drain_pct 100 so a leg can be switched away from as soon
# as its in-flight data drains; otherwise switching is blocked.
ConfluxDrainPct 100
# Keep the full prebuilt-set pool even though conflux Exits are a minority:
# by default tor builds a single prebuilt set when under 60% of Exits support
# conflux, which caps parallelism.
ConfluxLowExitThreshold 1

# DeltaTor speed tuning: buffers. Keep more cells queued per circuit and per OR
# connection so high-latency links stay busy instead of going idle between
# scheduler runs (costs memory).
CircuitCellQueueHigh 1000
CircuitCellQueueLow 64
OrConnHighWater 262144
OrConnLowWater 32768

# DeltaTor speed tuning: congestion window. Start with a bigger window, grow it
# faster and never let it collapse, so uploads ramp up sooner.
CongestionControlCwndInit 300
CongestionControlCwndInc 2
CongestionControlCwndIncPctSs 300
CongestionControlCwndIncRate 62
CongestionControlCwndMin 300

# DeltaTor speed tuning: start bandwidth (BDP) estimation sooner and smooth it
# with more congestion windows of acks so the estimate reacts faster.
CongestionControlBweMin 1
CongestionControlEwmaCwndPct 70
CongestionControlEwmaMax 15

# DeltaTor speed tuning: let the client buffer more downloaded data before
# pausing a stream, which helps bulk download throughput, and re-open the flow
# after smaller drain-rate improvements.
CongestionControlXoffClient 1000
CongestionControlXonChangePct 15

# DeltaTor speed tuning: send the first application data optimistically with the
# circuit-create/SOCKS-connect request, saving a round trip per stream.
OptimisticData 1

# DeltaTor speed tuning: allow more in-flight data to sit in the kernel socket
# buffer (5 congestion windows instead of 2).
KISTSockBufSizeFactor 4.0

# --- DeltaTor phase 1: code-patch options ---
# Vegas congestion-control thresholds on exit circuits. Larger alpha/beta/gamma
# let the window grow further before backing off, raising throughput at the cost
# of queueing (bufferbloat) and memory. 0 = consensus default.
CongestionControlVegasAlphaExit 230
CongestionControlVegasBetaExit 300
CongestionControlVegasGammaExit 230
CongestionControlVegasDeltaExit 380
CongestionControlVegasSsMax 8192
CongestionControlVegasCwndFullMinPct 20
CongestionControlVegasCwndFullGap 8
# Give up on an unlinked leg later so sets complete more reliably.
ConfluxMaxUnlinkedLegRetry 5
# Queue more circuit-build crypto tasks per CPU so the set pool fills faster.
MaxPendingTasksPerCPU 128
# Request consensus diffs for older consensuses => smaller directory fetches.
DirConsensusDiffMaxAge 168

# --- DeltaTor phase 1: onion + guard ---
HsDirSpreadFetch 6
HsIntroNumExtra 2
GuardMaxSampleSize 1200
GuardMinFilteredSampleSize 30

ConfluxNumSets 16
ConfluxNumLinkedSets 16
ConfluxNumLegs 2
ConfluxSetSelection 2
ConfluxSetRttPct 7
    """

    fun init(context: Context) {
        prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        shippedTemplate = readShippedTemplate(context)
    }

    /**
     * The fetched asset, stripped of blank lines, comments and the engine-owned
     * directives (see [ENGINE_OWNED]). Returns null (and leaves [builtinTemplate]
     * in charge) when the asset is missing, i.e. a build that never ran
     * Android/fetch-tor.sh.
     */
    private fun readShippedTemplate(context: Context): String? = runCatching {
        context.assets.open(SHIPPED_ASSET).bufferedReader().use { it.readText() }
    }.getOrNull()
        ?.lines()
        ?.map { it.trim() }
        ?.filter { it.isNotBlank() && !it.startsWith("#") }
        ?.filterNot { isEngineOwned(it) }
        ?.joinToString("\n")
        ?.takeIf { it.isNotBlank() }

    /** True when the line's directive is one [TorRunner] owns (torrc is last-wins). */
    private fun isEngineOwned(line: String): Boolean {
        val option = line.split(WHITESPACE, limit = 2).firstOrNull()?.lowercase().orEmpty()
        return option in ENGINE_OWNED
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

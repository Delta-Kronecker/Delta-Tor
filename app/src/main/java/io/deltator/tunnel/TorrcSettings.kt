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
 */
object TorrcSettings {

    private const val PREFS = "deltator"
    private const val KEY_TEMPLATE = "torrc_template_v2"
    private lateinit var prefs: SharedPreferences

    val defaultTemplate: String = """
        # DeltaTor (Android) - speed-optimized torrc template (tor 0.4.9.11)
        # Adapted from the portable Windows DeltaTor config. All parameters are
        # kept, except lines that must be Android-managed:
        #   - DataDirectory / GeoIP* : absolute paths are set by the app.
        #   - ClientTransportPlugin  : appended automatically on connect.
        #   - Log path               : app reads stdout for the in-app log.
        # Bridges are appended automatically on connect.

        # --- Local proxies (VPN tunnel uses its own SocksPort; extra listeners kept) ---
        CookieAuthentication 1
        SocksPolicy accept 127.0.0.1
        SocksPolicy reject *

        # --- Remove cover-traffic padding (privacy OFF; pure throughput) ---
        CircuitPadding 0
        ConnectionPadding 0
        UseMicrodescriptors 1

        # --- Bootstrap/circuit tuning ---
        DormantOnFirstStartup 0
        DormantCanceledByStartup 1
        LearnCircuitBuildTimeout 0
        CircuitBuildTimeout 30
        MaxCircuitDirtiness 3600
        NumEntryGuards 15
        NumDirectoryGuards 6
        MaxClientCircuitsPending 64
        SocksTimeout 60
        KeepalivePeriod 3600

        # --- Faster start from a clean state (lower directory download delays) ---
        ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
        ClientBootstrapConsensusFallbackDownloadInitialDelay 0
        ClientBootstrapConsensusAuthorityOnlyDownloadInitialDelay 0
        ClientBootstrapConsensusMaxInProgressTries 6

        # --- Fetch directory info early so the first circuits are ready sooner ---
        FetchDirInfoEarly 1
        FetchDirInfoExtraEarly 1

        # --- Consider the client connected once 25% of paths succeed ---
        PathsNeededToBuildCircuits 0.25

        # --- Operational (Android manages paths; the rest is kept) ---
        DisableDebuggerAttachment 1
        AvoidDiskWrites 1
        SafeLogging 1

        # --- EXPERIMENT: Conflux (split traffic across circuits) ---
        # This Android tor build supports ConfluxEnabled + ConfluxClientUX only.
        # The fork-only knobs below are NOT in the Android binary and would abort
        # startup, so they are preserved as comments.
        ConfluxEnabled 1
        ConfluxClientUX throughput
        # ConfluxNumSets 32
        # ConfluxNumLinkedSets 32
        # ConfluxNumLegs 1
        # ConfluxSetSelection 1
        # ConfluxSetRttPct 15

        # --- strategy: ultimate (later values win over the tuning above) ---
        MaxCircuitDirtiness 86400
        CircuitsAvailableTimeout 4320
        CircuitStreamTimeout 10
        CircuitBuildTimeout 20
        NumPrimaryGuards 20
        Schedulers Vanilla
        MaxClientCircuitsPending 128
        CircuitPriorityHalflife 5
        SocksTimeout 120

        # --- socks listeners (in addition to the forced tunnel SocksPort) ---
        SocksPort 127.0.0.1:9350 IsolateSOCKSAuth
        SocksPort 127.0.0.1:9352 NoIsolateSOCKSAuth
        HTTPTunnelPort 127.0.0.1:8350
        DNSPort 127.0.0.1:63530
        ControlPort 127.0.0.1:9351
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
}
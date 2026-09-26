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
    private const val KEY_TEMPLATE = "torrc_template"
    private lateinit var prefs: SharedPreferences

    val defaultTemplate: String = """
        # DeltaTor — torrc template
        # Every non-comment line below is written to the real torrc when you connect.
        # Bridges and pluggable transport plugins are appended automatically.

        # Connectivity
        ClientUseIPv4 1
        ClientUseIPv6 1
        ClientPreferIPv6ORPort auto

        # Circuit & guard behaviour
        CircuitBuildTimeout 60
        LearnCircuitBuildTimeout 0
        KeepalivePeriod 30
        NumEntryGuards 1

        # Exit policy
        StrictNodes 0

        # Diagnostics
        Log notice stdout

        # Reliability
        SafeLogging 0
        AvoidDiskWrites 1
        DormantClientTimeout 2419200
        ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
        ConnectionPadding 1
        ReducedConnectionPadding 0
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
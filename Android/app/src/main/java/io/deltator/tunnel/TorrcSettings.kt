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
 * config is looking for. The reasoning lives here instead, where it does not get
 * written into the user's torrc.
 *
 * The values are the ones the Windows build ships with, kept verbatim so both
 * clients behave the same. The directives below are deliberately absent: they
 * either do not exist on Android, or [TorRunner] already supplies them and a
 * second copy here would win and break them (torrc is last-wins).
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
 */
object TorrcSettings {

    private const val PREFS = "deltator"
    private const val KEY_TEMPLATE = "torrc_template_v6"
    private lateinit var prefs: SharedPreferences

    val defaultTemplate: String = """SocksPolicy accept 127.0.0.1
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
SocksTimeout 30
ConfluxNumSets 32
ConfluxNumLinkedSets 32
ConfluxNumLegs 2
ConfluxSetSelection 1
ConfluxSetRttPct 10"""

    /**
     * The error fallback: conservative values the connect retries with once,
     * whenever an attempt fails. Applied automatically by [applyErrorFallback]
     * and kept from then on, so the next connects boot with it too.
     */
    val errorFallbackTemplate: String = """SocksPolicy accept 127.0.0.1
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
MaxClientCircuitsPending 128"""

    /**
     * Switch to the error fallback when the current template is not already it.
     * Returns true only when something changed, so the caller that retries on
     * failure cannot loop: a retry that fails again lands on a template that is
     * already the fallback, this returns false, and the failure stays terminal.
     */
    fun applyErrorFallback(): Boolean {
        if (template() == errorFallbackTemplate) return false
        setTemplate(errorFallbackTemplate)
        return true
    }

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
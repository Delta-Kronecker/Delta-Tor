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
 * The default is the fork's own torrc, fetched from the Delta-Kronecker/tor release
 * into `assets/torrc` at build time (Android/fetch-tor.sh), so the tuning the core
 * was built with is the tuning the app starts from, and it tracks every new core.
 * [builtinTemplate] is only a last-resort copy for a build that skipped the fetch.
 *
 * [init] strips blank and comment lines before the asset becomes the template: the
 * release torrc carries prose, and the template is shown verbatim in an editable
 * text field and written into the user's torrc, where that prose does not belong.
 * Tor ignores blanks and comments either way, so nothing is lost.
 *
 * The prefs key carries a version suffix: bumping it hands every existing install
 * the new default template instead of leaving it on the values it was shipped with.
 *
 * The directives below are deliberately absent from any default. They either do
 * not exist on Android, or [TorRunner] already supplies them and a second copy
 * here would win and break them (torrc is last-wins):
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
 * The release torrc already honors all of these, so it is adopted verbatim.
 */
object TorrcSettings {

    private const val PREFS = "deltator"
    private const val KEY_TEMPLATE = "torrc_template_v6"
    private const val SHIPPED_ASSET = "torrc"
    private lateinit var prefs: SharedPreferences

    /** The fork's own torrc, fetched into assets at build time; null until [init]. */
    private var shippedTemplate: String? = null

    /**
     * The default torrc: the core's own, fetched into `assets/torrc` by
     * Android/fetch-tor.sh, or [builtinTemplate] when that asset is absent.
     */
    val defaultTemplate: String
        get() = shippedTemplate ?: builtinTemplate

    /**
     * Last-resort copy for a build that skipped the fetch. Kept in sync with the
     * fork's release torrc by hand; the fetched asset is what normally ships.
     */
    private val builtinTemplate: String = """SocksPolicy accept 127.0.0.1
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
        shippedTemplate = readShippedTemplate(context)
    }

    /**
     * The fetched asset, stripped of blank and comment lines. Returns null (and
     * leaves [builtinTemplate] in charge) when the asset is missing, i.e. a build
     * that never ran Android/fetch-tor.sh.
     */
    private fun readShippedTemplate(context: Context): String? = runCatching {
        context.assets.open(SHIPPED_ASSET).bufferedReader().use { it.readText() }
    }.getOrNull()
        ?.lines()
        ?.map { it.trim() }
        ?.filter { it.isNotBlank() && !it.startsWith("#") }
        ?.joinToString("\n")
        ?.takeIf { it.isNotBlank() }

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
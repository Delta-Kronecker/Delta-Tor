package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.util.concurrent.TimeUnit

/**
 * The set of torrc options the shipped Tor core actually accepts.
 *
 * The default torrc is the fork's own, fetched into `assets/torrc` by
 * Android/fetch-tor.sh so it tracks every new core. That is what we want, but the
 * fork's release torrc ships options its own core does not implement --
 * `HardwareAcceleration` is the one that bit us. Tor treats an unknown option as
 * fatal: `[warn] Failed to parse/validate config: Unknown option
 * 'HardwareAcceleration'.  Failing.` then `[err] Reading config failed`. The
 * process exits before it bootstraps, and because all transports share the same
 * template, one stale line takes every one of them down with a bare exit 139.
 *
 * Tor can be asked what it accepts: `<core> --list-torrc-options` prints one
 * option name per line and exits 0. [supported] runs exactly that against the
 * same binary the runner execs, so the template is pruned to what this core
 * knows instead of being trusted blindly.
 */
object TorCoreOptions {

    private const val TAG = "TorCoreOptions"
    private const val PROBE_TIMEOUT_SECONDS = 15L

    /** Option names are bare identifiers: letters, digits and underscore. */
    private val OPTION_NAME = Regex("[A-Za-z_][A-Za-z0-9_]*")

    private val lock = Any()
    @Volatile private var cached: Set<String>? = null

    /**
     * Lowercased option names the core accepts, or null when the probe cannot run
     * (missing binary, non-zero exit, timeout, exception). Null means "do not
     * filter": keeping the template intact is always safer than dropping lines
     * based on a failed probe. A failure is not cached, so a transient one heals
     * itself on the next connect.
     */
    fun supported(context: Context): Set<String>? {
        cached?.let { return it }
        synchronized(lock) {
            cached?.let { return it }
            return probe(context)?.also { cached = it }
        }
    }

    private fun probe(context: Context): Set<String>? {
        val core = File(context.applicationInfo.nativeLibraryDir, "libtor.so")
        if (!core.exists()) {
            Log.w(TAG, "core missing at $core; leaving the torrc template unfiltered")
            return null
        }
        return try {
            // stderr is folded into stdout so a chatty core cannot fill the other
            // pipe and deadlock the read; only bare identifiers survive the filter,
            // so a log line cannot be mistaken for an option name.
            val process = ProcessBuilder(core.absolutePath, "--list-torrc-options")
                .redirectErrorStream(true)
                .start()
            val names = LinkedHashSet<String>()
            BufferedReader(InputStreamReader(process.inputStream)).use { reader ->
                var line: String?
                while (reader.readLine().also { line = it } != null) {
                    val token = line!!.trim()
                    if (OPTION_NAME.matches(token)) names.add(token.lowercase())
                }
            }
            when {
                !process.waitFor(PROBE_TIMEOUT_SECONDS, TimeUnit.SECONDS) -> {
                    process.destroyForcibly()
                    Log.w(TAG, "core --list-torrc-options timed out; leaving the torrc template unfiltered")
                    null
                }
                process.exitValue() != 0 -> {
                    Log.w(TAG, "core --list-torrc-options exited ${process.exitValue()}; leaving the torrc template unfiltered")
                    null
                }
                names.isEmpty() -> {
                    Log.w(TAG, "core --list-torrc-options listed no options; leaving the torrc template unfiltered")
                    null
                }
                else -> {
                    Log.i(TAG, "core accepts ${names.size} torrc options")
                    names
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "core --list-torrc-options failed: ${e.message}; leaving the torrc template unfiltered")
            null
        }
    }
}

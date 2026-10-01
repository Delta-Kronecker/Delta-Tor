package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.BufferedReader
import java.io.InputStreamReader
import java.util.concurrent.atomic.AtomicBoolean

/**
 * How much exit capacity a country has.
 *
 * @param exits how many relays in that country carried the Exit flag
 * @param weight that country's share of all Tor exit bandwidth, 0..1
 */
data class ExitCapacity(val exits: Int, val weight: Float)

/**
 * Per-country exit capacity, read from the bundled table.
 *
 * The country picker used to be every country in the bundled GeoIP file: 248 of
 * them, of which 187 have no Tor exit at all and never did. Picking one of those
 * was not a slow choice, it was a choice that could not work, and nothing on
 * screen said so. On top of that sat a hand-written order of 24 countries whose
 * own comment claimed it was derived from recorded exits; checked against the
 * relay database, 12 of the 24 were dead or carried a rounding error's worth of
 * bandwidth, and two of them had no exits at all.
 *
 * The table answers the question instead, from Tor's own relay database: which
 * countries have exits running right now, how many, and what share of all exit
 * bandwidth they hold. Share is the figure that predicts speed, because a
 * country can have a hundred slow exits or a few quick ones. The spread is steep
 * enough to be worth having: the top fifteen countries hold about 97% of it.
 *
 * It ships as an asset rather than a fetch on purpose. The answer moves on the
 * scale of months, an app update carries a new table as easily as a new one, and
 * a country list is not worth a network round trip and a cache expiry on every
 * launch. Regenerate `assets/geoip/exit-capacity.tsv` when the numbers drift;
 * the recipe is in its own header.
 */
object ExitCapacityIndex {
    private const val TAG = "ExitCapacity"

    private val _byCountry = MutableStateFlow<Map<String, ExitCapacity>>(emptyMap())
    private val started = AtomicBoolean(false)

    /** Country code to capacity. Empty until the bundled table is read. */
    val byCountry: StateFlow<Map<String, ExitCapacity>> = _byCountry.asStateFlow()

    /** Reads the table once, off the main thread. Never blocks the caller. */
    fun load(context: Context) {
        if (!started.compareAndSet(false, true)) return
        val app = context.applicationContext
        Thread({
            val table = read(app)
            if (table.isNotEmpty()) _byCountry.value = table
            Log.i(TAG, "exit capacity: ${table.size} countries with running exits")
        }, "deltator-exit-capacity").apply { isDaemon = true }.start()
    }

    private fun read(context: Context): Map<String, ExitCapacity> {
        val out = HashMap<String, ExitCapacity>()
        try {
            context.assets.open("geoip/exit-capacity.tsv").use { input ->
                BufferedReader(InputStreamReader(input, Charsets.UTF_8)).useLines { lines ->
                    lines.forEach { line ->
                        if (line.isBlank() || line.startsWith("#")) return@forEach
                        val p = line.split('\t')
                        if (p.size < 4) return@forEach
                        val cc = p[0].trim().uppercase()
                        val n = p[2].trim().toIntOrNull() ?: return@forEach
                        val w = p[3].trim().toFloatOrNull() ?: return@forEach
                        if (cc.length != 2 || n <= 0) return@forEach
                        out[cc] = ExitCapacity(n, w)
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "exit capacity table unavailable: ${e.message}")
        }
        return out
    }
}
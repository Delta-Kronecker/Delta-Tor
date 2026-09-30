package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

/**
 * How much exit capacity a country has right now.
 *
 * @param exits how many relays in that country currently carry the Exit flag
 * @param weight that country's share of all Tor exit bandwidth, 0..1
 */
data class ExitCapacity(val exits: Int, val weight: Float)

/**
 * Per-country exit capacity, from Tor's own relay database.
 *
 * The country picker used to be every country in the bundled GeoIP table: 248 of
 * them, of which 186 have no Tor exit at all and never did. Picking one of those
 * was not a slow choice, it was a choice that could not work, and there was
 * nothing on screen to say so. The hand-written "recommended" order on top of it
 * was no better: of its 24 entries, 12 were dead or carried a rounding error's
 * worth of bandwidth, and two (`AM`, `EU`) had no exits whatsoever.
 *
 * Onionoo already knows the answer and it moves on its own. Asking it for the
 * running exits gives a count per country and, with it, that country's share of
 * total exit bandwidth, which is the number that actually predicts whether a
 * circuit built there will be fast. The distribution is steep enough to be
 * worth having: the top fifteen countries hold about 97% of all exit bandwidth.
 *
 * Fetched straight from the device's own connection, not through the tunnel:
 * the request goes to the Tor Project either way, and doing it directly means
 * the picker is populated before the first connection is ever made. Cached for
 * a week, since exits come and go on the scale of months.
 */
object ExitCapacityIndex {
    private const val TAG = "ExitCapacity"

    private const val ENDPOINT =
        "https://onionoo.torproject.org/details" +
            "?search=flag:Exit&search=running:true" +
            "&fields=country,consensus_weight_fraction"

    private const val PREFS = "deltator"
    private const val KEY_DATA = "exit_cc_capacity"
    private const val KEY_FETCHED = "exit_cc_capacity_at"
    private const val KEY_ATTEMPT = "exit_cc_capacity_try"
    private const val TTL_MS = 7L * 24 * 60 * 60 * 1000
    private const val RETRY_MS = 6L * 60 * 60 * 1000

    private val _byCountry = MutableStateFlow<Map<String, ExitCapacity>>(emptyMap())

    /** Country code to capacity. Empty until the cache or the network answers. */
    val byCountry: StateFlow<Map<String, ExitCapacity>> = _byCountry.asStateFlow()

    private val started = AtomicBoolean(false)

    /**
     * Publish whatever is cached right now, then refresh in the background if
     * the cache has gone stale. Never blocks the caller: the picker opens on
     * whatever it has and improves when the answer lands.
     */
    fun load(context: Context) {
        if (!started.compareAndSet(false, true)) return
        val app = context.applicationContext
        val prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val cached = parse(prefs.getString(KEY_DATA, "").orEmpty())
        if (cached.isNotEmpty()) _byCountry.value = cached
        val age = System.currentTimeMillis() - prefs.getLong(KEY_FETCHED, 0L)
        if (cached.isNotEmpty() && age < TTL_MS) {
            Log.i(TAG, "exit capacity from cache: ${cached.size} countries, ${age / 3600000}h old")
            return
        }
        // With nothing cached there is nothing to show for it, so do not spend
        // the launch on a ten second connect timeout to a host that may be
        // unreachable. Try again later in the day instead.
        val sinceTry = System.currentTimeMillis() - prefs.getLong(KEY_ATTEMPT, 0L)
        if (cached.isEmpty() && sinceTry < RETRY_MS) {
            Log.i(TAG, "no exit capacity yet, last try ${sinceTry / 60000}m ago")
            return
        }
        prefs.edit().putLong(KEY_ATTEMPT, System.currentTimeMillis()).apply()
        Thread({ refresh(app, prefs) }, "deltator-exit-capacity").apply {
            isDaemon = true
        }.start()
    }

    private fun refresh(app: Context, prefs: SharedPreferences) {
        try {
            val conn = URL(ENDPOINT).openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 20_000
            conn.requestMethod = "GET"
            conn.setRequestProperty("User-Agent", "DeltaTor-Android")
            conn.setRequestProperty("Accept", "application/json")
            val code = conn.responseCode
            if (code != 200) {
                Log.w(TAG, "onionoo HTTP $code")
                return
            }
            val body = conn.inputStream.bufferedReader().use { it.readText() }
            val totals = aggregate(body)
            if (totals.isEmpty()) {
                Log.w(TAG, "onionoo returned no relays")
                return
            }
            // Only overwrite the cache once the answer is complete: a truncated
            // body would otherwise persist as a short list for a week.
            prefs.edit()
                .putString(KEY_DATA, serialise(totals))
                .putLong(KEY_FETCHED, System.currentTimeMillis())
                .apply()
            _byCountry.value = totals
            Log.i(TAG, "exit capacity: ${totals.size} countries with running exits")
        } catch (e: Exception) {
            Log.w(TAG, "exit capacity fetch failed: ${e.message}")
        }
    }

    /**
     * Sum every relay's consensus weight by country.
     *
     * The weight is the only figure here that says anything about speed: a
     * country can have a hundred exits that are all far slower than one exit
     * somewhere with real transit. Counting relays alone would rank a country of
     * hobby relays above a country of well-connected ones.
     */
    private fun aggregate(body: String): Map<String, ExitCapacity> {
        val relays = JSONObject(body).optJSONArray("relays") ?: return emptyMap()
        val exits = HashMap<String, Int>()
        val weight = HashMap<String, Double>()
        for (i in 0 until relays.length()) {
            val relay = relays.optJSONObject(i) ?: continue
            val cc = relay.optString("country").trim().uppercase()
            if (cc.length != 2 || !cc.all { it in 'A'..'Z' }) continue
            exits[cc] = (exits[cc] ?: 0) + 1
            weight[cc] = (weight[cc] ?: 0.0) + relay.optDouble("consensus_weight_fraction", 0.0)
        }
        val out = HashMap<String, ExitCapacity>(exits.size)
        for (cc in exits.keys) {
            out[cc] = ExitCapacity(exits.getValue(cc), weight.getValue(cc).toFloat())
        }
        return out
    }

    /** "NL:630:0.0896,DE:420:0.0849,..." */
    private fun serialise(map: Map<String, ExitCapacity>): String =
        map.entries.joinToString(",") {
            "${it.key}:${it.value.exits}:${String.format(Locale.ROOT, "%.6f", it.value.weight)}"
        }

    private fun parse(raw: String): Map<String, ExitCapacity> {
        if (raw.isBlank()) return emptyMap()
        val out = HashMap<String, ExitCapacity>()
        for (entry in raw.split(',')) {
            val p = entry.split(':')
            if (p.size != 3) continue
            val cc = p[0].trim().uppercase()
            val n = p[1].trim().toIntOrNull() ?: continue
            val w = p[2].trim().toFloatOrNull() ?: continue
            if (cc.length != 2 || n <= 0) continue
            out[cc] = ExitCapacity(n, w)
        }
        return out
    }
}

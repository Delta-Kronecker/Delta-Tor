package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.InputStreamReader
import java.util.zip.GZIPInputStream

/**
 * One entry of the exit-country picker: an ISO country code and its name.
 */
data class ExitCountry(
    val code: String,
    val name: String
)

/**
 * Bundled db-ip country-lite (CC BY 4.0) dataset: sorted IP ranges -> country code.
 * Loaded lazily once per process from the geoip asset.
 */
private class CountryDb(
    private val starts: LongArray,
    private val ends: LongArray,
    private val codeIdx: IntArray,
    private val codeList: List<String>
) {
    fun country(ip: Long): String? {
        var lo = 0
        var hi = starts.size - 1
        while (lo <= hi) {
            val mid = (lo + hi) ushr 1
            if (starts[mid] <= ip) lo = mid + 1 else hi = mid - 1
        }
        if (hi < 0) return null
        return if (ip <= ends[hi]) codeList[codeIdx[hi]] else null
    }

    companion object {
        fun load(context: Context): CountryDb {
            val startsRaw = ArrayList<Long>()
            val endsRaw = ArrayList<Long>()
            val idxRaw = ArrayList<Int>()
            val codes = ArrayList<String>()
            val codeMap = HashMap<String, Int>()
            GZIPInputStream(context.assets.open("geoip/country.csv.gz")).use { gz ->
                BufferedReader(InputStreamReader(gz, Charsets.US_ASCII)).useLines { lines ->
                    lines.forEach { line ->
                        val p = line.split(',')
                        if (p.size < 3) return@forEach
                        // db-ip country-lite stores dotted quads, e.g. "1.0.0.0"
                        val a = ipv4ToLong(p[0].trim()) ?: return@forEach
                        val b = ipv4ToLong(p[1].trim()) ?: return@forEach
                        val cc = p[2].trim().uppercase()
                        if (cc.length != 2 || !cc.all { it in 'A'..'Z' }) return@forEach
                        startsRaw.add(a)
                        endsRaw.add(b)
                        val ci = codeMap.getOrPut(cc) { codes.size.also { codes.add(cc) } }
                        idxRaw.add(ci)
                    }
                }
            }
            val n = startsRaw.size
            val order = Array(n) { i -> i }
            order.sortBy { startsRaw[it] }
            val s = LongArray(n)
            val e = LongArray(n)
            val ix = IntArray(n)
            for (k in 0 until n) {
                s[k] = startsRaw[order[k]]
                e[k] = endsRaw[order[k]]
                ix[k] = idxRaw[order[k]]
            }
            Log.d("BridgeCountries", "Loaded ${n} IP blocks for geo lookup")
            return CountryDb(s, e, ix, codes)
        }
    }
}

/**
 * Country list for the EXIT NODE picker, plus an offline IP -> country lookup
 * used as a fallback when the online exit lookup is unavailable.
 *
 * The picker is a plain list of every country shipped in the GeoIP dataset,
 * sorted alphabetically by name so it is easy to scan. It intentionally does
 * not depend on the bridge cache: the list must always be there, even on a
 * first run with no bridges downloaded yet.
 */
object BridgeCountries {
    private const val TAG = "BridgeCountries"

    @Volatile private var db: CountryDb? = null
    @Volatile private var cachedNames: Map<String, String>? = null

    private fun countryNames(context: Context): Map<String, String> {
        cachedNames?.let { return it }
        val map = HashMap<String, String>()
        try {
            context.assets.open("geoip/countries.tsv").use { input ->
                BufferedReader(InputStreamReader(input, Charsets.UTF_8)).useLines { lines ->
                    lines.forEach { line ->
                        val p = line.split('\t')
                        if (p.size >= 2 && p[0].length == 2) map[p[0].trim().uppercase()] = p[1].trim()
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Country names unavailable: ${e.message}")
        }
        cachedNames = map
        return map
    }

    /** Country (code, name) for an IP that exited through the tunnel. */
    suspend fun countryInfo(context: Context, ip: String): Pair<String, String>? =
        withContext(Dispatchers.IO) {
            val geo = db ?: CountryDb.load(context).also { db = it }
            val l = ipv4ToLong(ip.trim().substringBefore(':')) ?: return@withContext null
            val cc = geo.country(l) ?: return@withContext null
            cc to (countryNames(context)[cc] ?: cc)
        }

    /**
     * Every selectable exit country, alphabetical by name. Cheap: the name table
     * is a few kB asset, no GeoIP parse and no bridge cache needed.
     */
    suspend fun top(context: Context): List<ExitCountry> = withContext(Dispatchers.IO) {
        try {
            countryNames(context)
                .filterKeys { it.length == 2 }
                .map { (cc, name) -> ExitCountry(cc, name) }
                .sortedBy { it.name.lowercase() }
        } catch (e: Exception) {
            Log.w(TAG, "Country list failed: ${e.message}")
            emptyList()
        }
    }
}

/** "1.2.3.4" -> packed 32-bit value, or null when not a dotted quad. */
private fun ipv4ToLong(host: String): Long? {
    val p = host.split('.')
    if (p.size != 4) return null
    var v = 0L
    for (octet in p) {
        val n = octet.toIntOrNull() ?: return null
        if (n !in 0..255) return null
        v = (v shl 8) or n.toLong()
    }
    return v
}
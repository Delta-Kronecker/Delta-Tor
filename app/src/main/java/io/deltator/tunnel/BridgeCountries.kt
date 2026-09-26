package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.util.zip.GZIPInputStream

/**
 * One entry of the exit-country ranking: how many unique bridge IPs run in that
 * country (all mirrors are pre-tested, so the count doubles as the performance
 * proxy). Sorted by bridge count descending, then by name.
 */
data class ExitCountry(
    val code: String,
    val name: String,
    val bridges: Int
) : Comparable<ExitCountry> {
    override fun compareTo(other: ExitCountry): Int {
        if (bridges != other.bridges) return other.bridges - bridges
        return name.compareTo(other.name)
    }
}

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
                        val a = p[0].trim().toLongOrNull() ?: return@forEach
                        val b = p[1].trim().toLongOrNull() ?: return@forEach
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
 * Aggregates the cached bridge lists by ISO country code of the bridge IP and
 * exposes the ranking for the EXIT NODE settings card.
 */
object BridgeCountries {
    private const val TAG = "BridgeCountries"

    @Volatile private var db: CountryDb? = null

    private fun countryNames(context: Context): Map<String, String> {
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
        return map
    }

    /** Rank countries by unique bridge IP count, best first. */
    suspend fun top(context: Context): List<ExitCountry> = withContext(Dispatchers.IO) {
        try {
            val geo = db ?: CountryDb.load(context).also { db = it }
            val names = countryNames(context)
            val perCountry = HashMap<String, HashSet<Long>>()
            for ((name, _) in ParallelTorManager.BRIDGE_SOURCES) {
                val f = File(context.filesDir, "bridges/$name.txt")
                if (!f.exists()) continue
                f.readLines().forEach { line ->
                    val ip = bridgeIp(line) ?: return@forEach
                    val cc = geo.country(ip) ?: return@forEach
                    perCountry.getOrPut(cc) { HashSet() }.add(ip)
                }
            }
            perCountry.map { (cc, set) ->
                ExitCountry(cc, names[cc] ?: cc, set.size)
            }.sorted().toList()
        } catch (e: Exception) {
            Log.w(TAG, "Exit ranking failed: ${e.message}")
            emptyList()
        }
    }

    /** Extract the IPv4 host from a bridge line token (ignores IPv6). */
    private fun bridgeIp(line: String): Long? {
        for (token in line.split(Regex("\\s+"))) {
            if (token.isEmpty()) continue
            val ip = ipv4ToLong(token.substringBefore(':'))
            if (ip != null) return ip
        }
        return null
    }

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
}
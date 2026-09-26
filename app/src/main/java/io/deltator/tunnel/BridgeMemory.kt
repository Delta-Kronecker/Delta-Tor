package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences
import io.deltator.util.AppLog as Log

/**
 * Remembers the bridges that actually worked, per transport, so the next connect
 * can start from them instead of grinding through the whole list again.
 *
 * A bridge counts as healthy once Tor fetched its descriptor, which is the point
 * where the pluggable-transport handshake and the descriptor download both
 * succeeded. [TorRunner] extracts those fingerprints from its own log while it
 * runs; [remember] stores them when the runner reaches 100%.
 *
 * The pool is a union across runs and is never shrunk, because a warm restart
 * (cached microdescriptors) no longer logs new bridge descriptors - the pool is
 * what carries that knowledge forward.
 */
object BridgeMemory {
    private const val TAG = "BridgeMemory"
    private const val PREFS = "deltator_bridge_memory"
    private const val MAX_PER_TRANSPORT = 60

    @Volatile private var prefs: SharedPreferences? = null

    private fun prefs(context: Context): SharedPreferences =
        prefs ?: synchronized(this) {
            prefs ?: context.applicationContext
                .getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .also { prefs = it }
        }

    private fun key(name: String) = "healthy_$name"

    /** Fingerprints remembered for one transport, in the order they were proven. */
    fun healthy(context: Context, name: String): List<String> =
        prefs(context).getString(key(name), "")
            .orEmpty()
            .split(',')
            .map { it.trim() }
            .filter { it.isNotEmpty() }

    /** Every remembered fingerprint across all transports. */
    fun allHealthy(context: Context): List<String> =
        TRANSPORTS.flatMap { healthy(context, it) }.distinct()

    fun count(context: Context, name: String): Int = healthy(context, name).size

    fun countAll(context: Context): Int = allHealthy(context).size

    /**
     * Add fingerprints proven by [name] to its pool, keeping the most recent
     * [MAX_PER_TRANSPORT] entries. Returns how many were new.
     */
    fun remember(context: Context, name: String, fingerprints: Collection<String>): Int {
        val valid = fingerprints.map { it.trim() }.filter { isFingerprint(it) }.distinct()
        if (valid.isEmpty()) return 0
        val current = healthy(context, name)
        val newOnes = valid.filter { it !in current }
        if (newOnes.isEmpty()) return 0
        val merged = (newOnes + current).take(MAX_PER_TRANSPORT)
        prefs(context).edit().putString(key(name), merged.joinToString(",")).apply()
        Log.i(TAG, "memory[$name]: +${newOnes.size} healthy bridge(s), pool=${merged.size}")
        return newOnes.size
    }

    fun clear(context: Context) {
        TRANSPORTS.forEach { prefs(context).edit().remove(key(it)).apply() }
        Log.i(TAG, "memory cleared")
    }

    /**
     * Bridge lines for the memory runner: the full lists filtered down to the
     * fingerprints that worked before, remembered bridges first. Returns null
     * when nothing has been proven yet, so the runner is skipped entirely
     * instead of racing with an empty bridge set.
     */
    fun bridgeLinesFor(context: Context, sources: Map<String, String>): String? {
        val remembered = allHealthy(context)
        if (remembered.isEmpty()) return null
        val order = remembered.withIndex().associate { (i, fp) -> fp to i }
        val kept = mutableListOf<Pair<Int, String>>()
        sources.forEach { (_, content) ->
            content.lines().forEach { raw ->
                val line = raw.trim()
                if (line.isEmpty() || line.startsWith("#")) return@forEach
                val fp = fingerprintOf(line) ?: return@forEach
                val rank = order[fp] ?: return@forEach
                kept += rank to line
            }
        }
        if (kept.isEmpty()) return null
        return kept.sortedBy { it.first }.map { it.second }.joinToString("\n")
    }

    private fun isFingerprint(s: String) = s.length in 32..40 && s.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    /**
     * The identity fingerprint of a bridge line. Prefixed lines
     * (`obfs4 <ip:port> <fp> ...`, webtunnel, snowflake) carry it as the third
     * token; plain vanilla lines (`<ip:port> <fp> ...`) as the second. Detected
     * by looking at the first token so every format is handled.
     */
    fun fingerprintOf(line: String): String? {
        val parts = line.split(WHITESPACE).filter { it.isNotEmpty() }
        if (parts.isEmpty()) return null
        val first = parts[0].lowercase()
        val idx = if (first in PLUGGABLE_PREFIXES) 2 else 1
        val candidate = parts.getOrNull(idx) ?: return null
        return candidate.takeIf { isFingerprint(it) }
    }

    private val WHITESPACE = Regex("\\s+")
    private val PLUGGABLE_PREFIXES = setOf("obfs4", "webtunnel", "snowflake", "meek_lite", "meek")

    /** Transports that keep their own memory pool. */
    val TRANSPORTS = listOf(
        ParallelTorManager.TRANSPORT_VANILLA,
        ParallelTorManager.TRANSPORT_OBFS4,
        ParallelTorManager.TRANSPORT_WEBTUNNEL,
        ParallelTorManager.TRANSPORT_MEMORY
    )
}

package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.net.HttpURLConnection
import java.net.URL

/**
 * Hands a brand-new install a starting set of vetted bridges.
 *
 * A fresh install has no cached lists and no memory yet, so its first connects
 * grind through whatever the collector published that day. This downloads one
 * curated list once, saves it as the combined transport's cache so combined
 * mode merges it in at connect time, and seeds the combined memory pool so the
 * combined-memory twin starts warm instead of empty.
 *
 * Only for new users, not updaters: [DeltaTorApp] gates it on the first-run
 * flag, which an upgrading install has long since set.
 */
object CombinedMemorySeed {
    private const val TAG = "CombinedMemorySeed"
    private const val SEED_URL =
        "https://github.com/Delta-Kronecker/Delta-Tor/raw/refs/heads/test/Android/CombinedMemory.txt"

    fun seedIfFresh(context: Context) {
        try {
            val conn = URL(SEED_URL).openConnection() as HttpURLConnection
            conn.connectTimeout = 15_000
            conn.readTimeout = 15_000
            conn.instanceFollowRedirects = true
            conn.setRequestProperty("User-Agent", "DeltaTor-Android")
            val code = conn.responseCode
            if (code !in 200..399) {
                Log.w(TAG, "seed HTTP $code")
                return
            }
            val body = conn.inputStream.bufferedReader().use { it.readText() }
            conn.disconnect()
            val lines = body.lines().map { it.trim() }
                .filter { it.isNotEmpty() && !it.startsWith("#") }
            if (lines.isEmpty()) {
                Log.w(TAG, "seed file empty")
                return
            }
            // Persist so combined mode merges these in at connect time.
            BridgeStore.saveLines(
                context,
                ParallelTorManager.TRANSPORT_COMBINED,
                lines.joinToString("\n")
            )
            // Seed the combined memory pool so the combined-memory twin starts warm.
            val fingerprints = lines.mapNotNull { BridgeMemory.fingerprintOf(it) }
            BridgeMemory.remember(context, ParallelTorManager.TRANSPORT_COMBINED, fingerprints)
            BridgeStore.refreshMemory(context)
            Log.i(TAG, "seeded ${lines.size} combined bridge(s), ${fingerprints.size} fingerprint(s)")
        } catch (e: Exception) {
            Log.w(TAG, "seed failed: ${e.message}")
        }
    }
}

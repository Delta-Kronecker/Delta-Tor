package io.deltator

import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ConcurrentHashMap

/**
 * One GET for one statistic, retried only when the previous attempt really
 * failed.
 *
 * The install and connect numbers are GitHub release-asset download counters,
 * so the request *is* the event: every request that reaches the asset bumps the
 * number by one. That makes duplicates expensive and retries safe only when
 * the last request genuinely did not complete, which is exactly what this
 * helper enforces:
 *
 *  - attempts run in sequence, never in parallel, and one asset is never
 *    requested twice at the same time (a second caller waits for the first);
 *  - the next attempt happens only after a timeout, a network error, or a
 *    non-success HTTP status -- a 200 ends the loop immediately;
 *  - the body is read into a throwaway buffer and discarded. Nothing is
 *    written to disk, so there is no file to save, clean up, or leak;
 *  - the total number of attempts is bounded, so a machine that is offline
 *    does not spin.
 */
internal object StatBeacon {
    private const val TAG = "StatBeacon"

    /** One try plus three retries. Failures do not count as downloads. */
    private const val MAX_ATTEMPTS = 4
    private const val CONNECT_TIMEOUT_MS = 10_000
    private const val READ_TIMEOUT_MS = 15_000

    private val gates = ConcurrentHashMap<String, Mutex>()

    /** One gate per asset: two callers can never request the same asset at once. */
    private fun gate(name: String): Mutex = synchronized(gates) {
        gates.getOrPut(name) { Mutex() }
    }

    /**
     * Requests [url] until it succeeds or the attempts run out. Returns true
     * only when the asset was actually served.
     */
    suspend fun hit(name: String, url: String, userAgent: String): Boolean =
        withContext(Dispatchers.IO) {
            val mutex = gate(name)
            mutex.withLock {
                for (attempt in 1..MAX_ATTEMPTS) {
                    if (attempt > 1) delay(backoffMs(attempt))
                    if (tryOnce(name, url, userAgent)) return@withLock true
                }
                Log.w(TAG, "$name still not counted after $MAX_ATTEMPTS attempts")
                false
            }
        }

    /** 1s, 3s, 7s: long enough to clear a dropped link, short enough to finish. */
    private fun backoffMs(attempt: Int): Long = when (attempt) {
        2 -> 1_000L
        3 -> 3_000L
        else -> 7_000L
    }

    private fun tryOnce(name: String, url: String, userAgent: String): Boolean {
        var conn: HttpURLConnection? = null
        return try {
            val c = (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = CONNECT_TIMEOUT_MS
                readTimeout = READ_TIMEOUT_MS
                requestMethod = "GET"
                instanceFollowRedirects = true
                useCaches = false
                setRequestProperty("User-Agent", userAgent)
                setRequestProperty("Accept", "application/octet-stream")
            }
            conn = c
            val code = c.responseCode
            // Read the response out and drop it. The read is what guarantees the
            // transfer finished, and no byte of it ever reaches a file.
            val stream = if (code in 200..299) c.inputStream else c.errorStream
            stream?.use { input ->
                val buffer = ByteArray(4096)
                while (input.read(buffer) != -1) {
                    // Discard: this request is the statistic, not the content.
                }
            }
            if (code in 200..399) {
                Log.i(TAG, "$name counted (HTTP $code)")
                true
            } else {
                Log.w(TAG, "$name attempt got HTTP $code")
                false
            }
        } catch (e: Exception) {
            Log.w(TAG, "$name attempt failed: ${e.message}")
            false
        } finally {
            conn?.disconnect()
        }
    }
}
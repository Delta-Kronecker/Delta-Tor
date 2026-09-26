package io.deltator.tunnel

import android.content.Context
import io.deltator.AppState
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * Caches the three bridge lists (vanilla / obfs4 / webtunnel) on disk and keeps
 * them fresh. Lists are downloaded from the Tor-Bridges-Collector repository; a
 * download atomically replaces the cached files. Auto-update runs when no cache
 * exists yet or more than a day has passed since the last update.
 */
object BridgeStore {
    private const val TAG = "BridgeStore"
    private const val UPDATE_INTERVAL_MS = 24L * 60 * 60 * 1000
    private const val CONNECT_TIMEOUT_MS = 20_000
    private const val READ_TIMEOUT_MS = 20_000

    /**
     * Bumped when the shape of the sources changed (webtunnel became two merged
     * files), so an upgrade refreshes the cache once instead of keeping a list
     * written by the old code.
     */
    private const val KEY_LAST_UPDATE = "bridges_last_update_v2"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var updateInProgress = false

    private fun prefs(context: Context): android.content.SharedPreferences =
        context.getSharedPreferences("deltator", Context.MODE_PRIVATE)

    private fun sources(): Map<String, List<String>> = ParallelTorManager.BRIDGE_SOURCES

    fun dir(context: Context): File = File(context.filesDir, "bridges")

    fun file(context: Context, name: String): File = File(dir(context), "$name.txt")

    /** Cached bridge lines, or null when not cached yet (or cached empty). */
    fun lines(context: Context, name: String): String? {
        val f = file(context, name)
        if (!f.exists()) return null
        val text = f.readText().trim()
        return text.ifEmpty { null }
    }

    /** Persist bridge lines (blank content is ignored). */
    fun saveLines(context: Context, name: String, content: String) {
        if (content.isBlank()) return
        val target = file(context, name)
        target.parentFile?.mkdirs()
        val tmp = File(target.parentFile, "$name.txt.tmp")
        tmp.writeText(content)
        if (!tmp.renameTo(target)) {
            target.writeText(content)
            tmp.delete()
        }
    }

    private fun countLines(content: String): Int =
        content.lineSequence().count { it.isNotBlank() && !it.trimStart().startsWith("#") }

    fun stats(context: Context): Map<String, Int> =
        sources().keys.associateWith { name ->
            lines(context, name)?.let { countLines(it) } ?: 0
        }

    fun lastUpdatedMillis(context: Context): Long =
        prefs(context).getLong(KEY_LAST_UPDATE, 0L)

    /** True when no bridge cache exists yet or it is older than a day. */
    fun shouldAutoUpdate(context: Context): Boolean {
        if (stats(context).values.any { it <= 0 }) return true
        val last = lastUpdatedMillis(context)
        return last == 0L || System.currentTimeMillis() - last >= UPDATE_INTERVAL_MS
    }

    /**
     * Download every bridge file and atomically replace the cached lists. A
     * transport backed by several files (webtunnel) gets the merged result.
     */
    private fun updateInternal(context: Context): Map<String, Int> {
        var ok = 0
        sources().forEach { (name, urls) ->
            val bodies = urls.mapNotNull { url ->
                val body = downloadText(url)
                if (body.isNotBlank()) {
                    body
                } else {
                    Log.w(TAG, "Empty download for $url")
                    null
                }
            }
            if (bodies.isEmpty()) return@forEach
            val merged = ParallelTorManager.mergeBridgeLists(bodies)
            if (merged.isNotBlank()) {
                saveLines(context, name, merged)
                ok++
            }
        }
        if (ok > 0) {
            prefs(context).edit().putLong(KEY_LAST_UPDATE, System.currentTimeMillis()).apply()
        }
        return stats(context)
    }

    /** Push the cached stats into [AppState] for the UI. */
    fun refreshState(context: Context) {
        AppState.updateBridge {
            it.copy(
                updating = false,
                lastUpdateMillis = lastUpdatedMillis(context),
                vanilla = stats(context)[ParallelTorManager.TRANSPORT_VANILLA] ?: 0,
                obfs4 = stats(context)[ParallelTorManager.TRANSPORT_OBFS4] ?: 0,
                webtunnel = stats(context)[ParallelTorManager.TRANSPORT_WEBTUNNEL] ?: 0,
                error = null
            )
        }
    }

    /** Kick off a background bridge update; the UI observes progress via [AppState]. */
    fun update(context: Context) {
        if (updateInProgress) return
        updateInProgress = true
        AppState.updateBridge { it.copy(updating = true, error = null) }
        scope.launch {
            val failure = try {
                updateInternal(context)
                null
            } catch (e: Exception) {
                Log.e(TAG, "Bridge update failed", e)
                e.message ?: "Update failed"
            }
            updateInProgress = false
            refreshState(context)
            if (failure != null) {
                AppState.updateBridge { it.copy(error = failure) }
            }
        }
    }

    /** Auto-update once a day (or when no cache exists); best-effort, never throws. */
    fun autoUpdateIfStale(context: Context) {
        try {
            if (shouldAutoUpdate(context)) {
                Log.i(TAG, "Bridges are stale; auto-updating")
                update(context)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Auto-update check failed", e)
        }
    }

    private fun downloadText(url: String): String {
        Log.i(TAG, "Downloading $url")
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = CONNECT_TIMEOUT_MS
        conn.readTimeout = READ_TIMEOUT_MS
        conn.setRequestProperty("User-Agent", "DeltaTor/1.0")
        return try {
            if (conn.responseCode == HttpURLConnection.HTTP_OK) {
                BufferedReader(InputStreamReader(conn.inputStream)).use { it.readText() }
            } else {
                Log.e(TAG, "HTTP ${conn.responseCode} for $url")
                ""
            }
        } finally {
            conn.disconnect()
        }
    }
}
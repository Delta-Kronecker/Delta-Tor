package io.deltator

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL

/**
 * Counts installs by downloading a one byte asset from a dedicated release.
 *
 * GitHub increments the download counter of a release asset on every request, so
 * a single successful fetch per installation is enough to make that number the
 * install count. The asset is never stored and never parsed, only the counter
 * matters.
 *
 * The flag is written only after a successful response, so a launch without
 * connectivity does not consume the one attempt: the next launch tries again and
 * a failed request never inflates the counter either.
 */
object InstallCounter {
    private const val TAG = "InstallCounter"
    private const val PREFS = "deltator_install"
    private const val KEY_DONE = "counted_v1"
    private const val KEY_ATTEMPTS = "attempts_v1"
    private const val MAX_ATTEMPTS = 5
    private const val ASSET_URL =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
            "ForInstallationStatistics/DeltaTorAndroid"

    suspend fun countInstall(context: Context) = withContext(Dispatchers.IO) {
        val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (prefs.getBoolean(KEY_DONE, false)) return@withContext
        val attempts = prefs.getInt(KEY_ATTEMPTS, 0)
        if (attempts >= MAX_ATTEMPTS) return@withContext
        prefs.edit().putInt(KEY_ATTEMPTS, attempts + 1).apply()

        try {
            val conn = URL(ASSET_URL).openConnection() as HttpURLConnection
            conn.connectTimeout = 8000
            conn.readTimeout = 8000
            conn.requestMethod = "GET"
            conn.instanceFollowRedirects = true
            conn.setRequestProperty("User-Agent", "DeltaTor-Android")
            val code = conn.responseCode
            conn.inputStream.use { it.readBytes() }
            conn.disconnect()
            if (code in 200..399) {
                prefs.edit().putBoolean(KEY_DONE, true).apply()
                Log.i(TAG, "Install counted (HTTP $code)")
            } else {
                Log.w(TAG, "Install count HTTP $code")
            }
        } catch (e: Exception) {
            Log.w(TAG, "Install count failed: ${e.message}")
        }
    }
}

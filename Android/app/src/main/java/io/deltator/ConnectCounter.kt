package io.deltator

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.net.HttpURLConnection
import java.net.URL

/**
 * Counts connect attempts by downloading a one byte asset from a dedicated
 * release every time the connect button is pressed.
 *
 * GitHub increments the download counter of a release asset on every request,
 * so the asset's download count is the number of connect attempts. The asset is
 * read into memory and discarded immediately -- never written to disk -- so
 * there is no file to clean up and no storage is consumed.
 *
 * Fire-and-forget: called from the connect button's click handler without
 * blocking it, so a slow or failed request never delays the actual connection.
 */
object ConnectCounter {
    private const val TAG = "ConnectCounter"
    private const val ASSET_URL =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
            "connected/connected"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun countConnect(context: Context) {
        scope.launch {
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
                    Log.i(TAG, "Connect counted (HTTP $code)")
                } else {
                    Log.w(TAG, "Connect count HTTP $code")
                }
            } catch (e: Exception) {
                Log.w(TAG, "Connect count failed: ${e.message}")
            }
        }
    }
}

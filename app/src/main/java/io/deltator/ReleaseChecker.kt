package io.deltator

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.app.NotificationCompat
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * Checks GitHub for a newer DeltaTor release, raises an in-app banner and a
 * system notification whenever one exists, and opens the release page in a browser.
 */
object ReleaseChecker {
    private const val TAG = "ReleaseChecker"
    private const val REPO = "Delta-Kronecker/Delta-Tor"
    private const val API_LATEST = "https://api.github.com/repos/$REPO/releases/latest"
    private const val GITHUB_URL = "https://github.com/$REPO"

    fun openInBrowser(context: Context, url: String) {
        try {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url.ifBlank { GITHUB_URL }))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(intent)
        } catch (e: Exception) {
            Log.w(TAG, "Could not open browser: ${e.message}")
        }
    }

    suspend fun check(context: Context): Boolean = withContext(Dispatchers.IO) {
        try {
            val conn = URL(API_LATEST).openConnection() as HttpURLConnection
            conn.connectTimeout = 8000
            conn.readTimeout = 8000
            conn.requestMethod = "GET"
            conn.setRequestProperty("User-Agent", "DeltaTor-Android")
            val code = conn.responseCode
            if (code != 200) {
                Log.w(TAG, "Update check HTTP $code")
                AppState.updateRelease { it.copy(checking = false) }
                return@withContext false
            }
            val body = conn.inputStream.bufferedReader().use { it.readText() }
            val json = JSONObject(body)
            val tag = json.optString("tag_name").trim().removePrefix("v")
            val htmlUrl = json.optString("html_url").ifBlank { "$GITHUB_URL/releases/tag/$tag" }

            val current = BuildConfig.VERSION_NAME.trim().removePrefix("v")
            val newer = tag.isNotEmpty() && compareVersions(current, tag) < 0
            Log.i(TAG, "Latest release: $tag (current $current, newer=$newer)")

            AppState.updateRelease {
                it.copy(checking = false, latestVersion = tag, latestUrl = htmlUrl, newer = newer)
            }
            if (newer) notify(context, tag, htmlUrl)
            true
        } catch (e: Exception) {
            Log.w(TAG, "Update check failed: ${e.message}")
            AppState.updateRelease { it.copy(checking = false) }
            false
        }
    }

    private fun notify(context: Context, version: String, url: String) {
        // System notification and in-app banner are raised on every app launch
        // while a newer release exists; there is no dismiss state.
        val open = PendingIntent.getActivity(
            context,
            40,
            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notification = NotificationCompat.Builder(context, TorVpnService.CHANNEL_UPDATES)
            .setSmallIcon(R.drawable.ic_tor)
            .setContentTitle("DeltaTor $version available")
            .setContentText("A new release is out \u2014 tap to open it on GitHub.")
            .setStyle(NotificationCompat.BigTextStyle())
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .build()
        try {
            context.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_UPDATE_ID, notification)
        } catch (e: Exception) {
            Log.w(TAG, "Update notification failed: ${e.message}")
        }
    }

    private fun compareVersions(a: String, b: String): Int {
        val pa = a.split(".").mapNotNull { it.toIntOrNull() }
        val pb = b.split(".").mapNotNull { it.toIntOrNull() }
        for (i in 0 until maxOf(pa.size, pb.size)) {
            val x = pa.getOrElse(i) { 0 }
            val y = pb.getOrElse(i) { 0 }
            if (x != y) return x - y
        }
        return 0
    }

    private const val NOTIFICATION_UPDATE_ID = 42
}
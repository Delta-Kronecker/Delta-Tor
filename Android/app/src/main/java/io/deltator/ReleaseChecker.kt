package io.deltator

import android.content.Context
import android.content.Intent
import android.net.Uri
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * Looks for a newer DeltaTor release on GitHub and offers a link to it.
 *
 * DeltaTor deliberately cannot download or install its own updates. Doing that
 * needs android.permission.REQUEST_INSTALL_PACKAGES, and Google names "apps
 * that self update using any method other than Google Play's update mechanism"
 * as the abuse that permission exists to prevent. A sideloaded app that
 * fetches an APK and runs the installer matches the dropper/self-updater
 * pattern Play Protect scans for, and holds a permission whose only sanctioned
 * uses are browsing, messaging attachments, file transfer and device
 * management -- none of which is "a VPN app updating itself".
 *
 * The check therefore stops at a URL. The user's browser downloads the APK
 * (browsing is a permitted use of the permission, and the browser is a separate,
 * visible source), and Android's own installer installs it. This app starts no
 * download, creates no install session, and requests no install permission.
 */
object ReleaseChecker {
    private const val TAG = "ReleaseChecker"
    private const val REPO = "Delta-Kronecker/Delta-Tor"
    private const val API_LATEST = "https://api.github.com/repos/$REPO/releases/latest"
    private const val GITHUB_URL = "https://github.com/$REPO"

    /** The APK that fits this device, or the release page when none matched. */
    private var latestUrl = ""

    suspend fun check(): Boolean = withContext(Dispatchers.IO) {
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
            val json = JSONObject(conn.inputStream.bufferedReader().use { it.readText() })
            val tag = json.optString("tag_name").trim().removePrefix("v")
            val page = json.optString("html_url").ifBlank { "$GITHUB_URL/releases/tag/$tag" }
            val asset = pickAsset(json, tag)

            val current = BuildConfig.VERSION_NAME.trim().removePrefix("v")
            val newer = tag.isNotEmpty() && compareVersions(current, tag) < 0
            latestUrl = if (newer) asset ?: page else ""
            Log.i(TAG, "Latest release: $tag (current $current, newer=$newer)")

            AppState.updateRelease {
                it.copy(
                    checking = false,
                    latestVersion = tag,
                    latestUrl = latestUrl,
                    newer = newer
                )
            }
            true
        } catch (e: Exception) {
            // A failed check is not the user's problem: they still have a
            // working app, and the banner simply does not appear.
            Log.w(TAG, "Update check failed: ${e.message}")
            AppState.updateRelease { it.copy(checking = false) }
            false
        }
    }

    /**
     * Opens the release in whatever the user browses with. Nothing is
     * downloaded here; the browser owns the transfer, and Android's installer
     * owns the install.
     */
    fun openRelease(context: Context, url: String) {
        val target = url.ifBlank { latestUrl.ifBlank { GITHUB_URL } }
        try {
            context.startActivity(
                Intent(Intent.ACTION_VIEW, Uri.parse(target))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        } catch (e: Exception) {
            Log.w(TAG, "Could not open $target: ${e.message}")
        }
    }

    /** The APK for the first ABI this device supports, universal as fallback. */
    private fun pickAsset(json: JSONObject, tag: String): String? {
        val assets = json.optJSONArray("assets") ?: return null
        val apks = mutableMapOf<String, String>()
        for (i in 0 until assets.length()) {
            val asset = assets.optJSONObject(i) ?: continue
            val name = asset.optString("name")
            if (!name.endsWith(".apk")) continue
            apks[name] = asset.optString("browser_download_url")
        }
        if (apks.isEmpty()) return null
        for (abi in android.os.Build.SUPPORTED_ABIS) {
            apks["deltator-$tag-$abi.apk"]?.let { return it }
        }
        apks["deltator-$tag-universal.apk"]?.let { return it }
        return apks.values.first()
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
}
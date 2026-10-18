package io.deltator

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.app.NotificationCompat
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * Checks GitHub for a newer DeltaTor release and takes the update the rest of
 * the way instead of only pointing at the release page: the APK matching this
 * device's ABI is downloaded into the private updates folder while the in-app
 * banner shows the progress, and the finished file is handed to the system
 * installer -- on the tap after the download, and by itself on every later
 * launch while the file is still there.
 */
object ReleaseChecker {
    private const val TAG = "ReleaseChecker"
    private const val REPO = "Delta-Kronecker/Delta-Tor"
    private const val API_LATEST = "https://api.github.com/repos/$REPO/releases/latest"
    private const val GITHUB_URL = "https://github.com/$REPO"
    private const val UPDATES_DIR = "updates"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var downloadJob: Job? = null
    private var apkUrl = ""
    private var apkSize = 0L
    private var apkFile: File? = null
    private var apkTag = ""

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
            val (name, url, size) = pickAsset(json, tag) ?: Triple("", "", 0L)

            val current = BuildConfig.VERSION_NAME.trim().removePrefix("v")
            val newer = tag.isNotEmpty() && name.isNotEmpty() && compareVersions(current, tag) < 0
            Log.i(TAG, "Latest release: $tag (current $current, newer=$newer, apk=$name)")

            if (!newer) {
                AppState.updateRelease {
                    it.copy(
                        checking = false,
                        latestVersion = tag,
                        latestUrl = htmlUrl,
                        newer = false,
                        phase = "idle",
                        bytes = 0L,
                        totalBytes = 0L,
                        autoPrompt = false,
                        lastError = ""
                    )
                }
                // Whatever an earlier run downloaded is either installed now or
                // superseded; nothing else ever reads this folder.
                pruneUpdates(context, keep = null)
                return@withContext true
            }

            apkUrl = url
            apkSize = size
            apkTag = tag
            val dir = File(context.filesDir, UPDATES_DIR)
            dir.mkdirs()
            val file = File(dir, name)
            apkFile = file
            // A partial file from a run that was killed mid-download is never
            // installable, and the download below overwrites it anyway.
            File(dir, "$name.part").delete()

            if (file.exists() && (size <= 0L || file.length() == size)) {
                // Downloaded in an earlier run: straight to the install dialog.
                AppState.updateRelease {
                    it.copy(
                        checking = false,
                        latestVersion = tag,
                        latestUrl = htmlUrl,
                        newer = true,
                        phase = "ready",
                        bytes = file.length(),
                        totalBytes = file.length(),
                        autoPrompt = true,
                        lastError = ""
                    )
                }
                notifyReady(context, tag)
                Log.i(TAG, "Update apk already on disk: ${file.name}")
                return@withContext true
            }

            AppState.updateRelease {
                it.copy(
                    checking = false,
                    latestVersion = tag,
                    latestUrl = htmlUrl,
                    newer = true,
                    phase = "downloading",
                    bytes = 0L,
                    totalBytes = size,
                    autoPrompt = false,
                    lastError = ""
                )
            }
            startDownload(context)
            true
        } catch (e: Exception) {
            Log.w(TAG, "Update check failed: ${e.message}")
            AppState.updateRelease { it.copy(checking = false) }
            false
        }
    }

    /** The APK for the first ABI this device supports, universal as fallback. */
    private fun pickAsset(json: JSONObject, tag: String): Triple<String, String, Long>? {
        val assets = json.optJSONArray("assets") ?: return null
        val apks = mutableMapOf<String, Pair<String, Long>>()
        for (i in 0 until assets.length()) {
            val asset = assets.optJSONObject(i) ?: continue
            val name = asset.optString("name")
            if (!name.endsWith(".apk")) continue
            apks[name] = asset.optString("browser_download_url") to asset.optLong("size")
        }
        for (abi in Build.SUPPORTED_ABIS) {
            apks["deltator-$tag-$abi.apk"]?.let { (u, s) -> return Triple("deltator-$tag-$abi.apk", u, s) }
        }
        apks["deltator-$tag-universal.apk"]?.let { (u, s) -> return Triple("deltator-$tag-universal.apk", u, s) }
        val any = apks.entries.firstOrNull() ?: return null
        return Triple(any.key, any.value.first, any.value.second)
    }

    /** Called from the banner when a download failed, or when one never started. */
    fun retryDownload(context: Context) {
        if (apkUrl.isNotEmpty()) startDownload(context)
    }

    /** Called from the banner and the notification once the file is complete. */
    fun installLatest(context: Context) {
        val file = apkFile ?: return
        if (!file.exists() || file.length() == 0L) {
            val msg = "The downloaded file is gone \u00b7 tap to download it again."
            AppState.updateRelease { it.copy(phase = "error", lastError = msg) }
            notifyError(context, apkTag, msg)
            return
        }
        // PackageInstaller would raise its own "install unknown apps" dialog
        // at commit() time, which is jarring and easy to dismiss by accident.
        // Catch it one step earlier instead: go straight to the per-app switch
        // in Settings and tell the user what to do, so the only thing standing
        // between them and the install is one toggle they can see.
        if (!canInstall(context)) {
            AppState.updateRelease {
                it.copy(lastError = "Enable \u201cAllow from this source\u201d for DeltaTor in Settings, then tap to install.")
            }
            openUnknownSources(context)
            return
        }
        // PackageInstaller is the same session API adb install and the Play
        // Store use: it talks straight to the system service, so it does not
        // depend on any activity being registered to handle an ACTION_VIEW
        // intent, and it is the system itself that raises the install
        // confirmation -- this code no longer has to guess at that.
        try {
            val installer = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            val sessionId = installer.createSession(params)
            installer.openSession(sessionId).use { session ->
                session.openWrite("DeltaTor", 0, file.length()).use { out ->
                    file.inputStream().use { input -> input.copyTo(out) }
                    session.fsync(out)
                }
                val intent = Intent(context, InstallResultReceiver::class.java)
                val pendingIntent = PendingIntent.getBroadcast(
                    context,
                    sessionId,
                    intent,
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
                )
                session.commit(pendingIntent.intentSender)
            }
            AppState.updateRelease { it.copy(lastError = "") }
            Log.i(TAG, "Install session $sessionId committed")
        } catch (e: Exception) {
            Log.w(TAG, "Install session failed: ${e.message}")
            val msg = "Could not start the installer: ${e.message}"
            AppState.updateRelease { it.copy(lastError = msg) }
            notifyError(context, apkTag, msg)
        }
    }

    /** Whether the system will let this app hand an APK to the installer at all. */
    private fun canInstall(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
            context.packageManager.canRequestPackageInstalls()

    /** This package's own "Install unknown apps" switch in Settings. */
    private fun openUnknownSources(context: Context) {
        try {
            context.startActivity(
                Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:${context.packageName}")
                ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        } catch (e: Exception) {
            Log.w(TAG, "Could not open unknown-sources settings: ${e.message}")
        }
    }

    private fun startDownload(context: Context) {
        if (downloadJob?.isActive == true) return
        val url = apkUrl
        val file = apkFile
        if (url.isEmpty() || file == null) return

        downloadJob = scope.launch {
            val part = File(file.parentFile, "${file.name}.part")
            try {
                val conn = URL(url).openConnection() as HttpURLConnection
                conn.connectTimeout = 15_000
                conn.readTimeout = 30_000
                conn.connect()
                if (conn.responseCode != 200) throw IOException("HTTP ${conn.responseCode}")
                val total = if (apkSize > 0L) apkSize else conn.contentLengthLong
                part.parentFile?.mkdirs()
                var done = 0L
                var lastPost = 0L
                conn.inputStream.use { input ->
                    part.outputStream().use { out ->
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            val now = System.currentTimeMillis()
                            if (now - lastPost >= 250) {
                                lastPost = now
                                publishProgress(context, done, total)
                            }
                        }
                        out.flush()
                    }
                }
                if (total > 0L && part.length() != total) {
                    throw IOException("got ${part.length()} of $total bytes")
                }
                file.delete()
                if (!part.renameTo(file)) throw IOException("could not move download into place")

                publishProgress(context, file.length(), file.length())
                AppState.updateRelease { it.copy(phase = "ready", autoPrompt = false) }
                notifyReady(context, apkTag)
                pruneUpdates(context, keep = file)
                Log.i(TAG, "Update apk downloaded: ${file.name} (${file.length()} bytes)")
            } catch (e: Exception) {
                part.delete()
                Log.w(TAG, "Update download failed: ${e.message}")
                val msg = "Download failed \u00b7 open the app to retry."
                AppState.updateRelease { it.copy(phase = "error", lastError = msg) }
                notifyError(context, apkTag, msg)
            }
        }
    }

    private fun publishProgress(context: Context, bytes: Long, total: Long) {
        AppState.updateRelease { it.copy(bytes = bytes, totalBytes = total) }
        val pct = if (total > 0L) (bytes * 100 / total).toInt().coerceIn(0, 100) else -1
        post(
            context,
            NotificationCompat.Builder(context, TorVpnService.CHANNEL_UPDATES)
                .setSmallIcon(R.drawable.ic_tor)
                .setContentTitle("DeltaTor $apkTag downloading")
                .setContentText(if (pct >= 0) "$pct% downloaded" else "Downloading update\u2026")
                .setProgress(if (total > 0L) 100 else 0, pct.coerceAtLeast(0), total <= 0L)
                .setContentIntent(openAppIntent(context))
                .setOnlyAlertOnce(true)
                .setOngoing(true)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .setCategory(NotificationCompat.CATEGORY_PROGRESS)
        )
    }

    private fun notifyReady(context: Context, version: String) {
        // The tap starts a PackageInstaller session directly, so it does not
        // need an activity to "handle" an install intent -- that resolution is
        // what failed outright on stripped-down images with no package
        // installer app registered for ACTION_VIEW.
        val tap = PendingIntent.getBroadcast(
            context,
            41,
            Intent(context, InstallTriggerReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        post(
            context,
            NotificationCompat.Builder(context, TorVpnService.CHANNEL_UPDATES)
                .setSmallIcon(R.drawable.ic_tor)
                .setContentTitle("DeltaTor $version ready")
                .setContentText("Tap to install the new version.")
                .setContentIntent(tap)
                .setAutoCancel(true)
                .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
                .setPriority(NotificationCompat.PRIORITY_DEFAULT)
        )
    }

    private fun notifyError(context: Context, version: String, text: String) {
        post(
            context,
            NotificationCompat.Builder(context, TorVpnService.CHANNEL_UPDATES)
                .setSmallIcon(R.drawable.ic_tor)
                .setContentTitle("DeltaTor $version update")
                .setContentText(text)
                .setContentIntent(openAppIntent(context))
                .setAutoCancel(true)
                .setCategory(NotificationCompat.CATEGORY_ERROR)
                .setPriority(NotificationCompat.PRIORITY_DEFAULT)
        )
    }

    private fun openAppIntent(context: Context): PendingIntent = PendingIntent.getActivity(
        context,
        42,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
    )

    private fun post(context: Context, builder: NotificationCompat.Builder) {
        try {
            context.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_UPDATE_ID, builder.build())
        } catch (e: Exception) {
            Log.w(TAG, "Update notification failed: ${e.message}")
        }
    }

    private fun pruneUpdates(context: Context, keep: File?) {
        File(context.filesDir, UPDATES_DIR).listFiles()?.forEach { f ->
            if (f.absolutePath != keep?.absolutePath && f.name != "${keep?.name}.part") {
                runCatching { f.delete() }
            }
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

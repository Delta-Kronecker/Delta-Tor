package io.deltator

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Process
import androidx.core.content.FileProvider
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.system.exitProcess

/** Why a report is being offered. */
enum class ErrorKind { CONNECTION, CRASH }

/**
 * One error that the user is asked to send a log for. [message] is the failure
 * the app already showed (a refused connect, say); [crash] is the stack trace of
 * a crash from a previous run that was persisted before the process died.
 */
data class ErrorReport(val kind: ErrorKind, val message: String?, val crash: String?)

/**
 * Turns an error into a "send the log" prompt.
 *
 * The dialog itself lives in MainActivity; this object only remembers what to
 * ask about and builds the archive. Two triggers feed it:
 *
 *  - a connection failure, reported from the service the moment it gives up;
 *  - a crash. The process that crashed cannot show a dialog, so the throwable
 *    and the recent log are written to a file first and turned into a report
 *    on the next launch, when there is a screen to show it on.
 *
 * The archive is built in the cache directory. Nothing is uploaded by the app:
 * Telegram is opened with the file attached and the user sends it themselves,
 * so no bot token and no secret live in the binary.
 */
object LogReporter {
    private const val TAG = "LogReporter"
    private const val EXPORT_DIR = "log_exports"
    private const val AUTHORITY_SUFFIX = ".fileprovider"
    private const val PENDING_CRASH = "crash-pending.txt"

    /** The bot the user is asked to send the archive to. */
    const val BOT_NAME = "@DeltaKronecker_Bot"
    const val BOT_URL = "https://t.me/DeltaKronecker_Bot"

    private val _report = MutableStateFlow<ErrorReport?>(null)
    val report: StateFlow<ErrorReport?> = _report.asStateFlow()

    /** Install the crash handler. Called once from DeltaTorApp.onCreate. */
    fun init(context: Context) = installCrashHandler(context.applicationContext)

    fun reportConnection(message: String) {
        _report.value = ErrorReport(ErrorKind.CONNECTION, message, null)
    }

    /** Clear the prompt (dismissed or sent). */
    fun dismiss() {
        _report.value = null
    }

    /**
     * Turn a crash that killed the previous run into a report, if there is one.
     * Reads and deletes the persisted file so a single crash is asked about
     * once, not on every launch from then on.
     */
    fun postPendingCrash(context: Context) {
        val file = File(context.filesDir, PENDING_CRASH)
        if (!file.exists()) return
        val text = try {
            file.readText()
        } catch (e: Exception) {
            Log.w(TAG, "crash file read failed: ${e.message}")
            null
        } finally {
            file.delete()
        }
        if (text.isNullOrBlank()) return
        _report.value = ErrorReport(ErrorKind.CRASH, null, text)
    }

    /**
     * Build the archive: the recent log, a small device/app header, and the
     * crash text when the report came from a crash. Returns null when the file
     * could not be written. The previous export is cleared first so exports
     * never accumulate in the cache.
     */
    fun buildZip(context: Context, report: ErrorReport): File? {
        val dir = File(context.cacheDir, EXPORT_DIR).apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() }
        val stamp = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
        val zip = File(dir, "deltator-log-$stamp.zip")
        try {
            ZipOutputStream(zip.outputStream().buffered()).use { out ->
                out.putNextEntry(ZipEntry("connection-log.txt"))
                out.write(Log.dumpReport().toByteArray())
                out.closeEntry()

                out.putNextEntry(ZipEntry("device.txt"))
                out.write(deviceInfo(context, report).toByteArray())
                out.closeEntry()

                val crash = report.crash
                if (!crash.isNullOrBlank()) {
                    out.putNextEntry(ZipEntry("crash.txt"))
                    out.write(crash.toByteArray())
                    out.closeEntry()
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "report zip failed: ${e.message}")
            zip.delete()
            return null
        }
        return zip
    }

    /**
     * Hand the archive to Telegram. Telegram is picked directly when installed
     * so the user lands in its share screen; otherwise the system chooser is
     * offered. If neither works, the bot is opened in the browser so the user is
     * never left with a dead button.
     */
    fun openShare(context: Context, zip: File) {
        val uri = FileProvider.getUriForFile(context, context.packageName + AUTHORITY_SUFFIX, zip)
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "application/zip"
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_SUBJECT, "DeltaTor connection log")
            putExtra(Intent.EXTRA_TEXT, BOT_URL)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val telegram = TELEGRAM_PACKAGES.firstOrNull { installed(context, it) }
        val intent = if (telegram != null) {
            send.apply { setPackage(telegram) }
        } else {
            Intent.createChooser(send, "Send log")
        }
        if (context !is Activity) intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try {
            context.startActivity(intent)
        } catch (e: Exception) {
            Log.w(TAG, "Telegram share failed: ${e.message}")
            try {
                val open = Intent(Intent.ACTION_VIEW, Uri.parse(BOT_URL))
                if (context !is Activity) open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                context.startActivity(open)
            } catch (e2: Exception) {
                Log.w(TAG, "opening the bot also failed: ${e2.message}")
            }
        }
    }

    private val TELEGRAM_PACKAGES = listOf(
        "org.telegram.messenger",
        "org.telegram.messenger.web",
        "org.thunderdog.challegram",
        "nekox.messenger",
        "org.telegram.plus"
    )

    private fun installed(context: Context, pkg: String): Boolean = try {
        context.packageManager.getLaunchIntentForPackage(pkg) != null
    } catch (e: Exception) {
        false
    }

    private fun deviceInfo(context: Context, report: ErrorReport): String = buildString {
        appendLine("DeltaTor connection log")
        appendLine("time: ${SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())}")
        appendLine("app: ${appVersion(context)}")
        appendLine("android: ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
        appendLine("device: ${Build.MANUFACTURER} ${Build.MODEL}")
        appendLine("kind: ${report.kind}")
        val message = report.message
        if (!message.isNullOrBlank()) appendLine("error: $message")
        appendLine("recording: ${if (Log.enabled) "on" else "off"}")
        appendLine()
        appendLine("Send this archive to $BOT_NAME.")
    }

    private fun appVersion(context: Context): String = try {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "?"
    } catch (e: Exception) {
        "?"
    }

    /**
     * Persist a crashing run before the process dies. The log buffer is
     * in-memory, so the crash text carries a copy of the recent ring with it;
     * otherwise the very lines that explain the crash would be gone by the time
     * the next launch offers to send them.
     */
    private fun installCrashHandler(context: Context) {
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                val text = buildString {
                    appendLine("DeltaTor crash")
                    appendLine("time: ${SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())}")
                    appendLine("thread: ${thread.name}")
                    appendLine()
                    appendLine("----- stack trace -----")
                    appendLine(error.stackTraceToString())
                    appendLine("----- recent log -----")
                    append(Log.dumpReport())
                }
                File(context.filesDir, PENDING_CRASH).writeText(text)
            } catch (e: Exception) {
                // The process is already going; a failed write must not change
                // how it ends.
            }
            if (previous != null) {
                previous.uncaughtException(thread, error)
            } else {
                Process.killProcess(Process.myPid())
                exitProcess(10)
            }
        }
    }
}

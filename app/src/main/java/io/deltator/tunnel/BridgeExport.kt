package io.deltator.tunnel

import android.app.Activity
import android.content.Context
import android.content.Intent
import androidx.core.content.FileProvider
import io.deltator.util.AppLog as Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * Packs every bridge the app is holding into one zip and hands it to the user.
 *
 * One file per list rather than a single blob, because the point of a backup is
 * being usable somewhere else: every txt file under bridges/ is a plain Tor bridge file
 * that can be dropped straight into Tor Browser. The remembered bridges
 * (the txt files under memory/) are exported as bridge lines too, not as fingerprints, so the
 * proven set is portable and not just a list of hashes that mean nothing outside
 * this app.
 *
 * The archive is rebuilt in the cache directory and the previous export is
 * cleared, so an export can never accumulate across shares.
 */
object BridgeExport {
    private const val TAG = "BridgeExport"
    private const val EXPORT_DIR = "exports"
    private const val AUTHORITY_SUFFIX = ".fileprovider"

    /** Written as `bridges/<file>`, in this order. */
    private val LISTS = listOf(
        ParallelTorManager.TRANSPORT_VANILLA to "vanilla.txt",
        ParallelTorManager.TRANSPORT_OBFS4 to "obfs4.txt",
        ParallelTorManager.TRANSPORT_WEBTUNNEL to "webtunnel.txt",
        ParallelTorManager.TRANSPORT_SNOWFLAKE to "snowflake.txt",
        ParallelTorManager.TRANSPORT_FRESH to "fresh.txt",
        ParallelTorManager.TRANSPORT_COMBINED to "combined.txt"
    )

    /**
     * Written as `memory/<file>`. `auto-mixed.txt` is the pool the auto mode
     * races; the rest are the per-mode twins.
     */
    private val MEMORY = listOf(
        ParallelTorManager.TRANSPORT_VANILLA to "vanilla.txt",
        ParallelTorManager.TRANSPORT_OBFS4 to "obfs4.txt",
        ParallelTorManager.TRANSPORT_WEBTUNNEL to "webtunnel.txt",
        ParallelTorManager.TRANSPORT_SNOWFLAKE to "snowflake.txt",
        ParallelTorManager.TRANSPORT_FRESH to "fresh.txt",
        ParallelTorManager.TRANSPORT_COMBINED to "combined.txt",
        ParallelTorManager.TRANSPORT_MEMORY to "auto-mixed.txt"
    )

    /**
     * Build the zip. Returns null when there is nothing in it: a backup of zero
     * bridges is not a backup, and offering the share sheet for an empty archive
     * only looks broken.
     */
    fun build(context: Context): File? {
        val cached = ParallelTorManager.BRIDGE_SOURCES.keys
            .mapNotNull { name -> BridgeStore.lines(context, name)?.let { name to it } }
            .toMap()
            .toMutableMap()
        // Combined keeps no cache of its own, so it is rebuilt here for the same
        // reason it is rebuilt on connect: its value is that it can be merged.
        val combined = ParallelTorManager.mergeBridgeLists(
            ParallelTorManager.COMBINED_SOURCES.mapNotNull { cached[it] }
        ).lines().filter { it.isNotBlank() }
        if (combined.isNotEmpty()) cached[ParallelTorManager.TRANSPORT_COMBINED] = combined.joinToString("\n")

        val written = mutableListOf<String>()
        val dir = File(context.cacheDir, EXPORT_DIR).apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() }
        val stamp = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
        val zip = File(dir, "deltator-bridges-$stamp.zip")

        ZipOutputStream(zip.outputStream().buffered()).use { out ->
            var total = 0
            LISTS.forEach { (name, file) ->
                val body = cached[name]?.lines()?.filter { it.isNotBlank() }
                if (body.isNullOrEmpty()) return@forEach
                out.putNextEntry(ZipEntry("bridges/$file"))
                out.write(body.joinToString("\n").toByteArray())
                out.closeEntry()
                written += "bridges/$file (${body.size})"
                if (name != ParallelTorManager.TRANSPORT_COMBINED) total += body.size
            }
            // The memory pools are stored as fingerprints only, so the lines have
            // to be looked up in the lists again. A remembered bridge whose list
            // has since been replaced is simply dropped: its fingerprint alone is
            // not something Tor could use.
            MEMORY.forEach { (name, file) ->
                val pool = BridgeMemory.count(context, name)
                if (pool == 0) return@forEach
                val body = BridgeMemory.bridgeLinesFor(context, cached, name)
                    ?.lines()?.filter { it.isNotBlank() }.orEmpty()
                if (body.isEmpty()) return@forEach
                out.putNextEntry(ZipEntry("memory/$file"))
                out.write(body.joinToString("\n").toByteArray())
                out.closeEntry()
                written += "memory/$file (${body.size} of $pool remembered)"
            }
            out.putNextEntry(ZipEntry("README.txt"))
            out.write(readme(written, total).toByteArray())
            out.closeEntry()
        }

        Log.i(TAG, "export: ${zip.name}, ${written.size} file(s)")
        return if (written.isEmpty()) {
            zip.delete()
            null
        } else {
            zip
        }
    }

    /** Build and offer the zip to the user. */
    fun share(context: Context) {
        val zip = build(context)
        if (zip == null) {
            Log.w(TAG, "nothing to export: no cached bridges and no remembered ones")
            return
        }
        val uri = FileProvider.getUriForFile(context, context.packageName + AUTHORITY_SUFFIX, zip)
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "application/zip"
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_SUBJECT, "Delta Tor bridges")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val chooser = Intent.createChooser(send, "Export bridges")
        if (context !is Activity) chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(chooser)
    }

    private fun readme(written: List<String>, total: Int): String = buildString {
        appendLine("Delta Tor bridge export")
        appendLine("Every bridge list the app is holding, one file each.")
        appendLine()
        appendLine("bridges/ -- the lists Tor races, exactly as used.")
        appendLine("memory/  -- the bridges that already worked, as bridge lines.")
        appendLine("            These are the ones a reconnect starts from.")
        appendLine("            A remembered bridge whose list has since changed is")
        appendLine("            left out: only its fingerprint was stored, and a")
        appendLine("            fingerprint is not something Tor can connect to.")
        appendLine()
        appendLine("Files in this archive:")
        written.forEach { appendLine("  $it") }
        appendLine()
        appendLine("Any bridges/*.txt file can be added to Tor Browser as-is.")
        appendLine("$total bridge(s) across the cached lists.")
    }
}
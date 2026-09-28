package io.deltator.tunnel

import android.content.Context
import android.util.Log
import java.io.BufferedWriter
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStreamWriter
import java.util.zip.GZIPInputStream

/**
 * Builds the Tor `geoip` and `geoip6` databases the client needs for country
 * restrictions.
 *
 * Tor can only resolve an `ExitNodes {cc}` rule when it has geoip data, and the
 * bundled Tor binary ships none, so `ExitNodes {us}` used to match nothing at all:
 * the country was silently ignored, and with `StrictNodes 1` on top of that no
 * circuit could ever be built, which is what pinned bootstrap at 50%.
 *
 * The APK already carries the raw range table the country picker is built from,
 * `assets/geoip/country.csv.gz`, as `low,high,CC` with dotted-quad or IPv6 text
 * bounds. That is exactly what Tor's own parser (`geoip_parse_entry`) wants, the
 * only difference being that for IPv4 Tor reads the two bounds as plain 32 bit
 * numbers, so those are converted here instead of downloading a second copy of the
 * world:
 *
 *     geoip   low,high,CC              (unsigned 32 bit bounds)
 *     geoip6  low,high,CC              (colon separated IPv6 text)
 *
 * Only the selected countries are written, because `ExitNodes` is the only country
 * feature the app uses; that keeps the files to a few hundred KB instead of the
 * full 31 MB table. The chosen set is stored in a leading `#` comment, which Tor
 * skips, and the files are regenerated whenever that set changes.
 */
object GeoIpFile {
    private const val TAG = "GeoIpFile"
    private const val ASSET = "geoip/country.csv.gz"
    private const val HEADER_PREFIX = "# countries:"

    /**
     * Make sure [dataDir] holds a geoip and geoip6 that cover exactly [countries].
     * Returns true when both are ready. On false the caller must not claim the exit
     * country is enforced.
     */
    fun ensure(context: Context, dataDir: File, countries: Set<String>): Boolean {
        val wanted = countries.map { it.trim().uppercase() }
            .filter { it.length == 2 }
            .distinct()
            .sorted()
        if (wanted.isEmpty()) return false
        val signature = wanted.joinToString(",")
        val v4 = File(dataDir, "geoip")
        val v6 = File(dataDir, "geoip6")
        if (v4.isFile && v4.length() > 0 && v6.isFile && v6.length() > 0 &&
            header(v4) == signature && header(v6) == signature
        ) {
            return true
        }

        return try {
            if (!dataDir.isDirectory && !dataDir.mkdirs()) {
                Log.e(TAG, "cannot create $dataDir")
                return false
            }
            val tmp4 = File(dataDir, "geoip.tmp")
            val tmp6 = File(dataDir, "geoip6.tmp")
            var kept4 = 0
            var kept6 = 0
            val keep = wanted.toHashSet()
            BufferedWriter(
                OutputStreamWriter(FileOutputStream(tmp4), Charsets.UTF_8), 1 shl 16
            ).use { out4 ->
                BufferedWriter(
                    OutputStreamWriter(FileOutputStream(tmp6), Charsets.UTF_8), 1 shl 16
                ).use { out6 ->
                    out4.write("$HEADER_PREFIX $signature\n")
                    out6.write("$HEADER_PREFIX $signature\n")
                    GZIPInputStream(context.assets.open(ASSET)).use { gz ->
                        gz.bufferedReader(Charsets.UTF_8).use { input ->
                            while (true) {
                                val line = input.readLine() ?: break
                                val parts = line.split(',')
                                if (parts.size != 3) continue
                                val cc = parts[2].trim()
                                if (cc.length != 2 || cc !in keep) continue
                                val low = parts[0].trim()
                                val high = parts[1].trim()
                                if (high < low) continue
                                if (low.contains(':')) {
                                    // IPv6: Tor parses the bounds as text.
                                    out6.write("$low,$high,$cc\n")
                                    kept6++
                                } else {
                                    val lo = toUint32(low) ?: continue
                                    val hi = toUint32(high) ?: continue
                                    out4.write("$lo,$hi,$cc\n")
                                    kept4++
                                }
                            }
                        }
                    }
                }
            }
            if (kept4 == 0) {
                tmp4.delete()
                Log.e(TAG, "no IPv4 ranges matched $signature")
                return false
            }
            // Replace in one step so a failed run can never leave a half file that a
            // later connect would happily point Tor at.
            replace(tmp4, v4)
            replace(tmp6, v6)
            Log.i(TAG, "geoip built for $signature: $kept4 IPv4 and $kept6 IPv6 ranges")
            true
        } catch (e: Exception) {
            Log.e(TAG, "geoip build failed: ${e.message}")
            File(dataDir, "geoip.tmp").delete()
            File(dataDir, "geoip6.tmp").delete()
            false
        }
    }

    /** Remove generated databases, e.g. when the user clears all countries. */
    fun discard(dataDir: File) {
        File(dataDir, "geoip").delete()
        File(dataDir, "geoip6").delete()
    }

    private fun replace(from: File, to: File) {
        if (!from.renameTo(to)) {
            from.copyTo(to, overwrite = true)
            from.delete()
        }
    }

    private fun header(file: File): String? = try {
        file.useLines { lines ->
            val first = lines.firstOrNull()?.trim() ?: return null
            if (first.startsWith(HEADER_PREFIX)) first.removePrefix(HEADER_PREFIX).trim() else null
        }
    } catch (e: Exception) {
        null
    }

    /** `1.2.3.4` to the unsigned 32 bit number Tor expects, or null when invalid. */
    private fun toUint32(dotted: String): Long? {
        val parts = dotted.split('.')
        if (parts.size != 4) return null
        var value = 0L
        for (part in parts) {
            val octet = part.toIntOrNull() ?: return null
            if (octet !in 0..255) return null
            value = (value shl 8) or octet.toLong()
        }
        return value
    }
}

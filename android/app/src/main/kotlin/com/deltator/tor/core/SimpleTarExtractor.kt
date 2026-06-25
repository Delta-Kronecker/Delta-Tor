package com.deltator.tor.core

import android.content.Context
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.util.zip.GZIPInputStream

object SimpleTarExtractor {

    fun extractTarGz(context: Context, assetName: String, destDir: File, onLog: (String) -> Unit = {}): Boolean {
        return try {
            onLog("[Extract] Opening $assetName from assets...")
            val inputStream = context.assets.open(assetName)
            onLog("[Extract] Asset opened, size: ${inputStream.available()}")

            val gzIn = GZIPInputStream(inputStream)
            onLog("[Extract] GZIP decompressed")

            var count = 0
            var bytesRead = 0L

            while (true) {
                val header = ByteArray(512)
                val headerRead = readFully(gzIn, header)
                if (headerRead < 512) break

                val fileName = String(header, 0, 100).trimEnd('\u0000')
                if (fileName.isEmpty()) break

                val sizeOctal = String(header, 124, 12).trimEnd('\u0000')
                val size = sizeOctal.toLongOrNull(8) ?: 0L

                val typeFlag = header[156].toInt() and 0xFF
                val isDirectory = typeFlag == 53 || fileName.endsWith('/')

                if (isDirectory) {
                    File(destDir, fileName).mkdirs()
                } else {
                    val outFile = File(destDir, fileName)
                    outFile.parentFile?.mkdirs()
                    FileOutputStream(outFile).use { out ->
                        var remaining = size
                        val buf = ByteArray(8192)
                        while (remaining > 0) {
                            val toRead = minOf(buf.size.toLong(), remaining).toInt()
                            val read = gzIn.read(buf, 0, toRead)
                            if (read <= 0) break
                            out.write(buf, 0, read)
                            remaining -= read
                            bytesRead += read
                        }
                    }
                }

                count++

                val skip = if (size > 0) (512 - (size % 512)) % 512 else 0
                if (skip > 0) gzIn.skip(skip.toLong())

                if (count % 5 == 0) {
                    onLog("[Extract] Extracted $count files...")
                }
            }

            gzIn.close()
            onLog("[Extract] Done! Extracted $count files total")
            true
        } catch (e: Exception) {
            onLog("[Extract] FAILED: ${e.javaClass.simpleName}: ${e.message}")
            e.stackTrace.take(3).forEach { onLog("[Extract]   at $it") }
            false
        }
    }

    private fun readFully(stream: InputStream, buf: ByteArray): Int {
        var offset = 0
        while (offset < buf.size) {
            val read = stream.read(buf, offset, buf.size - offset)
            if (read <= 0) break
            offset += read
        }
        return offset
    }
}

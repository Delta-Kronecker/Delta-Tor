package com.deltator.tor.core

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.URL

class BridgeManager(private val context: Context) {

    private val bridgesDir: File
        get() = File(context.filesDir, "bridges").also { it.mkdirs() }

    fun getSafeFilename(category: String, transport: String, ip: String): String {
        val safe = category
            .replace(" ", "_")
            .replace("&", "and")
            .replace("(", "")
            .replace(")", "")
        return "${safe}_${transport}_${ip}.txt"
    }

    fun getBridgeFile(category: String, transport: String, ip: String): File {
        return File(bridgesDir, getSafeFilename(category, transport, ip))
    }

    fun getBridgeCount(category: String, transport: String, ip: String): Int {
        val file = getBridgeFile(category, transport, ip)
        if (!file.exists()) return 0
        return file.readLines().count { it.isNotBlank() }
    }

    fun getBridgeCountForSelection(category: String, transport: String, ip: String): Int {
        var count = 0
        for (entry in Config.BRIDGE_DATA) {
            val matchCat = entry.category == category
            val matchTrans = entry.transport == transport
            val matchIp = ip == "Both" || ip == entry.ip
            if (matchCat && matchTrans && matchIp) {
                count += getBridgeCount(entry.category, entry.transport, entry.ip)
            }
        }
        return count
    }

    fun getLastModified(category: String, transport: String, ip: String): Long {
        val file = getBridgeFile(category, transport, ip)
        return if (file.exists()) file.lastModified() else 0L
    }

    fun getBridgeLines(
        category: String,
        transport: String,
        ip: String,
        limit: Int = 100,
        shuffle: Boolean = true
    ): List<String> {
        val lines = mutableListOf<String>()
        for (entry in Config.BRIDGE_DATA) {
            val matchCat = entry.category == category
            val matchTrans = entry.transport == transport
            val matchIp = ip == "Both" || ip == entry.ip
            if (matchCat && matchTrans && matchIp) {
                val file = getBridgeFile(entry.category, entry.transport, entry.ip)
                if (file.exists()) {
                    lines.addAll(file.readLines().filter { it.isNotBlank() })
                }
            }
        }
        if (shuffle) lines.shuffle()
        return lines.take(limit)
    }

    fun getCustomBridges(customText: String, limit: Int = 100, shuffle: Boolean = true): List<String> {
        val lines = customText.lines()
            .filter { it.isNotBlank() && !it.startsWith("#") }
            .map { it.trim() }
        val mutable = lines.toMutableList()
        if (shuffle) mutable.shuffle()
        return mutable.take(limit)
    }

    suspend fun downloadAllBridges(
        onProgress: (current: Int, total: Int, message: String) -> Unit = { _, _, _ -> }
    ) = coroutineScope {
        val total = Config.BRIDGE_DATA.size
        var done = 0

        Config.BRIDGE_DATA.forEach { entry ->
            launch(Dispatchers.IO) {
                downloadBridgeFile(entry)
                synchronized(this@BridgeManager) {
                    done++
                    onProgress(done, total, "Downloading bridges... ($done/$total)")
                }
            }
        }
    }

    suspend fun downloadFreshBridges(
        onProgress: (current: Int, total: Int, message: String) -> Unit = { _, _, _ -> }
    ) = coroutineScope {
        val total = Config.FRESH_DATA.size
        var done = 0

        Config.FRESH_DATA.forEach { entry ->
            launch(Dispatchers.IO) {
                downloadBridgeFile(entry)
                synchronized(this@BridgeManager) {
                    done++
                    onProgress(done, total, "Updating Fresh bridges... ($done/$total)")
                }
            }
        }
    }

    private fun downloadBridgeFile(entry: BridgeEntry, retries: Int = 4) {
        val file = getBridgeFile(entry.category, entry.transport, entry.ip)
        repeat(retries) { attempt ->
            try {
                val content = URL(entry.url).readText()
                file.writeText(content)
                return
            } catch (e: Exception) {
                if (attempt == retries - 1) return
                Thread.sleep(minOf(1000L * (1 shl attempt), 16000L))
            }
        }
    }

    fun getBuiltInBridges(transport: String): List<String> {
        val configFile = File(context.filesDir, "pt_config.json")
        if (!configFile.exists()) return emptyList()
        return try {
            val json = org.json.JSONObject(configFile.readText())
            val bridges = json.getJSONObject("bridges")
            val list = bridges.optJSONArray(transport) ?: return emptyList()
            (0 until list.length()).map { list.getString(it) }
        } catch (e: Exception) {
            emptyList()
        }
    }
}

package com.deltator.tor.core

import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import java.net.Socket

data class ScanResult(
    val transport: String,
    val host: String,
    val port: Int,
    val latency: Int?,
    val reachable: Boolean,
    val error: String? = null
)

class BridgeScanner {

    suspend fun scan(
        bridgeLines: List<String>,
        transport: String,
        workers: Int = 20,
        timeoutSeconds: Int = 5,
        onProgress: (done: Int, total: Int) -> Unit = { _, _ -> },
        onResult: (ScanResult) -> Unit = {}
    ): List<ScanResult> = coroutineScope {
        val results = mutableListOf<ScanResult>()
        val mutex = Mutex()
        var done = 0

        val semaphore = Semaphore(workers)

        val deferreds = bridgeLines.map { line ->
            async(Dispatchers.IO) {
                semaphore.acquire()
                try {
                    val result = scanOne(line, transport, timeoutSeconds)
                    mutex.withLock {
                        results.add(result)
                        done++
                        onProgress(done, bridgeLines.size)
                        onResult(result)
                    }
                } finally {
                    semaphore.release()
                }
            }
        }

        deferreds.awaitAll()
        results
    }

    private fun scanOne(line: String, transport: String, timeoutSeconds: Int): ScanResult {
        val parsed = parseBridge(line) ?: return ScanResult(
            transport = transport,
            host = "?",
            port = 0,
            latency = null,
            reachable = false,
            error = "Parse error"
        )

        return try {
            val start = System.currentTimeMillis()
            val socket = Socket()
            socket.connect(java.net.InetSocketAddress(parsed.first, parsed.second), timeoutSeconds * 1000)
            socket.close()
            val latency = (System.currentTimeMillis() - start).toInt()

            ScanResult(
                transport = transport,
                host = parsed.first,
                port = parsed.second,
                latency = latency,
                reachable = true
            )
        } catch (e: Exception) {
            ScanResult(
                transport = transport,
                host = parsed.first,
                port = parsed.second,
                latency = null,
                reachable = false,
                error = e.message?.take(30) ?: "Error"
            )
        }
    }

    private fun parseBridge(line: String): Pair<String, Int>? {
        val ipv4 = Regex("(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}):(\\d+)").find(line)
        if (ipv4 != null) return Pair(ipv4.groupValues[1], ipv4.groupValues[2].toInt())

        val ipv6 = Regex("\\[([0-9a-fA-F:]+)]:(\\d+)").find(line)
        if (ipv6 != null) return Pair(ipv6.groupValues[1], ipv6.groupValues[2].toInt())

        return null
    }
}

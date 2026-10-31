package io.deltator.tunnel

import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Pre-Flight RTT socket ping for Tor bridges.
 *
 * ParallelTorManager previously started up to 4 complete Tor daemon processes with heavy bootstrap,
 * wasting CPU and battery on unreachable bridges.
 *
 * PreFlightBridgePing rapidly tests candidate bridge TCP sockets in parallel (timeout 500-600ms),
 * measures round-trip time (RTT), and reorders candidate lines so that active, low-latency bridges
 * are placed at the top of torrc before Tor processes are spawned.
 */
object PreFlightBridgePing {
    private const val TAG = "PreFlightBridgePing"
    const val DEFAULT_TIMEOUT_MS = 600
    const val DEFAULT_MAX_CANDIDATES = 50

    private data class PingResult(
        val line: String,
        val reachable: Boolean,
        val rttMs: Long
    )

    /**
     * Extracts host and port from a Tor bridge configuration line.
     * Examples:
     * - "192.0.2.1:9001 1234567890ABCDEF..."
     * - "obfs4 192.0.2.1:443 1234567890ABCDEF cert=..."
     * - "webtunnel [2001:db8::1]:443 1234567890ABCDEF url=https://... ver=..."
     */
    fun extractEndpoint(bridgeLine: String): Pair<String, Int>? {
        val trimmed = bridgeLine.trim()
        if (trimmed.isEmpty() || trimmed.startsWith("#")) return null
        val tokens = trimmed.split("\\s+".toRegex())
        if (tokens.isEmpty()) return null

        // WebTunnel: check for url=https://... parameter
        val urlToken = tokens.firstOrNull { it.startsWith("url=", ignoreCase = true) }
        if (urlToken != null) {
            try {
                val rawUrl = urlToken.substring(4)
                val uri = java.net.URI(rawUrl)
                val host = uri.host
                if (!host.isNullOrBlank()) {
                    val port = if (uri.port > 0) uri.port else 443
                    return host to port
                }
            } catch (_: Exception) {}
        }

        val endpointToken = when {
            tokens[0].equals("Bridge", ignoreCase = true) -> {
                if (tokens.size > 2 && !tokens[1].contains(':') && !tokens[1].startsWith("[")) tokens[2]
                else if (tokens.size > 1) tokens[1]
                else return null
            }
            tokens[0] in listOf("obfs4", "webtunnel", "meek_lite", "dnstt", "vanilla") -> {
                if (tokens.size > 1) tokens[1] else return null
            }
            tokens[0].contains(':') -> tokens[0]
            else -> return null
        }

        return try {
            if (endpointToken.startsWith("[")) {
                // IPv6 [addr]:port
                val closingBracket = endpointToken.indexOf(']')
                if (closingBracket == -1) return null
                val host = endpointToken.substring(1, closingBracket)
                if (host.startsWith("2001:db8", ignoreCase = true)) return null
                val port = endpointToken.substring(closingBracket + 2).toInt()
                host to port
            } else {
                // IPv4 host:port
                val colon = endpointToken.lastIndexOf(':')
                if (colon == -1) return null
                val host = endpointToken.substring(0, colon)
                val port = endpointToken.substring(colon + 1).toInt()
                host to port
            }
        } catch (_: Exception) {
            null
        }
    }

    /**
     * Measure single TCP handshake latency.
     */
    private fun testSocket(host: String, port: Int, timeoutMs: Int): Long? {
        val start = System.nanoTime()
        return try {
            Socket().use { socket ->
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(host, port), timeoutMs)
                val durationMs = (System.nanoTime() - start) / 1_000_000
                durationMs
            }
        } catch (_: Exception) {
            null
        }
    }

    /**
     * Run parallel socket checks on candidate bridges, returning reordered lines
     * with verified responsive bridges sorted by lowest RTT first.
     */
    suspend fun filterAndSortBridges(
        lines: List<String>,
        maxCandidates: Int = DEFAULT_MAX_CANDIDATES,
        timeoutMs: Int = DEFAULT_TIMEOUT_MS
    ): List<String> = withContext(Dispatchers.IO) {
        val validLines = lines.filter { it.isNotBlank() && !it.trim().startsWith("#") }
        val candidateLines = validLines.take(maxCandidates)
        if (candidateLines.isEmpty()) return@withContext emptyList()
        val remainingLines = validLines.drop(maxCandidates)

        val results = candidateLines.map { line ->
            async {
                val ep = extractEndpoint(line)
                if (ep == null) {
                    // Lines that cannot be socket-pinged (e.g. snowflake rendezvous) are kept as-is
                    PingResult(line, reachable = true, rttMs = 9999L)
                } else {
                    val rtt = testSocket(ep.first, ep.second, timeoutMs)
                    if (rtt != null) {
                        PingResult(line, reachable = true, rttMs = rtt)
                    } else {
                        PingResult(line, reachable = false, rttMs = -1L)
                    }
                }
            }
        }.awaitAll()

        val reachable = results.filter { it.reachable }.sortedBy { it.rttMs }
        val unreachable = results.filter { !it.reachable }

        Log.i(TAG, "Pre-flight test: ${reachable.size} responsive / ${candidateLines.size} tested (fastest: ${reachable.firstOrNull()?.rttMs} ms)")

        // Put responsive bridges first, unreachable second, and the remaining pool after
        buildList {
            reachable.forEach { add(it.line) }
            unreachable.forEach { add(it.line) }
            addAll(remainingLines)
        }
    }
}

package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.coroutines.Dispatchers
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * Races three Tor transports against each other:
 *
 *  - [TRANSPORT_VANILLA]: direct (no pluggable transport) bridges
 *  - [TRANSPORT_OBFS4]: obfs4 bridges via lyrebird
 *  - [TRANSPORT_WEBTUNNEL]: webtunnel (HTTP CONNECT over TLS) bridges via lyrebird
 *
 * Bridge lists are fetched from the Tor-Bridges-Collector repository at runtime.
 * The runner whose Tor reports "Bootstrapped 100%" first wins; the other two are
 * stopped immediately.
 */
object ParallelTorManager {
    private const val TAG = "ParallelTorManager"

    const val TRANSPORT_VANILLA = "vanilla"
    const val TRANSPORT_OBFS4 = "obfs4"
    const val TRANSPORT_WEBTUNNEL = "webtunnel"

    private const val BRIDGE_BASE_URL =
        "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge"
    private const val RACE_TIMEOUT_MS = 300_000L
    private const val POLL_INTERVAL_MS = 1_000L

    val BRIDGE_SOURCES = listOf(
        TRANSPORT_VANILLA to "$BRIDGE_BASE_URL/vanilla_tested.txt",
        TRANSPORT_OBFS4 to "$BRIDGE_BASE_URL/obfs4_tested.txt",
        TRANSPORT_WEBTUNNEL to "$BRIDGE_BASE_URL/webtunnel_tested.txt"
    )

    private val runnersLock = Any()
    @Volatile private var runners = mutableMapOf<String, TorRunner>()

    /** Current bootstrap % per transport (-1 = failed). Safe for UI reads. */
    fun progressSnapshot(): Map<String, Int> = synchronized(runnersLock) {
        runners.mapValues { (_, r) -> if (r.failed != null) -1 else r.progress() }
    }

    fun isBusy(): Boolean = synchronized(runnersLock) {
        runners.values.any { it.started && (it.failed == null || !it.ready) && it.isRunning() }
    }

    /**
     * Fetch bridge lists, launch all three runners and wait for the first to
     * reach 100% bootstrap. Returns the winning [TorRunner]; the losers are
     * stopped and their processes torn down.
     *
     * @param basePort the winner's Tor SOCKS5 port is derived from this
     *        (vanilla=base, obfs4=base+1, webtunnel=base+2)
     * @param onProgress called every [POLL_INTERVAL_MS] with a live snapshot,
     *        including runners that have not finished starting yet.
     */
    suspend fun race(
        context: Context,
        basePort: Int,
        sessionId: Int,
        onProgress: (Map<String, TorRunner>) -> Unit
    ): TorRunner {
        stopAll()

        val lines = withContext(Dispatchers.IO) { fetchBridgeLines(context) }

        val ports = mapOf(
            TRANSPORT_VANILLA to basePort,
            TRANSPORT_OBFS4 to basePort + 1,
            TRANSPORT_WEBTUNNEL to basePort + 2
        )

        synchronized(runnersLock) { runners = mutableMapOf() }

        BRIDGE_SOURCES.forEach { (name, _) ->
            val bridgeLines = lines[name] ?: ""
            val runner = TorRunner(context, name, ports[name] ?: basePort, bridgeLines)
            synchronized(runnersLock) { runners[name] = runner }
            val bridgeCount = bridgeLines.lines().count { it.isNotBlank() }
            Log.transport(sessionId, name, 'I', TAG, "starting ($bridgeCount bridges)")
            val result = runner.start()
            if (result.isFailure) {
                val reason = result.exceptionOrNull()?.message ?: "failed to start"
                runner.failed = reason
                Log.transport(sessionId, name, 'E', TAG, "failed to start: $reason")
                Log.e(TAG, "$name failed to start: $reason")
            } else {
                Log.transport(sessionId, name, 'I', TAG, "tor + transport started")
            }
        }

        val deadline = System.currentTimeMillis() + RACE_TIMEOUT_MS
        while (true) {
            val snapshot = synchronized(runnersLock) { runners.toMap() }
            onProgress(snapshot)

            // Mark runners whose Tor process died before completing. Read the
            // real reason from their own per-transport log tail first.
            snapshot.values.forEach { r ->
                if (r.failed == null && r.started && !r.isReady() && !r.isRunning()) {
                    val reason = r.failureSummary()
                    r.failed = reason
                    Log.w(TAG, "${r.name} exited early: $reason")
                    Log.transport(sessionId, r.name, 'E', TAG, "tor died: $reason")
                }
            }

            val winner = snapshot.values.firstOrNull { it.isReady() }
            if (winner != null) {
                Log.i(TAG, "Winner: ${winner.name} at ${winner.progress()}%")
                Log.transport(sessionId, winner.name, 'I', TAG, "*** WINNER *** bootstrapped 100%")
                snapshot.values.filter { it !== winner }.forEach {
                    Log.i(TAG, "Stopping losing transport: ${it.name}")
                    if (it.failed != null) {
                        Log.transport(sessionId, it.name, 'E', TAG, "lost the race: ${it.failed}")
                    }
                    it.stop()
                }
                return winner
            }

            val live = snapshot.values.count { it.failed == null }
            if (live == 0) {
                val details = snapshot.values.joinToString(", ") { "${it.name}=${it.failed}" }
                snapshot.values.forEach {
                    Log.transport(sessionId, it.name, 'E', TAG, "final: ${it.failed}")
                    it.logLines().takeLast(12).forEach { line ->
                        Log.transport(sessionId, it.name, 'D', TAG, line)
                    }
                }
                stopAll()
                throw RuntimeException("All transports failed ($details)")
            }

            if (System.currentTimeMillis() >= deadline) {
                stopAll()
                throw RuntimeException("No transport reached 100% within ${RACE_TIMEOUT_MS / 1000}s")
            }

            delay(POLL_INTERVAL_MS)
        }
    }

    /** Stop and discard every runner. */
    fun stopAll() {
        val list = synchronized(runnersLock) {
            val copy = runners.values.toList()
            runners = mutableMapOf()
            copy
        }
        list.forEach { it.stop() }
    }

    /** Read the cached bridge lists (falling back to a fresh download + cache). */
    private fun fetchBridgeLines(context: Context): Map<String, String> {
        return BRIDGE_SOURCES.associate { (name, url) ->
            name to (BridgeStore.lines(context, name) ?: downloadText(url).also { BridgeStore.saveLines(context, name, it) })
        }
    }

    private fun downloadText(url: String): String {
        Log.i(TAG, "Downloading $url")
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = 20_000
        conn.readTimeout = 20_000
        conn.setRequestProperty("User-Agent", "DeltaTor/1.0")
        return try {
            if (conn.responseCode == HttpURLConnection.HTTP_OK) {
                BufferedReader(InputStreamReader(conn.inputStream)).use { it.readText() }
            } else {
                Log.e(TAG, "HTTP ${conn.responseCode} for $url")
                ""
            }
        } finally {
            conn.disconnect()
        }
    }
}
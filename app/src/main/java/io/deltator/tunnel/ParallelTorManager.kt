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
 * Races four Tor clients against each other:
 *
 *  - [TRANSPORT_VANILLA]: direct (no pluggable transport) bridges
 *  - [TRANSPORT_OBFS4]: obfs4 bridges via lyrebird
 *  - [TRANSPORT_WEBTUNNEL]: webtunnel (HTTP CONNECT over TLS) bridges via lyrebird
 *  - [TRANSPORT_MEMORY]: the bridges that provably worked last time, in any
 *    transport (see [BridgeMemory])
 *
 * The first three always start with their full bridge list. The memory runner
 * only joins once something has been proven; from then on it usually wins in
 * seconds because Tor tries bridges in roughly listed order and gives up fast on
 * the dead ones. Whenever a runner reaches 100%, the bridges it proved are added
 * to its pool so the next connect starts from them.
 *
 * Bridge lists are fetched from the Tor-Bridges-Collector repository at runtime.
 * The runner whose Tor reports "Bootstrapped 100%" first wins; the other three
 * are stopped immediately.
 */
object ParallelTorManager {
    private const val TAG = "ParallelTorManager"

    const val TRANSPORT_VANILLA = "vanilla"
    const val TRANSPORT_OBFS4 = "obfs4"
    const val TRANSPORT_WEBTUNNEL = "webtunnel"
    const val TRANSPORT_MEMORY = "memory"

    private const val BRIDGE_BASE_URL =
        "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge"
    private const val RACE_TIMEOUT_MS = 300_000L
    private const val POLL_INTERVAL_MS = 1_000L
    private const val PORT_FREE_TIMEOUT_MS = 15_000L
    private const val PORT_FREE_POLL_MS = 250L

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
     * Fetch bridge lists, launch every runner and wait for the first to reach
     * 100% bootstrap. Returns the winning [TorRunner]; the losers are stopped and
     * their processes torn down.
     *
    /**
     * @param basePort the app's own TUN<->Tor bridge port. It is reserved and NOT
     *        handed to any runner: the runners take basePort+1 .. basePort+4, so a
     *        runner that wins can never hold the port the bridge needs to bind.
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

        val memoryLines = BridgeMemory.bridgeLinesFor(context, lines)
        if (memoryLines != null) {
            val n = memoryLines.lines().count { it.isNotBlank() }
            Log.i(TAG, "Memory runner: $n proven bridge(s) available")
            Log.transport(sessionId, TRANSPORT_MEMORY, 'I', TAG, "reusing $n previously proven bridge(s)")
        } else {
            Log.i(TAG, "Memory runner: nothing proven yet, racing 3 transports only")
        }

        // basePort belongs to the app's TUN bridge, so the runners start above it.
        val ports = mapOf(
            TRANSPORT_VANILLA to basePort + 1,
            TRANSPORT_OBFS4 to basePort + 2,
            TRANSPORT_WEBTUNNEL to basePort + 3,
            TRANSPORT_MEMORY to basePort + 4
        )
        val plans = buildList {
            BRIDGE_SOURCES.forEach { (name, _) -> add(name to (lines[name] ?: "")) }
            memoryLines?.let { add(TRANSPORT_MEMORY to it) }
        }

        synchronized(runnersLock) { runners = mutableMapOf() }

        plans.forEach { (name, bridgeLines) ->
            val runner = TorRunner(context, name, ports.getValue(name), bridgeLines)
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

        // Runners already proven in an earlier poll, so two runners finishing in
        // the same tick both contribute their bridges to the memory.
        val recorded = mutableSetOf<String>()

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

            // Every runner that reached 100% teaches the memory its working bridges.
            snapshot.values.filter { it.isReady() && recorded.add(it.name) }
                .forEach { recordMemory(context, sessionId, it) }

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

    /**
     * Store the bridges a 100% runner proved and, when it is the memory runner,
     * drop the ones that just died so the pool cannot lock onto a dead set.
     */
    private fun recordMemory(context: Context, sessionId: Int, runner: TorRunner) {
        val proven = runner.healthyBridges()
        if (proven.isEmpty()) {
            Log.transport(sessionId, runner.name, 'I', TAG, "100% but no bridge descriptor seen, memory unchanged")
            return
        }
        val added = BridgeMemory.remember(context, runner.name, proven)
        Log.transport(
            sessionId, runner.name, 'I', TAG,
            "memory updated: ${runner.healthyBridges().size} working bridge(s), +$added new " +
                "(pool ${BridgeMemory.count(context, runner.name)})"
        )
    }

    /**
     * Stop and discard every runner. [TorRunner.stop] blocks until the processes
     * are really gone, so this returns with no Tor/lyrebird process of ours left
     * holding a socket.
     */
    fun stopAll() {
        val list = synchronized(runnersLock) {
            val copy = runners.values.toList()
            runners = mutableMapOf()
            copy
        }
        list.forEach { it.stop() }
    }

    /**
     * Block until [port] on [host] can actually be bound, or the timeout expires.
     * A killed process keeps its listening socket until the kernel reaps it, so
     * anything that binds right after a teardown has to wait for this.
     * Returns true when the port is free.
     */
    fun awaitPortFree(host: String, port: Int, timeoutMs: Long = PORT_FREE_TIMEOUT_MS): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
        var attempt = 0
        while (true) {
            val free = try {
                java.net.ServerSocket().use { it.reuseAddress = true; it.bind(java.net.InetSocketAddress(host, port)); true }
            } catch (_: Exception) {
                false
            }
            if (free) {
                if (attempt > 0) Log.i(TAG, "port $port free after $attempt wait(s)")
                return true
            }
            if (System.currentTimeMillis() >= deadline) {
                Log.e(TAG, "port $port still in use after ${timeoutMs}ms")
                return false
            }
            attempt++
            Log.w(TAG, "port $port still held, waiting (attempt $attempt)")
            Thread.sleep(PORT_FREE_POLL_MS)
        }
    }

    /**
     * Stop everything and wait for every runner port to be released. Used on
     * disconnect so a following connect never races the previous teardown.
     */
    fun stopAllAndWait(basePort: Int, host: String = "127.0.0.1"): Boolean {
        val ports = (basePort..basePort + 4).toList()
        stopAll()
        return ports.all { awaitPortFree(host, it) }
    }

    /** Read the cached bridge lists (falling back to a fresh download + cache). */
    private fun fetchBridgeLines(context: Context): Map<String, String> {
        val lines = BRIDGE_SOURCES.associate { (name, url) ->
            name to (BridgeStore.lines(context, name) ?: downloadText(url).also { BridgeStore.saveLines(context, name, it) })
        }
        // Let the UI (exit-node ranking, bridge counts) see the cache we just wrote.
        BridgeStore.refreshState(context)
        return lines
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
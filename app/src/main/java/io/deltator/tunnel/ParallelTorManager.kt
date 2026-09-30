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
import java.util.concurrent.atomic.AtomicLong

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
 * When the user picks one transport instead of auto, that transport still gets
 * its memory twin: `webtunnel` races `webtunnel-memory` side by side, with the
 * twin carrying only the webtunnel bridges that worked before. Nothing about the
 * picked mode changes; the proven set just gets its own second chance.
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
    const val TRANSPORT_SNOWFLAKE = "snowflake"
    const val TRANSPORT_DIRECT = "direct"
    const val TRANSPORT_MEMORY = "memory"

    /** Selectable connect modes. [TRANSPORT_MEMORY] is a runner, not a mode. */
    const val TRANSPORT_AUTO = "auto"
    const val TRANSPORT_CUSTOM = "custom"
    val MODES = listOf(
        TRANSPORT_AUTO,
        TRANSPORT_VANILLA,
        TRANSPORT_OBFS4,
        TRANSPORT_WEBTUNNEL,
        TRANSPORT_SNOWFLAKE,
        TRANSPORT_DIRECT,
        TRANSPORT_CUSTOM
    )

    /**
     * Suffix of the memory twin runner. Every transport a user can select on its
     * own also gets a `<transport>-memory` companion, so choosing e.g. webtunnel
     * races the full webtunnel list *and* the webtunnel bridges that provably
     * worked before, instead of leaving that knowledge unused.
     */
    const val MEMORY_SUFFIX = "-memory"

    fun memoryNameFor(transport: String): String = "$transport$MEMORY_SUFFIX"

    /** The real transport behind a runner name (`webtunnel-memory` -> `webtunnel`). */
    fun baseTransportOf(name: String): String = name.substringBefore(MEMORY_SUFFIX)

    private const val BRIDGE_BASE_URL =
        "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge"

    /**
     * Remote bridge lists per transport. webtunnel is published as two files and
     * both are used: they are merged into a single list before anything is handed
     * to Tor. Every list is also bundled in the APK, so the bundled copy is the
     * first fallback before the network is tried.
     */
    val BRIDGE_SOURCES: Map<String, List<String>> = mapOf(
        TRANSPORT_VANILLA to listOf("$BRIDGE_BASE_URL/vanilla.txt"),
        TRANSPORT_OBFS4 to listOf("$BRIDGE_BASE_URL/obfs4.txt"),
        TRANSPORT_WEBTUNNEL to listOf(
            "$BRIDGE_BASE_URL/webtunnel.txt",
            "$BRIDGE_BASE_URL/webtunnel_ipv6.txt"
        ),
        TRANSPORT_SNOWFLAKE to listOf("$BRIDGE_BASE_URL/snowflake.txt")
    )

    /**
     * The same files are bundled in the APK under assets/bridges/, so a connect
     * works on the first launch and while the network is unavailable. The asset
     * name is the last path segment of its URL, so the two lists can never drift
     * apart.
     */
    const val BUNDLED_ASSET_DIR = "bridges"

    fun bundledAssetName(url: String): String = url.substringAfterLast('/')

    private const val RACE_TIMEOUT_MS = 1_800_000L

    /** A recovery restart is expected to be quick: proven bridges, same port. */
    private const val RECOVERY_TIMEOUT_MS = 120_000L
    private const val POLL_INTERVAL_MS = 1_000L
    private const val PORT_FREE_TIMEOUT_MS = 15_000L
    private const val PORT_FREE_POLL_MS = 250L

    /** Runners take basePort+1 .. basePort+[MAX_PORT_OFFSET]. */
    private const val MAX_PORT_OFFSET = 11

    /**
     * The fixed runner<->port assignment. It is a function of [basePort] only, so a
     * runner keeps the same port across a recovery, and a runner that was not part
     * of the last race still has a known, reserved port to be restarted on.
     */
    private fun runnerPorts(basePort: Int): Map<String, Int> = mapOf(
        TRANSPORT_VANILLA to basePort + 1,
        TRANSPORT_OBFS4 to basePort + 2,
        TRANSPORT_WEBTUNNEL to basePort + 3,
        TRANSPORT_MEMORY to basePort + 4,
        TRANSPORT_SNOWFLAKE to basePort + 5,
        TRANSPORT_CUSTOM to basePort + 6,
        TRANSPORT_DIRECT to basePort + 7,
        memoryNameFor(TRANSPORT_VANILLA) to basePort + 8,
        memoryNameFor(TRANSPORT_OBFS4) to basePort + 9,
        memoryNameFor(TRANSPORT_WEBTUNNEL) to basePort + 10,
        memoryNameFor(TRANSPORT_SNOWFLAKE) to basePort + 11
    )


    private val runnersLock = Any()
    @Volatile private var runners = mutableMapOf<String, TorRunner>()

    /**
     * Bridge lines and ports of the last race, kept so a recovery can rebuild the
     * winning transport without going back to the network for its bridge list.
     */
    @Volatile private var lastPlans: Map<String, String> = emptyMap()
    @Volatile private var lastPorts: Map<String, Int> = emptyMap()

    /**
     * Bumped whenever the runner map is emptied. A start captures it and checks
     * it every poll, so a stop or a second connect that empties the map under a
     * running race is reported as the supersession it is, instead of the race
     * waking up to an empty snapshot and blaming the transports it was watching.
     */
    private val generation = AtomicLong(0)

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
     * @param basePort the app's own TUN<->Tor bridge port. It is reserved and NOT
     *        handed to any runner: the runners take basePort+1 .. basePort+7, so a
     *        runner that wins can never hold the port the bridge needs to bind.
     * @param autoTransports the transports the user allows in auto mode. Ignored by
     *        every other mode, which always runs exactly one transport.
     * @param onProgress called every [POLL_INTERVAL_MS] with a live snapshot,
     *        including runners that have not finished starting yet.
     */
    suspend fun race(
        context: Context,
        basePort: Int,
        sessionId: Int,
        transportMode: String,
        customBridges: String,
        autoTransports: Set<String>,
        onProgress: (Map<String, TorRunner>) -> Unit
    ): TorRunner {
        stopAll()

        // Unknown or missing values fall back to auto, so a stale preference can
        // never leave the user with no way to connect.
        val mode = transportMode.lowercase().let {
            if (it in MODES) it else TRANSPORT_AUTO
        }
        val customLines = customBridges.lineSequence()
            .map { it.trim() }
            .filter { it.isNotEmpty() && !it.startsWith("#") }
            .toList()

        if (mode == TRANSPORT_CUSTOM && customLines.isEmpty()) {
            stopAll()
            throw RuntimeException("No custom bridges provided")
        }

        // Auto races exactly what the user ticked. A blank or fully stale set
        // falls back to the full list so auto can never end up with nothing.
        val autoNames = autoTransports.filter { it in BRIDGE_SOURCES }.toList()
            .ifEmpty { BRIDGE_SOURCES.keys.toList() }

        // Only the lists this mode actually needs are resolved, which keeps a
        // direct connect from waiting on any download at all.
        val needed = when (mode) {
            TRANSPORT_AUTO -> autoNames.toSet()
            TRANSPORT_DIRECT -> emptySet()
            TRANSPORT_CUSTOM -> emptySet()
            else -> setOf(mode)
        }
        val lines = if (mode == TRANSPORT_CUSTOM) {
            mapOf(TRANSPORT_CUSTOM to customLines.joinToString("\n"))
        } else {
            withContext(Dispatchers.IO) { fetchBridgeLines(context, needed) }
        }

        // The memory runner, in the two shapes it can take:
        //  - auto with several transports: one mixed runner over every pool
        //  - a single selected transport (or auto that resolved to one): a twin
        //    of that very transport, `webtunnel-memory` next to `webtunnel`
        // Direct and custom mode have no memory twin: there are no bridges to
        // remember, or the user asked for one exact set.
        val singleTransport: String? = when {
            mode == TRANSPORT_AUTO && autoNames.size == 1 -> autoNames.first()
            mode in BRIDGE_SOURCES -> mode
            else -> null
        }
        val mixedMemoryLines = if (mode == TRANSPORT_AUTO && autoNames.size > 1) {
            BridgeMemory.bridgeLinesFor(context, lines)
        } else {
            null
        }
        val twinMemoryLines = singleTransport?.let { BridgeMemory.bridgeLinesFor(context, lines, it) }
        val twinName = singleTransport?.let { memoryNameFor(it) }
        if (mixedMemoryLines != null) {
            val n = mixedMemoryLines.lines().count { it.isNotBlank() }
            Log.i(TAG, "Memory runner: $n proven bridge(s) available")
            Log.transport(sessionId, TRANSPORT_MEMORY, 'I', TAG, "reusing $n previously proven bridge(s)")
        }
        if (twinMemoryLines != null && twinName != null) {
            val n = twinMemoryLines.lines().count { it.isNotBlank() }
            Log.i(TAG, "Memory twin: $n proven $singleTransport bridge(s) for $twinName")
            Log.transport(
                sessionId, twinName, 'I', TAG,
                "reusing $n previously proven $singleTransport bridge(s)"
            )
        }
        if (mixedMemoryLines == null && twinMemoryLines == null) {
            Log.i(TAG, "Memory runner: nothing proven yet for this mode, racing without it")
        }

        // basePort belongs to the app's TUN bridge, so the runners start above it.
        val allPorts = runnerPorts(basePort)
        val plans = buildList {
            when (mode) {
                TRANSPORT_AUTO -> autoNames.forEach { name -> add(name to (lines[name] ?: "")) }
                TRANSPORT_VANILLA -> add(TRANSPORT_VANILLA to (lines[TRANSPORT_VANILLA] ?: ""))
                TRANSPORT_OBFS4 -> add(TRANSPORT_OBFS4 to (lines[TRANSPORT_OBFS4] ?: ""))
                TRANSPORT_WEBTUNNEL -> add(TRANSPORT_WEBTUNNEL to (lines[TRANSPORT_WEBTUNNEL] ?: ""))
                TRANSPORT_SNOWFLAKE -> add(TRANSPORT_SNOWFLAKE to (lines[TRANSPORT_SNOWFLAKE] ?: ""))
                TRANSPORT_DIRECT -> add(TRANSPORT_DIRECT to "")
                TRANSPORT_CUSTOM -> add(TRANSPORT_CUSTOM to (lines[TRANSPORT_CUSTOM] ?: ""))
            }
            if (mixedMemoryLines != null) {
                add(TRANSPORT_MEMORY to mixedMemoryLines)
            }
            if (twinMemoryLines != null && twinName != null) {
                add(twinName to twinMemoryLines)
            }
        }

        if (plans.isEmpty()) {
            stopAll()
            throw RuntimeException("No bridges available for $mode")
        }

        val planNames = plans.map { it.first }.toSet()
        lastPlans = plans.toMap()
        lastPorts = allPorts.filterKeys { it in planNames }

        val gen = beginRunners()

        plans.forEach { (name, bridgeLines) ->
            val runner = TorRunner(context, name, allPorts.getValue(name), bridgeLines)
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
            checkGeneration(gen)
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
                val minutes = RACE_TIMEOUT_MS / 60_000
                throw RuntimeException("No transport reached 100% within $minutes min")
            }

            delay(POLL_INTERVAL_MS)
        }
    }

    /**
     * Store the bridges a 100% runner proved and, when it is the memory runner,
     * drop the ones that just died so the pool cannot lock onto a dead set.
     *
     * The proof is always filed under the real transport, never under the runner
     * name: a `webtunnel-memory` twin teaches the webtunnel pool, so both the
     * twin and the plain webtunnel runner benefit from it next time.
     */
    private fun recordMemory(context: Context, sessionId: Int, runner: TorRunner) {
        val proven = runner.healthyBridges()
        if (proven.isEmpty()) {
            Log.transport(sessionId, runner.name, 'I', TAG, "100% but no bridge descriptor seen, memory unchanged")
            return
        }
        val transport = baseTransportOf(runner.name)
        val added = BridgeMemory.remember(context, transport, proven)
        Log.transport(
            sessionId, runner.name, 'I', TAG,
            "memory[$transport] updated: ${proven.size} working bridge(s), +$added new " +
                "(pool ${BridgeMemory.count(context, transport)})"
        )
    }

    /**
     * Stop and discard every runner. [TorRunner.stop] blocks until the processes
     * are really gone, so this returns with no Tor/lyrebird process of ours left
     * holding a socket.
     */
    /**
     * Bring one transport back after the network dropped under it.
     *
     * A full [race] is the wrong tool for a link that just blipped: it would go
     * back to the bridge lists, race every transport again and hand the caller a
     * new port. This re-runs just the transport that was carrying traffic, on the
     * port it already had, with the same lines it had, plus its memory twin when
     * the pool has something proven. Nothing else is touched, so the caller's
     * listener (the app's fixed SOCKS bridge) survives and only needs a repoint.
     *
     * Returns the new winner, or throws if it cannot bootstrap in
     * [RECOVERY_TIMEOUT_MS]; the caller then falls back to a full reconnect.
     */
    suspend fun restartTransport(
        context: Context,
        basePort: Int,
        sessionId: Int,
        name: String,
        onProgress: (Map<String, TorRunner>) -> Unit = {}
    ): TorRunner {
        val cached = lastPlans[name]
        if (cached == null) {
            throw RuntimeException("No cached bridges for $name")
        }
        if (lastPorts[name] == null) {
            throw RuntimeException("No cached port for $name")
        }

        // The twin is looked up fresh: the pool may have grown since the race,
        // and a transport that had no twin then can have one now. The mixed
        // auto-mode memory runner is skipped, it already carries every pool and
        // has no twin of its own.
        val base = baseTransportOf(name)
        val twin = memoryNameFor(base)
        val twinLines = if (twin == name || name == TRANSPORT_MEMORY) {
            null
        } else {
            BridgeMemory.bridgeLinesFor(context, mapOf(base to cached), base) ?: lastPlans[twin]
        }

        // A twin that has no port of its own cannot be started. Its port is
        // reserved by [runnerPorts], not by the last race: on the first connect
        // the memory pool is empty, so no twin races and no port is recorded for
        // it, while the race itself fills the pool. Recovery looks the twin up
        // again and used to find no port for it, throw, and escalate the whole
        // connection into a reconnect from zero. It now takes the reserved port.
        val fixedPorts = runnerPorts(basePort)
        val plans = buildList {
            add(name to cached)
            if (twinLines != null && fixedPorts.containsKey(twin)) add(twin to twinLines)
        }
        val ports = plans.associate { (planName, _) ->
            planName to (lastPorts[planName] ?: fixedPorts.getValue(planName))
        }

        // What this recovery builds becomes the last plan, the same way a race
        // records its own. Without it the twin it just started is unreachable:
        // in auto mode the race never plans a twin at all, so lastPlans holds no
        // twin, yet the twin is the one holding the proven bridges and it is the
        // one that wins. The next recovery asked for the active transport, got
        // that twin, found no cached bridges for it and threw before it even
        // started a process, which escalated a working connection into a
        // reconnect from zero. Merged, not replaced: a transport proven by an
        // earlier race is still a transport this recovery can rebuild.
        lastPlans = lastPlans + plans
        lastPorts = lastPorts + ports

        Log.i(TAG, "Recovery: restarting $name on port ${ports.getValue(name)}" +
            if (plans.size > 1) " with $twin" else "")

        // The dying processes still hold their listeners, and a replacement that
        // binds too early just fails to start, so wait the ports out.
        stopAll()
        withContext(Dispatchers.IO) {
            ports.values.forEach { awaitPortFree("127.0.0.1", it) }
        }

        val gen = beginRunners()
        val recorded = mutableSetOf<String>()
        plans.forEach { (planName, bridgeLines) ->
            val runner = TorRunner(context, planName, ports.getValue(planName), bridgeLines)
            synchronized(runnersLock) { runners[planName] = runner }
            val bridgeCount = bridgeLines.lines().count { it.isNotBlank() }
            Log.transport(sessionId, planName, 'I', TAG, "recovery restart ($bridgeCount bridges)")
            val result = runner.start()
            if (result.isFailure) {
                val reason = result.exceptionOrNull()?.message ?: "failed to start"
                runner.failed = reason
                Log.transport(sessionId, planName, 'E', TAG, "recovery start failed: $reason")
            }
        }

        val deadline = System.currentTimeMillis() + RECOVERY_TIMEOUT_MS
        while (true) {
            checkGeneration(gen)
            val snapshot = synchronized(runnersLock) { runners.toMap() }
            onProgress(snapshot)
            snapshot.values.forEach { r ->
                if (r.failed == null && r.started && !r.isReady() && !r.isRunning()) {
                    r.failed = r.failureSummary()
                    Log.transport(sessionId, r.name, 'E', TAG, "recovery: tor died: ${r.failed}")
                }
            }
            snapshot.values.filter { it.isReady() && recorded.add(it.name) }
                .forEach { recordMemory(context, sessionId, it) }

            snapshot.values.firstOrNull { it.isReady() }?.let { winner ->
                snapshot.values.filter { it !== winner }.forEach {
                    Log.i(TAG, "Recovery: stopping ${it.name}")
                    it.stop()
                }
                Log.transport(sessionId, winner.name, 'I', TAG, "*** RECOVERED *** 100% on port ${winner.torSocksPort}")
                return winner
            }

            if (snapshot.values.all { it.failed != null }) {
                val details = snapshot.values.joinToString(", ") { "${it.name}=${it.failed}" }
                stopAll()
                throw RuntimeException("Recovery failed ($details)")
            }
            if (System.currentTimeMillis() >= deadline) {
                stopAll()
                throw RuntimeException("Recovery of $name did not bootstrap in ${RECOVERY_TIMEOUT_MS / 1000}s")
            }
            delay(POLL_INTERVAL_MS)
        }
    }

    fun stopAll() {
        detachRunners().forEach { it.stop() }
    }

    /** Take the runner list out of the map so a stop cannot race a new start. */
    private fun detachRunners(): List<TorRunner> = synchronized(runnersLock) {
        val copy = runners.values.toList()
        runners = mutableMapOf()
        generation.incrementAndGet()
        copy
    }

    /**
     * Empty the map for a fresh start and take the generation that start owns.
     * Any poll loop still running on an older generation is watching runners
     * that no longer exist and is told so instead of failing on its own.
     */
    private fun beginRunners(): Long = synchronized(runnersLock) {
        runners = mutableMapOf()
        generation.incrementAndGet()
    }

    private fun checkGeneration(gen: Long) {
        if (generation.get() != gen) {
            throw RuntimeException("Start was superseded by a stop or a newer connect")
        }
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
     * disconnect and on a user stop so a following connect never races the
     * previous teardown.
     *
     * The return value is not just "we sent the signal": every runner reports
     * whether its processes were seen to exit, and a port only frees once the
     * kernel has reaped the listener, so a true here means the cores are really
     * down and the ports are really back.
     */
    fun stopAllAndWait(basePort: Int, host: String = "127.0.0.1"): Boolean {
        // basePort itself is the app's own TUN bridge; runners take +1 .. +MAX_PORT_OFFSET.
        val ports = (basePort..basePort + MAX_PORT_OFFSET).toList()
        val list = detachRunners()
        list.forEach { it.stop() }
        val survivors = list.filter { !it.confirmedStopped }
        survivors.forEach { Log.e(TAG, "runner ${it.name} still has a live process after SIGKILL") }
        Log.i(
            TAG,
            "stopped ${list.size} runner(s), ${list.size - survivors.size} confirmed dead, " +
                "${survivors.size} unconfirmed"
        )
        val free = ports.all { awaitPortFree(host, it) }
        Log.i(TAG, if (free) "every port free" else "some ports are still held")
        return free
    }

    /**
     * Read the bridge list for every transport. Preference order:
     * disk cache -> bundled assets -> network. A transport may be backed by
     * several files, which are merged by [mergeBridgeLists] into one list.
     */
    private fun fetchBridgeLines(context: Context, only: Set<String>? = null): Map<String, String> {
        val lines = LinkedHashMap<String, String>()
        BRIDGE_SOURCES.forEach { (name, urls) ->
            if (only != null && name !in only) return@forEach
            val cached = BridgeStore.lines(context, name)
            val content = when {
                !cached.isNullOrBlank() -> cached
                else -> readBundled(context, urls)?.also { BridgeStore.saveLines(context, name, it) }
                    ?: downloadLists(urls).also { if (it.isNotBlank()) BridgeStore.saveLines(context, name, it) }
            }
            lines[name] = content
        }
        // Let the UI (exit-node ranking, bridge counts) see the cache we just wrote.
        BridgeStore.refreshState(context)
        return lines
    }

    /** The bundled copies of every file this transport is built from. */
    private fun readBundled(context: Context, urls: List<String>): String? {
        val bodies = urls.mapNotNull { url ->
            readBundledAsset(context, "$BUNDLED_ASSET_DIR/${bundledAssetName(url)}")
        }
        return mergeBridgeLists(bodies).ifBlank { null }
    }

    /** One bundled asset file, or null when it is missing or empty. */
    private fun readBundledAsset(context: Context, asset: String): String? = try {
        context.assets.open(asset).use { it.readBytes().toString(Charsets.UTF_8) }
            .takeIf { it.isNotBlank() }
            ?.also { Log.i(TAG, "bundled $asset (${it.length} chars)") }
    } catch (e: Exception) {
        Log.w(TAG, "no bundled $asset: ${e.message}")
        null
    }

    private fun downloadLists(urls: List<String>): String {
        val bodies = urls.mapNotNull { url ->
            val text = runCatching { downloadText(url) }.getOrElse {
                Log.e(TAG, "download failed for $url: ${it.message}")
                ""
            }
            text.takeIf { it.isNotBlank() }
        }
        return mergeBridgeLists(bodies)
    }

    /**
     * Merge several bridge files into one list, keeping every bridge exactly once
     * (identified by its fingerprint) and alternating between the sources so a
     * transport backed by two files still draws from both once Tor's per-runner
     * line cap is applied. Comment and blank lines are dropped.
     *
     * A bridge is republished whenever the client protocol changes, so the same
     * fingerprint shows up with an older and a newer `ver=`. The newest one wins,
     * because a stale protocol version is rejected during the handshake.
     */
    fun mergeBridgeLists(bodies: List<String>): String {
        val lists = bodies.map { body ->
            body.lines().map { it.trim() }
                .filter { it.isNotEmpty() && !it.startsWith("#") }
                .map { it to BridgeMemory.fingerprintOf(it) }
        }
        if (lists.isEmpty()) return ""
        if (lists.size == 1) return lists[0].joinToString("\n") { it.first }

        val merged = LinkedHashMap<String, String>()
        val longest = lists.maxOf { it.size }
        for (i in 0 until longest) {
            lists.forEach { list ->
                val (line, fp) = list.getOrNull(i) ?: return@forEach
                val key = fp ?: line
                val kept = merged[key]
                if (kept == null || newerWebtunnelVersion(line, kept)) {
                    // A re-published line replaces the older one in place, so the
                    // order the bridges were first seen in is preserved.
                    merged[key] = line
                }
            }
        }
        Log.i(TAG, "merged ${lists.size} bridge files into ${merged.size} unique bridge(s)")
        return merged.values.joinToString("\n")
    }

    /**
     * True when [candidate] carries a webtunnel protocol version newer than the one
     * in [kept]. Lines without a `ver=` never replace a kept line, so every other
     * transport keeps the first-seen-wins behaviour.
     */
    private fun newerWebtunnelVersion(candidate: String, kept: String): Boolean {
        val a = webtunnelVersion(candidate) ?: return false
        val b = webtunnelVersion(kept) ?: return false
        if (a.first != b.first) return a.first > b.first
        if (a.second != b.second) return a.second > b.second
        return a.third > b.third
    }

    /**
     * The `ver=` of a webtunnel line, or null when the line has none.
     */
    private fun webtunnelVersion(line: String): Triple<Int, Int, Int>? {
        val raw = line.split(' ').firstOrNull { it.startsWith("ver=") }
            ?: return null
        val parts = raw.removePrefix("ver=").split('.').mapNotNull { it.toIntOrNull() }
        if (parts.isEmpty()) return null
        return Triple(parts.getOrElse(0) { 0 }, parts.getOrElse(1) { 0 }, parts.getOrElse(2) { 0 })
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
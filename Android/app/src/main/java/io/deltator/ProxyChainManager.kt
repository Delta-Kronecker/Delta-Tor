package io.deltator

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import io.deltator.util.AppLog as Log
import kittoku.osc.SharedBridge
import kittoku.osc.SstpMode
import kittoku.osc.SstpParams
import kittoku.osc.control.Controller
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.selects.select
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.URL
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger


private const val TAG = "ChainList"

/** The daily-updated VPN Gate host list. Format: one `host[:port]` per line. */
private const val HOSTLIST_URL =
    "https://github.com/Delta-Kronecker/Vpn-Gate/raw/refs/heads/main/sstp_hosts.txt"

/** What an untested/failed measurement is stored as. */
private const val NO_DELAY = -1L

/**
 * The VPN Gate server pool behind the Proxy Chain: fetches the host list once a
 * day (direct first, from behind Tor if the direct route is censored away),
 * probes each server with a *real* SSTP session, ranks the pool by measured
 * delay, and measures the live chain with a URL request once it exists.
 *
 * Every probe is the full negotiation the tunnel itself uses — TCP to Tor's
 * SOCKS bridge, TLS, the SSTP HTTP request, LCP, auth, IPCP — because a TCP
 * connect alone says nothing about a server that answers the port but refuses
 * the hub. The probe session never attaches a TUN ([SstpMode.PROBE]).
 */
object ProxyChainManager {

    data class Server(val host: String, val port: Int, val delayMs: Long = NO_DELAY) {
        val key: String get() = "$host:$port"
        val tested: Boolean get() = delayMs >= 0
    }

    /** Published for the config sheet, newest ranking first already applied. */
    data class ListState(val servers: List<Server> = emptyList(), val updatedAtMillis: Long = 0L)

    @Volatile
    private var appContext: Context? = null

    @Volatile
    private var updatedAtMillis = 0L

    @Volatile
    private var servers: List<Server> = emptyList()

    @Volatile
    var testing: Boolean = false
        private set

    private val _listState = MutableStateFlow(ListState())
    val listState: StateFlow<ListState> = _listState.asStateFlow()

    private fun file(context: Context) = java.io.File(context.filesDir, "proxy_chain.json")

    fun init(context: Context) {
        if (appContext != null) return
        appContext = context.applicationContext
        load(context.applicationContext)
    }

    // ------------------------------------------------------------------ store

    private fun load(context: Context) {
        try {
            val raw = file(context).takeIf { it.exists() }?.readText() ?: return
            val root = JSONObject(raw)
            updatedAtMillis = root.optLong("updated", 0L)
            val array = root.optJSONArray("servers") ?: return
            val parsed = ArrayList<Server>(array.length())
            for (i in 0 until array.length()) {
                val o = array.getJSONObject(i)
                parsed.add(Server(o.getString("h"), o.getInt("p"), o.optLong("ms", NO_DELAY)))
            }
            servers = parsed.sorted()
            publish()
        } catch (e: Exception) {
            Log.w(TAG, "chain list restore failed: ${e.message}")
        }
    }

    private fun save(context: Context) {
        try {
            val root = JSONObject()
            root.put("updated", updatedAtMillis)
            val array = JSONArray()
            servers.forEach {
                val o = JSONObject()
                o.put("h", it.host)
                o.put("p", it.port)
                o.put("ms", it.delayMs)
                array.put(o)
            }
            root.put("servers", array)
            file(context).writeText(root.toString())
        } catch (e: Exception) {
            Log.w(TAG, "chain list save failed: ${e.message}")
        }
    }

    private fun publish() {
        _listState.value = ListState(servers, updatedAtMillis)
    }

    /** Sorted list view: fastest measured first, servers never probed last. */
    private fun List<Server>.sorted(): List<Server> =
        sortedWith(compareBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs })

    // ------------------------------------------------------------------ daily

    /** One day, with a margin so an update is not skipped over midnight drift. */
    private const val REFRESH_MILLIS = 20L * 60 * 60 * 1000

    suspend fun updateIfNeeded(context: Context) {
        init(context)
        val freshEnough = servers.isNotEmpty() &&
            System.currentTimeMillis() - updatedAtMillis < REFRESH_MILLIS
        if (freshEnough) return

        updateNow(context)
    }

    /** Fetch, parse, dedupe, keep previous measurements where the key survives. */
    suspend fun updateNow(context: Context) {
        val text = withContext(Dispatchers.IO) { fetchDirect() } ?: run {
            Log.i(TAG, "direct fetch failed, retrying behind Tor")
            withContext(Dispatchers.IO) { fetchBehindTor() }
        } ?: run {
            Log.w(TAG, "host list fetch failed on both routes")
            return
        }

        val parsed = parse(text)
        if (parsed.isEmpty()) {
            Log.w(TAG, "host list came back empty")
            return
        }

        val previous = servers.associateBy({ it.key }, { it })
        val merged = parsed.map { previous[it.key] ?: it }
        updatedAtMillis = System.currentTimeMillis()
        servers = merged.sorted()
        save(context.applicationContext)
        publish()
        Log.i(TAG, "host list updated: ${merged.size} servers")
    }

    private fun fetchDirect(): String? = fetch(viaTor = false)

    private fun fetchBehindTor(): String? = fetch(viaTor = true)

    private fun fetch(viaTor: Boolean): String? {
        return try {
            val url = URL(HOSTLIST_URL)
            val connection = if (viaTor) {
                url.openConnection(
                    Proxy(
                        Proxy.Type.SOCKS,
                        InetSocketAddress("127.0.0.1", Config.proxyPort)
                    )
                )
            } else {
                url.openConnection(Proxy.NO_PROXY)
            }
            connection.connectTimeout = 15_000
            connection.readTimeout = 20_000
            connection.getInputStream().bufferedReader().use { it.readText() }
        } catch (e: Exception) {
            null
        }
    }

    private fun parse(text: String): List<Server> {
        val defaultPort = Config.sstpPort
        val seen = HashSet<String>()
        val out = ArrayList<Server>()
        text.lines().forEach { raw ->
            val line = raw.trim()
            if (line.isEmpty() || line.startsWith("#")) return@forEach

            val colon = line.lastIndexOf(':')
            val suffix = if (colon > 0) line.substring(colon + 1) else ""
            val (host, port) = if (colon > 0 && suffix.isNotEmpty() && suffix.all(Char::isDigit)) {
                line.substring(0, colon) to suffix.toInt()
            } else {
                line to defaultPort
            }
            if (host.isBlank()) return@forEach

            val normalized = host.lowercase(Locale.ROOT)
            if (seen.add("$normalized:$port")) {
                out.add(Server(normalized, port))
            }
        }
        return out
    }

    // ---------------------------------------------------------------- ranking

    /**
     * The [limit] best servers apart from [exclude]: ranked by the last real
     * probe, untested ones last. This is what the engine falls back to when a
     * server dies.
     */
    fun bestServers(exclude: String?, limit: Int): List<Server> {
        return servers
            .filter { it.key != exclude }
            .sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
            .take(limit)
    }

    // ------------------------------------------------------------ probing

    private const val BATCH_SIZE = 10

    private const val PROBE_TIMEOUT_MS = 45_000L

    /**
     * Probe every server with a real SSTP session, [BATCH_SIZE] at a time.
     *
     * Progress is published through [AppState.updateChain] so the sheet can
     * watch it without polling. When all servers have been probed the pool is
     * re-ranked and persisted.
     */
    suspend fun testAll(): Boolean {
        if (testing) return false
        val pool = servers
        if (pool.isEmpty()) return false
        if (!io.deltator.tunnel.TorSocksBridge.isRunning()) {
            Log.w(TAG, "probe skipped: the Tor bridge is not running")
            return false
        }

        testing = true
        val context = appContext
        AppState.updateChain { it.copy(tested = 0, testTotal = pool.size) }

        try {
            val semaphore = Semaphore(BATCH_SIZE)
            val done = AtomicInteger(0)
            val results = java.util.concurrent.ConcurrentHashMap<String, Long>()

            withContext(Dispatchers.IO) {
                pool.map { server ->
                    launch {
                        semaphore.withPermit {
                            val delay = probeServer(server)
                            if (delay != null) results[server.key] = delay
                            applyDelay(server.key, delay ?: NO_DELAY)
                            val finished = done.incrementAndGet()
                            AppState.updateChain { it.copy(tested = finished) }
                        }
                    }
                }.forEach { it.join() }
            }

            // Persist the final full ranking; the live view was already kept
            // up to date one measurement at a time.
            servers = servers
                .map { it.copy(delayMs = results[it.key] ?: NO_DELAY) }
                .sorted()
            if (context != null) save(context)
            publish()
            Log.i(TAG, "probe finished: ${results.size}/${pool.size} reachable")
            return results.isNotEmpty()
        } finally {
            testing = false
            AppState.updateChain { it.copy(tested = -1, testTotal = -1) }
        }
    }

    private val listLock = Any()

    /** Writes one measurement and re-publishes, so the sheet re-sorts live. */
    private fun applyDelay(key: String, delayMs: Long) {
        synchronized(listLock) {
            servers = servers
                .map { if (it.key == key) it.copy(delayMs = delayMs) else it }
                .sorted()
            publish()
        }
    }

    /**
     * One full SSTP negotiation against a single server. Returns the measured
     * handshake time in milliseconds, or null when the server refused, timed
     * out or failed anywhere between TCP and IPCP.
     */
    private suspend fun probeServer(server: Server): Long? {
        val negotiated = CompletableDeferred<Unit>()
        val ended = CompletableDeferred<Unit>()

        val params = SstpParams(
            mode = SstpMode.PROBE,
            hostname = server.host,
            port = server.port,
            proxyHostname = "127.0.0.1",
            proxyPort = Config.proxyPort,
            username = "vpn",
            password = "vpn",
            doVerifyCert = Config.chainVerifyTlsCert
        )
        val sessionScope = kotlinx.coroutines.CoroutineScope(
            kotlinx.coroutines.SupervisorJob() + Dispatchers.IO
        )
        val bridge = SharedBridge(
            params = params,
            scope = sessionScope,
            builder = null,
            protect = { true },
            log = { line -> Log.d(TAG, "probe ${server.key}: $line") },
            onSessionEnded = { ended.complete(Unit) },
            onNegotiated = { negotiated.complete(Unit) }
        )

        // The clock starts before the first byte: the TCP+SOCKS dial is part of
        // what the user would feel picking this server.
        val startedAt = android.os.SystemClock.elapsedRealtime()
        val controller = Controller(bridge).also {
            it.launchJobMain()
        }

        try {
            val success = withTimeoutOrNull(PROBE_TIMEOUT_MS) {
                select<Unit> {
                    negotiated.onAwait { }
                    ended.onAwait { }
                }
                negotiated.isCompleted
            } ?: false

            if (success) {
                // Close the probe politely so VPN Gate frees the call slot, and
                // wait - bounded - until the teardown confirms it ended.
                controller.disconnect()
                withTimeoutOrNull(10_000L) { ended.await() }
                return (android.os.SystemClock.elapsedRealtime() - startedAt).toInt().toLong()
            }

            // No negotiation: whatever is still running must stop. Failures
            // normally killed themselves; a raw timeout did not.
            controller.kill(null)
            withTimeoutOrNull(5_000L) { ended.await() }
            return null
        } finally {
            sessionScope.cancel()
        }
    }

    // -------------------------------------------------------------- URL test

    /** The request the live chain is measured with. */
    const val DEFAULT_TEST_URL = "http://connectivitycheck.gstatic.com/generate_204"

    private const val URL_TIMEOUT_MS = 10_000

    /**
     * The delay the *live* chain adds, measured with an actual HTTP fetch.
     *
     * The socket has to travel through the chain's TUN, but the app's own
     * package is disallowed there (it has to be, for Tor underneath), so the
     * fetch is bound instead to the TRANSPORT_VPN network the chain interface
     * created: [ConnectivityManager.getAllNetworks] exposes it, and
     * [android.net.Network.openConnection] sends this one request through it.
     * Returns -1 when no such network exists or the fetch failed.
     */
    suspend fun measureChainUrl(): Int {
        val context = appContext ?: return -1
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
            ?: return -1

        val vpnNetwork = connectivity.allNetworks.firstOrNull { network ->
            val caps = connectivity.getNetworkCapabilities(network)
            caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        } ?: return -1

        return withContext(Dispatchers.IO) {
            val startedAt = android.os.SystemClock.elapsedRealtime()
            try {
                val connection = vpnNetwork.openConnection(URL(Config.chainTestUrl))
                connection.connectTimeout = URL_TIMEOUT_MS
                connection.readTimeout = URL_TIMEOUT_MS
                connection.getInputStream().use { stream ->
                    val buffer = ByteArray(4096)
                    while (stream.read(buffer) != -1) { /* drain */ }
                }
                (android.os.SystemClock.elapsedRealtime() - startedAt).toInt()
            } catch (e: Exception) {
                Log.w(TAG, "chain URL test failed: ${e.message}")
                -1
            }
        }
    }
}

/**
 * The daily auto-update of the VPN Gate pool. Runs even when the app is closed,
 * so the list the chain falls back on is never the memory of last week.
 */
class ProxyChainWorker(
    context: Context,
    params: androidx.work.WorkerParameters,
) : androidx.work.CoroutineWorker(context, params) {

    override suspend fun doWork(): androidx.work.ListenableWorker.Result {
        return try {
            ProxyChainManager.updateIfNeeded(applicationContext)
            androidx.work.ListenableWorker.Result.success()
        } catch (e: Exception) {
            Log.w(TAG, "worker update failed: ${e.message}")
            if (runAttemptCount < 3) {
                androidx.work.ListenableWorker.Result.retry()
            } else {
                androidx.work.ListenableWorker.Result.success()
            }
        }
    }
}

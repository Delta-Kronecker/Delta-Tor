package io.deltator.tunnel

import android.net.VpnService
import io.deltator.AppState
import io.deltator.Config
import io.deltator.ProxyChainManager
import io.deltator.TorVpnService
import io.deltator.util.AppLog as Log
import kittoku.osc.SharedBridge
import kittoku.osc.SstpMode
import kittoku.osc.SstpParams
import kittoku.osc.control.Controller
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.select
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean


private const val TAG = "Chain"

/** VPN Gate's Virtual Hub "VPN" gives these credentials to everybody. */
private const val VPN_GATE_USER = "vpn"
private const val VPN_GATE_PASS = "vpn"

/** The same server gets three consecutive chances before the next one is tried. */
private const val RETRIES_PER_HOST = 3

/** Space between the attempts of one server: a Tor circuit change breathes. */
private const val RETRY_DELAY_MS = 4_000L

/** Next-best servers probed before the chain gives up and hands back to Tor. */
private const val FALLBACK_SERVERS = 2

/**
 * The Proxy Chain: Tor is still alive underneath its SOCKS bridge, and on top
 * of that a tunneled connection lands on a VPN Gate server over SSTP, so the
 * device traffic leaves the far end of the chain:
 *
 *     apps → [chain TUN] → SSTP/TLS → SOCKS5 → Tor → VPN Gate → internet
 *
 * The tunnel itself is the vendored kittoku core ([Controller] with a fresh
 * [VpnService.Builder]); DNS comes from IPCP or a public fallback, and the app's
 * own package is excluded from the TUN so Tor's own outbound links never loop
 * back into the tunnel they sit under.
 *
 * Failure policy, in order: reconnect the same server [RETRIES_PER_HOST] times,
 * then try the next best pinged server, then restore the plain Tor VPN — done
 * by [TorVpnService.onChainExhausted].
 */
class SstpChainEngine(private val service: TorVpnService) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null

    @Volatile
    private var controller: Controller? = null

    private val running = AtomicBoolean(false)
    private val deliberate = AtomicBoolean(false)

    /** True while the chain owns the VPN interface (or is trying to). */
    val isActive: Boolean get() = running.get()

    fun start(host: String, port: Int) {
        if (!running.compareAndSet(false, true)) {
            Log.i(TAG, "chain start ignored: already running")
            return
        }
        deliberate.set(false)
        restoreOnEnd = true
        pendingSwitch = null

        setChain {
            it.copy(
                status = AppState.ChainStatus.CONNECTING,
                host = host,
                port = port,
                message = "",
                urlMs = -1,
                tested = -1,
                testTotal = -1
            )
        }

        job = scope.launch {
            try {
                runPolicy(host, port)
            } catch (e: Exception) {
                Log.e(TAG, "chain policy crashed", e)
                if (!deliberate.get()) {
                    service.onChainExhausted(e.message ?: "internal error")
                }
            } finally {
                val restore = deliberate.get() && restoreOnEnd && pendingSwitch == null
                val next = pendingSwitch
                pendingSwitch = null
                restoreOnEnd = true
                deliberate.set(false)
                running.set(false)
                if (restore) {
                    service.onChainStoppedByUser()
                } else if (next != null) {
                    // The old loop has fully ended; the new one starts from
                    // here without ever touching the Tor VPN underneath.
                    start(next.first, next.second)
                } else {
                    AppState.updateChain { AppState.ChainState() }
                }
            }
        }
    }

    /**
     * Whose exit the policy loop owes an answer to: the default is a restore of
     * the plain Tor VPN, switching server restores nothing, and a stop the
     * service initiated (STOP VPN) leaves the cores where they are silently.
     */
    @Volatile
    private var restoreOnEnd = true

    @Volatile
    private var pendingSwitch: Pair<String, Int>? = null

    fun switchTo(host: String, port: Int) {
        if (!running.get()) {
            start(host, port)
            return
        }
        pendingSwitch = host to port
        restoreOnEnd = false
        deliberate.set(true)
        setChain {
            it.copy(status = AppState.ChainStatus.STOPPING, host = host, port = port, message = "switching…")
        }
        controller?.disconnect()
    }

    /**
     * The service itself is taking the chain down (STOP VPN): no restore is
     * asked for, because whoever calls knows what the next state should be.
     */
    fun stopWithoutRestore() {
        if (!running.get()) return
        restoreOnEnd = false
        deliberate.set(true)
        setChain { it.copy(status = AppState.ChainStatus.STOPPING) }
        controller?.disconnect()
    }

    /** The user pulled the plug: disconnect properly (SSTP Call Disconnect). */
    fun stop() {
        if (!running.get()) return
        restoreOnEnd = true
        deliberate.set(true)
        setChain { it.copy(status = AppState.ChainStatus.STOPPING) }
        controller?.disconnect()
    }

    /** The whole service is going down; no politebye message is needed. */
    fun shutdown() {
        deliberate.set(true)
        try {
            controller?.kill(null)
        } catch (_: Exception) {
        }
        job?.cancel()
        running.set(false)
        scope.cancel()
        AppState.updateChain { AppState.ChainState() }
    }

    private suspend fun runPolicy(host: String, port: Int) {
        val candidates = buildCandidates(host, port)
        var reason = "no attempt succeeded"

        outer@ for (candidate in candidates) {
            if (deliberate.get()) return

            for (attempt in 1..RETRIES_PER_HOST) {
                if (deliberate.get()) return

                val message = if (attempt == 1) "" else "attempt $attempt/$RETRIES_PER_HOST"
                setChain {
                    it.copy(status = AppState.ChainStatus.CONNECTING, host = candidate.first, port = candidate.second, message = message)
                }
                Log.i(TAG, "chain attempt ${candidate.first}:${candidate.second} $message")

                val outcome = runSession(candidate.first, candidate.second)

                if (outcome.negotiated && deliberate.get()) {
                    // A stop arrived while the tunnel was already up.
                    return
                }

                if (outcome.negotiated) {
                    // The tunnel lived, then dropped: count as one used attempt.
                    reason = outcome.log.ifBlank { "tunnel dropped" }
                    Log.i(TAG, "chain drop: $reason")
                } else {
                    reason = outcome.log
                    Log.w(TAG, "chain fail: $reason")
                }

                if (deliberate.get()) return
                delay(RETRY_DELAY_MS)
            }

            setChain { it.copy(message = "next server…") }
        }

        if (deliberate.get()) return
        service.onChainExhausted(reason)
    }

    /**
     * The preferred server first, then up to [FALLBACK_SERVERS] servers ranked
     * by their last measured delay.
     */
    private fun buildCandidates(host: String, port: Int): List<Pair<String, Int>> {
        val list = mutableListOf(host to port)
        if (Config.chainAutoFallback) {
            ProxyChainManager.bestServers("$host:$port", FALLBACK_SERVERS).forEach {
                list.add(it.host to it.port)
            }
        }
        return list
    }

    private class Outcome(val negotiated: Boolean, val log: String)

    /**
     * One full session lifecycle. Returns once the negotiation either succeeded
     * and the tunnel later ended, or failed never having carried a packet.
     */
    private suspend fun runSession(host: String, port: Int): Outcome {
        val negotiated = CompletableDeferred<Unit>()
        val ended = CompletableDeferred<String>()
        var lastLog = ""

        val sessionScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val bridge = SharedBridge(
            params = SstpParams(
                mode = SstpMode.RUN,
                hostname = host,
                port = port,
                proxyHostname = "127.0.0.1",
                proxyPort = Config.proxyPort,
                username = VPN_GATE_USER,
                password = VPN_GATE_PASS,
                doVerifyCert = Config.chainVerifyTlsCert,
                ownPackageName = service.packageName
            ),
            scope = sessionScope,
            builder = service.chainBuilderForSstp(),
            protect = { socket ->
                // A null socket has nothing to exempt; the core never sends one.
                socket != null && service.protect(socket)
            },
            log = { line ->
                lastLog = line
                Log.i(TAG, "[$host:$port] $line")
            },
            onSessionEnded = { ended.complete(lastLog) },
            onNegotiated = { negotiated.complete(Unit) }
        )

        // launchJobMain is not a suspend call: the session starts immediately
        // on the bridge's own scope.
        Controller(bridge).also {
            it.launchJobMain()
            controller = it
        }

        try {
            // Whichever comes first: a working tunnel or the session's death.
            select<Unit> {
                negotiated.onAwait { }
                ended.onAwait { }
            }

            if (!negotiated.isCompleted) {
                return Outcome(false, ended.await().ifBlank { "no answer from $host:$port" })
            }

            setChain { it.copy(status = AppState.ChainStatus.CONNECTED, host = host, port = port, message = "") }
            service.onChainEstablished(host, port)

            // Hold until the tunnel drops for any reason (echo timeout, abort,
            // network loss), then report back to the retry policy.
            val dropLog = ended.await()
            return Outcome(true, dropLog)
        } finally {
            controller = null
            // kill() runs its cleanup on the session scope, and onSessionEnded
            // fires after the terminals are closed, so ending first is safe.
            sessionScope.cancel()
        }
    }

    private fun setChain(block: (AppState.ChainState) -> AppState.ChainState) {
        AppState.updateChain(block)
        service.onChainStateChanged()
    }
}

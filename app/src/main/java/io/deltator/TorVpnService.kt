package io.deltator

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import io.deltator.tunnel.BridgeCountries
import io.deltator.tunnel.BridgeMemory
import io.deltator.tunnel.ExitLocator
import io.deltator.tunnel.ExitNodes
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.tunnel.ParallelTorManager
import io.deltator.tunnel.TorRunner
import io.deltator.tunnel.TorSocksBridge
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

class TorVpnService : VpnService() {

    companion object {
        const val ACTION_CONNECT = "io.deltator.CONNECT"
        const val ACTION_DISCONNECT = "io.deltator.DISCONNECT"
        const val ACTION_START_VPN = "io.deltator.START_VPN"
        const val ACTION_STOP_VPN = "io.deltator.STOP_VPN"
        const val CHANNEL_VPN_STATUS = "vpn_status"
        const val CHANNEL_UPDATES = "deltator_updates"
        const val NOTIFICATION_ID = 1

        private const val TAG = "TorVpnService"
        private const val VPN_MTU = 1280
        private const val VPN_ADDRESS = "10.255.255.1"
        private const val VPN_ROUTE = "0.0.0.0"
        private const val DEFAULT_DNS = "8.8.8.8"

        /** How often the connected tunnel is asked whether Tor can still carry traffic. */
        private const val PROBE_INTERVAL_MS = 5_000L

        /** The same check while nothing is wrong, to keep the request itself rare. */
        private const val PROBE_INTERVAL_HEALTHY_MS = 20_000L

        /**
         * Consecutive failed probes before the transport is rebuilt. Two is one
         * full interval of grace: long enough that a single dropped request does
         * not throw away a working circuit, short enough that a real outage is
         * noticed while the user is still looking at the screen.
         */
        private const val PROBES_BEFORE_RECOVERY = 2

        /** Upper bound on one liveness probe; a dead Tor would otherwise hang it. */
        private const val PROBE_TIMEOUT_MS = 6_000

        /**
         * How long the UI stays in STOPPING after the cores are gone. Long enough
         * that the phase is actually seen instead of flashing past, and it also
         * covers the teardown itself when that is the slower of the two.
         */
        private const val STOPPING_MIN_MS = 3_000L
    }

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var vpnInterface: ParcelFileDescriptor? = null
    private var activeRunner: TorRunner? = null
    private var statsJob: Job? = null
    private var linkWatchJob: Job? = null
    private var exitLocatorJob: Job? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var wakeLock: PowerManager.WakeLock? = null

    /**
     * Liveness of the link, kept by a [ConnectivityManager.NetworkCallback] so a
     * dropped connection is known without waiting for a request to time out.
     */
    @Volatile private var linkUp = true
    private val recovering = AtomicBoolean(false)
    private var failedProbes = 0

    /** Id of the connect session whose lines this service emits. */
    @Volatile private var currentSession: Int = 0

    private val _notificationText = MutableStateFlow("")
    val notificationText = _notificationText.asStateFlow()

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> connect()
            ACTION_DISCONNECT -> disconnect()
            ACTION_START_VPN -> startVpn()
            ACTION_STOP_VPN -> stopVpn()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        // Teardown blocks until every Tor/lyrebird process has exited and its
        // ports are released, so it must never run on the main thread.
        Thread({ teardown() }, "deltator-teardown").apply { isDaemon = true }.start()
        super.onDestroy()
    }

    private fun connect() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring connect: a stop is still tearing the cores down")
            return
        }
        if (AppState.vpnStarted) {
            Log.i(TAG, "Already running")
            return
        }
        AppState.markStarted()
        currentSession = Log.beginSession("connect")
        AppState.update { it.copy(connecting = true, connected = false, reconnecting = false, torRunning = false, error = null, transports = emptyMap(), transport = "") }

        startForeground(NOTIFICATION_ID, buildNotification("Connecting\u2026", progress = true, progressValue = 0))

        // Keep CPU alive during bootstrap on some OEM ROMs
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DeltaTor:vpn").apply {
            setReferenceCounted(false)
            acquire(10 * 60 * 1000L)
        }

        serviceScope.launch {
            try {
                runConnectFlow()
            } catch (e: Exception) {
                Log.e(TAG, "Connect failed", e)
                fail("Connect failed: ${e.message}")
            }
        }
    }

    private suspend fun runConnectFlow() {
        val proxyPort = Config.proxyPort
        val proxyHost = "127.0.0.1"

        TorSocksBridge.debugLogging = Config.debugMode
        TorSocksBridge.domainRouter = io.deltator.tunnel.DomainRouter.DISABLED

        // Step 1: Resolve the bridge lists for the selected mode and start its
        // runner(s). In auto mode vanilla / obfs4 / webtunnel race together, plus
        // a fourth "memory" runner on the bridges that provably worked last time.
        // Monitor each transport's bootstrap progress; the first to reach 100%
        // wins and the losing transports are stopped by the manager.
        val mode = ParallelTorManager.MODES
            .firstOrNull { it == Config.transportMode }
            ?: ParallelTorManager.TRANSPORT_AUTO
        val autoNames = Config.autoTransports
            .filter { it in ParallelTorManager.BRIDGE_SOURCES }
            .ifEmpty { ParallelTorManager.BRIDGE_SOURCES.keys.toList() }
        val modeLabel = if (mode == ParallelTorManager.TRANSPORT_AUTO) {
            autoNames.joinToString(" / ") +
                if (BridgeMemory.countAll(applicationContext) > 0) " / memory" else ""
        } else {
            // A single transport also races its own memory twin whenever that pool
            // has proven bridges, so say so instead of surprising the user later.
            mode + if (BridgeMemory.count(applicationContext, mode) > 0) " + memory" else ""
        }
        Log.i(TAG, "Transport mode: $modeLabel")
        updateNotification("Connecting via $modeLabel \u2026", progress = true, progressValue = 0)

        val w = try {
            ParallelTorManager.race(
                context = applicationContext,
                basePort = proxyPort,
                sessionId = currentSession,
                transportMode = Config.transportMode,
                customBridges = Config.customBridges,
                autoTransports = Config.autoTransports
            ) { snapshot ->
                val progress = snapshot.mapValues { (name, r) -> if (r.failed != null) -1 else r.progress() }
                AppState.update { it.copy(transports = progress) }
                val maxProg = (progress.values.maxOrNull() ?: 0).coerceAtLeast(0)
                val detail = progress.entries.joinToString("  ") { (n, p) ->
                    "$n=${if (p < 0) "FAIL" else "$p%"}"
                }
                updateNotification(detail, progress = true, progressValue = maxProg)
            }
        } catch (e: Exception) {
            fail(e.message ?: "All transports failed to bootstrap")
            return
        }
        activeRunner = w

        AppState.update {
            it.copy(transports = mapOf(w.name to 100), transport = w.name)
        }
        Log.i(TAG, "Winner transport: ${w.name} (SOCKS5 $proxyHost:${w.torSocksPort})")

        // Step 2: Start the SOCKS5 bridge between TUN and the winning Tor instance.
        // The bridge and Tor stay alive across VPN stop/start.
        // The losing runners were killed by race(); their processes are gone by
        // now, but wait for the kernel to release the ports anyway so the bind
        // below cannot lose a race against a dying socket.
        withContext(Dispatchers.IO) {
            ParallelTorManager.awaitPortFree(proxyHost, proxyPort)
        }
        val bridgeResult = TorSocksBridge.start(
            torSocksPort = w.torSocksPort,
            torHost = "127.0.0.1",
            listenPort = proxyPort,
            listenHost = proxyHost
        )
        if (bridgeResult.isFailure) {
            fail(bridgeResult.exceptionOrNull()?.message ?: "Failed to start bridge")
            return
        }
        AppState.update { it.copy(torRunning = true) }

        // Step 3+4: TUN interface + tun2socks
        establishTunnel(w, proxyHost, proxyPort)
    }

    /** Establish the TUN interface and tun2socks on top of the running Tor engine. */
    private suspend fun establishTunnel(w: TorRunner, proxyHost: String, proxyPort: Int) {
        updateNotification("Establishing VPN \u2026", progress = true, progressValue = 0)

        vpnInterface = establishVpnInterface()
        if (vpnInterface == null) {
            fail("Failed to establish VPN interface")
            return
        }

        delay(200)

        val tunResult = HevSocks5Tunnel.start(
            tunFd = vpnInterface!!,
            socksAddress = proxyHost,
            socksPort = proxyPort,
            enableUdpTunneling = true,
            mtu = VPN_MTU,
            ipv4Address = VPN_ADDRESS
        )
        if (tunResult.isFailure) {
            fail(tunResult.exceptionOrNull()?.message ?: "Failed to start tunnel")
            return
        }

        AppState.update {
            it.copy(
                connecting = false,
                connected = true,
                reconnecting = false,
                error = null,
                transports = mapOf(w.name to 100),
                connectedAtMillis = System.currentTimeMillis(),
                exitCode = "",
                exitName = "",
                exitIp = ""
            )
        }
        startForeground(NOTIFICATION_ID, buildNotification("Connected via ${w.name} \u00b7 Tor Network", progress = false))
        startStatsPolling()
        startLinkWatch()
        startExitLocator(proxyHost, proxyPort)
        Log.i(TAG, "DeltaTor connected. Winner: ${w.name}, SOCKS5 at $proxyHost:$proxyPort")
        Log.endSession("connected via ${w.name}")
    }

    /** Re-establish the VPN on top of an already-running Tor engine. */
    private fun startVpn() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring start: a stop is still tearing the cores down")
            return
        }
        if (AppState.state.value.connected) {
            Log.i(TAG, "VPN already active")
            return
        }
        val w = activeRunner
        if (w == null || !w.isReady() || !TorSocksBridge.isRunning()) {
            Log.e(TAG, "Tor not running; full reconnect required")
            fail("Tor is not running. Reconnect.")
            return
        }
        serviceScope.launch {
            try {
                AppState.update { it.copy(connecting = true, connected = false, error = null) }
                establishTunnel(w, "127.0.0.1", Config.proxyPort)
            } catch (e: Exception) {
                Log.e(TAG, "Start VPN failed", e)
                fail("Start VPN failed: ${e.message}")
            }
        }
    }

    /** Tear down the VPN (TUN + tunnel) and every Tor core, with a real stop phase. */
    private fun stopVpn() {
        val s = AppState.state.value
        if (s.stopping) {
            Log.i(TAG, "A stop is already running")
            return
        }
        if (!s.connected && !s.connecting && !s.reconnecting && !s.torRunning) {
            Log.i(TAG, "Nothing to stop")
            return
        }
        val startedAt = SystemClock.elapsedRealtime()
        Log.i(TAG, "Stopping: TUN, tunnel and every Tor core")
        AppState.update {
            it.copy(
                stopping = true,
                connecting = false,
                connected = false,
                reconnecting = false,
                error = null
            )
        }
        updateNotification("Stopping \u00b7 killing every Tor core", progress = true, progressValue = 0)
        serviceScope.launch {
            // Blocks until every Tor/lyrebird process has exited and every port
            // is free, so the cores are provably down before the flag clears.
            teardown()
            val left = STOPPING_MIN_MS - (SystemClock.elapsedRealtime() - startedAt)
            if (left > 0) {
                Log.i(TAG, "cores are gone, holding STOPPING for another ${left}ms")
                delay(left)
            }
            Log.endSession("stopped by user \u00b7 cores down")
            Log.i(TAG, "stopped: no Tor core is left running")
            AppState.update { it.copy(stopping = false, torRunning = false) }
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun establishVpnInterface(): ParcelFileDescriptor? {
        return try {
            val builder = Builder()
                .setSession("DeltaTor")
                .setMtu(VPN_MTU)
                .addAddress(VPN_ADDRESS, 32)
                .addDnsServer(DEFAULT_DNS)
                .addRoute(VPN_ROUTE, 0)
            // Exclude our own app so Tor/Snowflake sockets go direct (not through TUN)
            try {
                builder.addDisallowedApplication(packageName)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to exclude self from VPN", e)
            }
            builder.setBlocking(false)
            builder.establish()
        } catch (e: Exception) {
            Log.e(TAG, "establish() failed", e)
            null
        }
    }

    private fun startStatsPolling() {
        statsJob?.cancel()
        statsJob = serviceScope.launch {
            var lastTx = 0L
            var lastRx = 0L
            var lastTime = 0L
            while (isActive) {
                val stats = HevSocks5Tunnel.getStats()
                if (stats != null) {
                    val now = System.currentTimeMillis()
                    val dtSeconds = ((now - lastTime).coerceAtLeast(1000) / 1000f).coerceAtLeast(0.001f)
                    val upSpeed = if (lastTime > 0) (stats.txBytes - lastTx).toFloat() / dtSeconds else 0f
                    val downSpeed = if (lastTime > 0) (stats.rxBytes - lastRx).toFloat() / dtSeconds else 0f
                    lastTx = stats.txBytes
                    lastRx = stats.rxBytes
                    lastTime = now

                    AppState.update {
                        it.copy(
                            txBytes = stats.txBytes,
                            rxBytes = stats.rxBytes,
                            txSpeed = upSpeed,
                            rxSpeed = downSpeed
                        )
                    }
                    val notif = NotificationCompat.Builder(this@TorVpnService, CHANNEL_VPN_STATUS)
                        .setSmallIcon(R.drawable.ic_tor)
                        .setContentTitle(
                            if (AppState.state.value.reconnecting) "DeltaTor \u2014 Reconnecting"
                            else "DeltaTor \u2014 Connected"
                        )
                        .setContentText(
                            if (AppState.state.value.reconnecting) "Restoring the tunnel\u2026"
                            else "\u2191 ${formatBytes(upSpeed)}/s  \u2193 ${formatBytes(downSpeed)}/s\n" +
                                "Total: \u2191 ${formatBytes(stats.txBytes)}  \u2193 ${formatBytes(stats.rxBytes)}"
                        )
                        .setStyle(NotificationCompat.BigTextStyle())
                        .setContentIntent(mainPendingIntent())
                        .setOngoing(true)
                        .setOnlyAlertOnce(true)
                        .setCategory(NotificationCompat.CATEGORY_SERVICE)
                        .setPriority(NotificationCompat.PRIORITY_LOW)
                        .addAction(0, "Stop VPN", stopVpnPendingIntent())
                        .addAction(0, "Disconnect", disconnectPendingIntent())
                        .build()
                    getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notif)
                }
                delay(1000)
            }
        }
    }

    // --- Link loss and recovery ------------------------------------------------

    /**
     * Watch the link while the tunnel is up.
     *
     * A dropped connection used to be invisible: the Tor process keeps printing
     * 100%, the interface keeps claiming to be connected, and the wait for Tor to
     * retry its guard connections on its own schedule was as long as Tor decided
     * it should be. This loop asks the tunnel every few seconds whether Tor can
     * still complete a request, and repairs it when the answer stays no.
     */
    private fun startLinkWatch() {
        registerNetworkCallback()
        linkWatchJob?.cancel()
        linkWatchJob = serviceScope.launch {
            while (isActive) {
                val before = AppState.state.value
                // A healthy tunnel is only checked now and then, since the probe
                // is a real (if tiny) request through Tor; a link that is down or
                // already failing is watched closely.
                val urgent = before.reconnecting || !linkUp || failedProbes > 0
                delay(if (urgent) PROBE_INTERVAL_MS else PROBE_INTERVAL_HEALTHY_MS)

                val state = AppState.state.value
                if (!state.connected || recovering.get()) continue

                if (!linkUp) {
                    // Nothing can pass until the link is back; only say so.
                    if (!state.reconnecting) markReconnecting("network down")
                    continue
                }

                if (probeTunnelUsable()) {
                    if (state.reconnecting || failedProbes > 0) {
                        Log.i(TAG, "Tunnel usable again after $failedProbes failed probe(s)")
                        clearReconnecting()
                    }
                    failedProbes = 0
                    continue
                }

                failedProbes++
                Log.w(TAG, "Tunnel probe failed ($failedProbes/$PROBES_BEFORE_RECOVERY)")
                if (failedProbes >= PROBES_BEFORE_RECOVERY) {
                    failedProbes = 0
                    recoverTransport()
                }
            }
        }
    }

    /**
     * Ask the live tunnel whether Tor can still complete a request.
     *
     * A SOCKS5 CONNECT to a fixed address is the honest test: the reply only
     * turns into 0x00 once Tor has a built circuit, and the socket is closed
     * right after, so a probe costs one circuit check and no payload. Bootstrap
     * percentage cannot be used for this, it is printed once and never revoked
     * when the network disappears.
     */
    private fun probeTunnelUsable(): Boolean {
        return try {
            Socket().use { s ->
                s.connect(InetSocketAddress("127.0.0.1", Config.proxyPort), PROBE_TIMEOUT_MS)
                s.soTimeout = PROBE_TIMEOUT_MS
                val out = s.getOutputStream()
                val input = s.getInputStream()
                out.write(byteArrayOf(0x05, 0x01, 0x00)) // greeting, no auth
                out.flush()
                if (input.read() != 0x05 || input.read() != 0x00) return false
                // CONNECT 1.1.1.1:80, then close without sending a byte.
                out.write(byteArrayOf(0x05, 0x01, 0x00, 0x01, 1, 1, 1, 1, 0x00, 0x50))
                out.flush()
                input.read() == 0x00
            }
        } catch (e: Exception) {
            Log.d(TAG, "probe failed: ${e.message}")
            false
        }
    }

    /**
     * Rebuild the transport that was carrying traffic, keeping the tunnel up.
     *
     * The replacement runs on the same port with the same bridge lines, so the
     * app's SOCKS bridge only needs a repoint and every app connection survives
     * the swap; nothing on the TUN side is torn down. If even that cannot
     * bootstrap, the ordinary connect flow takes over, which is slower but
     * re-races every transport from scratch.
     */
    private suspend fun recoverTransport() {
        if (!recovering.compareAndSet(false, true)) return
        val startedAt = System.currentTimeMillis()
        try {
            val previous = activeRunner
            if (previous == null) {
                escalateToFullReconnect("no active transport")
                return
            }
            markReconnecting("rebuilding ${previous.name}")
            val winner = try {
                ParallelTorManager.restartTransport(
                    context = applicationContext,
                    basePort = Config.proxyPort,
                    sessionId = currentSession,
                    name = previous.name
                ) { snapshot ->
                    AppState.update {
                        it.copy(
                            transports = snapshot.mapValues { (n, r) ->
                                if (r.failed != null) -1 else r.progress()
                            }
                        )
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Transport rebuild failed: ${e.message}")
                escalateToFullReconnect(e.message ?: "transport rebuild failed")
                return
            }

            TorSocksBridge.repoint(winner.torSocksPort)
            activeRunner = winner
            AppState.update {
                it.copy(
                    transports = mapOf(winner.name to 100),
                    transport = winner.name,
                    reconnecting = false
                )
            }
            val seconds = (System.currentTimeMillis() - startedAt) / 1000
            Log.i(TAG, "Recovered via ${winner.name} in ${seconds}s (SOCKS5 ${winner.torSocksPort})")
            startForeground(
                NOTIFICATION_ID,
                buildNotification("Reconnected via ${winner.name} \u00b7 ${seconds}s", progress = false)
            )
            // The new Tor has its own exit circuits, so the reported country is
            // stale until it is looked up again.
            startExitLocator("127.0.0.1", Config.proxyPort)
        } catch (e: Exception) {
            Log.e(TAG, "Recovery failed", e)
            escalateToFullReconnect(e.message ?: "recovery failed")
        } finally {
            recovering.set(false)
        }
    }

    /**
     * Hand over to the normal connect flow. The teardown runs in a fresh job so
     * the cancellation inside [teardown] cannot abort the handover itself.
     */
    private fun escalateToFullReconnect(reason: String) {
        Log.w(TAG, "Falling back to a full reconnect ($reason)")
        markReconnecting("reconnecting")
        serviceScope.launch {
            teardown()
            connect()
        }
    }

    private fun markReconnecting(reason: String) {
        Log.w(TAG, "Reconnecting: $reason")
        AppState.update { it.copy(reconnecting = true) }
        updateNotification("Reconnecting\u2026", progress = false, progressValue = 0)
    }

    private fun clearReconnecting() {
        AppState.update { it.copy(reconnecting = false) }
    }

    private fun registerNetworkCallback() {
        if (networkCallback != null) return
        val cm = getSystemService(CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onLost(network: Network) {
                // Losing one network is routine when the system moves between
                // Wi-Fi and cellular, so only a real absence of any counts.
                if (cm.activeNetwork != null) return
                linkUp = false
                Log.w(TAG, "Network lost")
                if (AppState.state.value.connected) markReconnecting("network down")
            }

            override fun onAvailable(network: Network) {
                if (linkUp) return
                linkUp = true
                failedProbes = 0
                Log.i(TAG, "Network available again")
            }
        }
        try {
            cm.registerDefaultNetworkCallback(callback)
            networkCallback = callback
            linkUp = cm.activeNetwork != null
        } catch (e: Exception) {
            Log.w(TAG, "Network callback unavailable: ${e.message}")
        }
    }

    private fun unregisterNetworkCallback() {
        val callback = networkCallback ?: return
        networkCallback = null
        try {
            (getSystemService(CONNECTIVITY_SERVICE) as? ConnectivityManager)
                ?.unregisterNetworkCallback(callback)
        } catch (e: Exception) {
            Log.d(TAG, "unregisterNetworkCallback: ${e.message}")
        }
    }

    private fun formatBytes(bytes: Long): String {
        if (bytes < 1) return "0 B"
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        var v = bytes.toFloat()
        var idx = 0
        while (v >= 1024 && idx < units.size - 1) {
            v /= 1024f
            idx++
        }
        return if (idx == 0) "${v.toInt()} ${units[idx]}" else String.format("%.1f %s", v, units[idx])
    }

    private fun formatBytes(bytes: Float): String {
        return formatBytes(bytes.toLong())
    }

    private fun startExitLocator(proxyHost: String, proxyPort: Int) {
        // Every recovery starts the locator again, so the previous run is replaced
        // rather than left to finish: otherwise N recoveries mean N loops of up to
        // six 8s probes competing for the circuit that was just rebuilt.
        exitLocatorJob?.cancel()
        exitLocatorJob = serviceScope.launch {
            val selected = ExitNodes.currentCodes()
                .map { it.trim().uppercase() }
                .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
                .distinct()
            try {
                if (selected.isNotEmpty()) {
                    // The very first circuit of the session is the fast, unrestricted
                    // one; the post-bootstrap SETCONF + NEWNYM needs a moment to steer
                    // the next circuits into the selected countries before the reported
                    // location can match the choice.
                    Log.i("ExitNode", "waiting for live exit switch before locating (selected: ${selected.joinToString(",")})")
                    delay(5_000)
                }
                val tries = if (selected.isEmpty()) 1 else 6
                for (attempt in 1..tries) {
                    val info = ExitLocator.locate(this@TorVpnService, proxyHost, proxyPort, timeoutMs = 8_000)
                    if (info == null) {
                        Log.w("ExitNode", "location probe attempt $attempt/$tries failed (no traffic yet?)")
                    } else {
                        AppState.update {
                            it.copy(
                                exitIp = info.ip,
                                exitCode = info.countryCode,
                                exitName = info.countryName.ifBlank { info.city }
                            )
                        }
                        val match = info.countryCode.uppercase() in selected
                        Log.i(
                            "ExitNode",
                            "located ${info.ip} \u00b7 ${info.label()}" +
                                (if (selected.isEmpty()) "" else " \u00b7 ${if (match) "MATCHES ${selected.joinToString(",")}" else "NOT yet ${selected.joinToString(",")}"}") +
                                (if (info.asn.isNotBlank()) " \u00b7 ${info.asn}" else "")
                        )
                        if (selected.isEmpty() || match) return@launch
                    }
                    if (attempt < tries) delay(8_000)
                }
                if (selected.isNotEmpty()) {
                    Log.w("ExitNode", "did not observe a ${selected.joinToString(",")} exit after $tries tries; connection still up")
                }
            } catch (e: Exception) {
                Log.w("ExitNode", "exit locator failed: ${e.message}")
            }
        }
    }

    private fun fail(message: String) {
        Log.e(TAG, message)
        Log.endSession("failed \u00b7 $message")
        AppState.update { it.copy(connecting = false, connected = false, reconnecting = false, torRunning = false, stopping = false, error = message) }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        AppState.markStopped()
        teardown()
    }

    private fun disconnect() {
        Log.i(TAG, "Disconnecting...")
        Log.endSession("disconnected by user")
        serviceScope.launch {
            AppState.update { it.copy(connecting = false, connected = false, stopping = false, error = null) }
            teardown()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun teardown() {
        statsJob?.cancel()
        statsJob = null
        linkWatchJob?.cancel()
        linkWatchJob = null
        exitLocatorJob?.cancel()
        exitLocatorJob = null
        unregisterNetworkCallback()
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { vpnInterface?.close() } catch (_: Exception) {}
        vpnInterface = null
        try { TorSocksBridge.stop() } catch (_: Exception) {}
        // Blocks until every Tor/lyrebird process is really gone and the ports are
        // released, so a connect right after a stop cannot hit EADDRINUSE.
        try { ParallelTorManager.stopAllAndWait(Config.proxyPort) } catch (_: Exception) {}
        activeRunner = null
        try { wakeLock?.release() } catch (_: Exception) {}
        wakeLock = null
        if (AppState.vpnStarted) {
            AppState.markStopped()
        }
    }

    private fun buildNotification(text: String, progress: Boolean, progressValue: Int = 0): Notification {
        return NotificationCompat.Builder(this, CHANNEL_VPN_STATUS)
            .setSmallIcon(R.drawable.ic_tor)
            .setContentTitle("DeltaTor")
            .setContentText(text)
            .setContentIntent(mainPendingIntent())
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setProgress(100, progressValue, progress)
            .addAction(0, "Disconnect", disconnectPendingIntent())
            .build()
    }

    private fun updateNotification(text: String, progress: Boolean, progressValue: Int) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(text, progress, progressValue))
    }

    private fun mainPendingIntent(): PendingIntent {
        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        return PendingIntent.getActivity(
            this,
            1,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private fun disconnectPendingIntent(): PendingIntent {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = ACTION_DISCONNECT
        }
        return PendingIntent.getService(
            this,
            2,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private fun stopVpnPendingIntent(): PendingIntent {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = ACTION_STOP_VPN
        }
        return PendingIntent.getService(
            this,
            3,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }
}
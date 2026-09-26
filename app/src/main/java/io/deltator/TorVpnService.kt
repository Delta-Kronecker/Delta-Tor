package io.deltator

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import io.deltator.tunnel.BridgeCountries
import io.deltator.tunnel.BridgeMemory
import io.deltator.tunnel.ExitLocator
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
    }

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var vpnInterface: ParcelFileDescriptor? = null
    private var activeRunner: TorRunner? = null
    private var statsJob: Job? = null
    private var wakeLock: PowerManager.WakeLock? = null

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
        teardown()
        super.onDestroy()
    }

    private fun connect() {
        if (AppState.vpnStarted) {
            Log.i(TAG, "Already running")
            return
        }
        AppState.markStarted()
        currentSession = Log.beginSession("connect")
        AppState.update { it.copy(connecting = true, connected = false, torRunning = false, error = null, transports = emptyMap(), transport = "") }

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

        // Step 1: Fetch bridge lists and race vanilla / obfs4 / webtunnel, plus a
        // fourth "memory" runner on the bridges that provably worked last time.
        // Monitor each transport's bootstrap progress; the first to reach 100%
        // wins and the losing transports are stopped by the manager.
        Log.i(TAG, "Racing vanilla / obfs4 / webtunnel${if (BridgeMemory.countAll(applicationContext) > 0) " / memory" else ""} transports")
        updateNotification("Fetching bridges and racing transports \u2026", progress = true, progressValue = 0)

        val w = try {
            ParallelTorManager.race(applicationContext, basePort = proxyPort, sessionId = currentSession) { snapshot ->
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
        startExitLocator(proxyHost, proxyPort)
        Log.i(TAG, "DeltaTor connected. Winner: ${w.name}, SOCKS5 at $proxyHost:$proxyPort")
        Log.endSession("connected via ${w.name}")
    }

    /** Re-establish the VPN on top of an already-running Tor engine. */
    private fun startVpn() {
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

    /** Tear down only the VPN (TUN + tunnel); the Tor engine and bridge keep running. */
    private fun stopVpn() {
        if (!AppState.state.value.connected) {
            Log.i(TAG, "No VPN to stop")
            return
        }
        Log.i(TAG, "Stopping VPN; keeping Tor alive")
        statsJob?.cancel()
        statsJob = null
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { vpnInterface?.close() } catch (_: Exception) {}
        vpnInterface = null
        AppState.update { it.copy(connecting = false, connected = false, error = null) }
        startForeground(
            NOTIFICATION_ID,
            buildNotification("Tor ready \u00b7 VPN stopped \u00b7 SOCKS 127.0.0.1:${Config.proxyPort}", progress = false)
        )
        Log.i(TAG, "VPN stopped, Tor still running")
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
                        .setContentTitle("DeltaTor \u2014 Connected")
                        .setContentText(
                            "\u2191 ${formatBytes(upSpeed)}/s  \u2193 ${formatBytes(downSpeed)}/s\n" +
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
        serviceScope.launch {
            try {
                val info = ExitLocator.locate(this@TorVpnService, proxyHost, proxyPort)
                if (info == null) {
                    Log.w(TAG, "Exit location lookup failed")
                    return@launch
                }
                Log.i(
                    TAG,
                    "Exit located: ${info.ip} \u00b7 ${info.label()}" +
                        (if (info.asn.isNotBlank()) " \u00b7 ${info.asn}" else "") +
                        " (via ${if (info.fromNetwork) "ip2location" else "offline geoip"})"
                )
                AppState.update {
                    it.copy(
                        exitIp = info.ip,
                        exitCode = info.countryCode,
                        exitName = info.countryName.ifBlank { info.city }
                    )
                }
            } catch (e: Exception) {
                Log.w(TAG, "Exit locator failed: ${e.message}")
            }
        }
    }

    private fun fail(message: String) {
        Log.e(TAG, message)
        Log.endSession("failed \u00b7 $message")
        AppState.update { it.copy(connecting = false, connected = false, torRunning = false, error = message) }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        AppState.markStopped()
        teardown()
    }

    private fun disconnect() {
        Log.i(TAG, "Disconnecting...")
        Log.endSession("disconnected by user")
        serviceScope.launch {
            AppState.update { it.copy(connecting = false, connected = false) }
            teardown()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun teardown() {
        statsJob?.cancel()
        statsJob = null
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { vpnInterface?.close() } catch (_: Exception) {}
        vpnInterface = null
        try { TorSocksBridge.stop() } catch (_: Exception) {}
        try { ParallelTorManager.stopAll() } catch (_: Exception) {}
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
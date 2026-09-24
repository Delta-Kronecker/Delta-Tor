package io.deltator

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.tunnel.SnowflakeBridge
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
        const val CHANNEL_VPN_STATUS = "vpn_status"
        const val NOTIFICATION_ID = 1

        private const val TAG = "TorVpnService"
        private const val VPN_MTU = 1280
        private const val VPN_ADDRESS = "10.255.255.1"
        private const val VPN_ROUTE = "0.0.0.0"
        private const val DEFAULT_DNS = "8.8.8.8"
        private const val BOOTSTRAP_TIMEOUT_MS = 300_000L
        private const val BOOTSTRAP_POLL_MS = 1_000L
    }

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var vpnInterface: ParcelFileDescriptor? = null
    private var statsJob: Job? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private val _notificationText = MutableStateFlow("")
    val notificationText = _notificationText.asStateFlow()

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> connect()
            ACTION_DISCONNECT -> disconnect()
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
        AppState.update { it.copy(connecting = true, connected = false, error = null, bootstrapProgress = 0, transport = Config.transportLabel()) }

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
        val torSocksPort = proxyPort + 1
        val snowflakePtPort = proxyPort + 2

        TorSocksBridge.debugLogging = Config.debugMode
        TorSocksBridge.domainRouter = io.deltator.tunnel.DomainRouter.DISABLED

        // Step 1: Start Snowflake PT / lyrebird + Tor process
        Log.i(TAG, "Starting Tor with transport: ${Config.transportLabel()}")
        updateNotification("Connecting via ${Config.transportLabel()} \u2026", progress = true, progressValue = 0)
        val sfResult = SnowflakeBridge.startClient(
            context = this@TorVpnService,
            snowflakePort = snowflakePtPort,
            torSocksPort = torSocksPort,
            listenHost = proxyHost,
            bridgeLines = Config.bridgeLines()
        )
        if (sfResult.isFailure) {
            fail(sfResult.exceptionOrNull()?.message ?: "Failed to start Tor")
            return
        }

        // Step 2: Wait for Tor bootstrap
        val startTime = System.currentTimeMillis()
        while (System.currentTimeMillis() - startTime < BOOTSTRAP_TIMEOUT_MS) {
            if (SnowflakeBridge.isTorReady) break
            if (!SnowflakeBridge.isRunning()) {
                fail("Tor process died during bootstrap")
                return
            }
            AppState.update { it.copy(bootstrapProgress = SnowflakeBridge.torBootstrapProgress) }
            updateNotification(
                "Bootstrapping Tor (${SnowflakeBridge.torBootstrapProgress}%)",
                progress = true,
                progressValue = SnowflakeBridge.torBootstrapProgress
            )
            delay(BOOTSTRAP_POLL_MS)
        }
        if (!SnowflakeBridge.isTorReady) {
            fail("Tor failed to bootstrap (${SnowflakeBridge.torBootstrapProgress}%)")
            return
        }
        AppState.update { it.copy(bootstrapProgress = 100) }

        // Step 3: Start the SOCKS5 bridge between TUN and Tor
        val bridgeResult = TorSocksBridge.start(
            torSocksPort = torSocksPort,
            torHost = "127.0.0.1",
            listenPort = proxyPort,
            listenHost = proxyHost
        )
        if (bridgeResult.isFailure) {
            fail(bridgeResult.exceptionOrNull()?.message ?: "Failed to start bridge")
            return
        }

        // Step 4: Establish TUN interface
        vpnInterface = establishVpnInterface()
        if (vpnInterface == null) {
            fail("Failed to establish VPN interface")
            return
        }

        delay(200)

        // Step 5: Run tun2socks (hev-socks5-tunnel) pointing at the bridge
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

        // Connected!
        AppState.update { it.copy(connecting = false, connected = true, error = null, bootstrapProgress = 100) }
        startForeground(NOTIFICATION_ID, buildNotification("Connected \u00b7 Tor Network", progress = false))
        startStatsPolling()
        Log.i(TAG, "DeltaTor connected. SOCKS5 at $proxyHost:$proxyPort")
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
                        it.copy(txBytes = stats.txBytes, rxBytes = stats.rxBytes)
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

    private fun fail(message: String) {
        Log.e(TAG, message)
        AppState.update { it.copy(connecting = false, connected = false, error = message) }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        AppState.markStopped()
        teardown()
    }

    private fun disconnect() {
        Log.i(TAG, "Disconnecting...")
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
        try { SnowflakeBridge.stopClient() } catch (_: Exception) {}
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
}
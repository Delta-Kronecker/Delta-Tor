package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.core.dns.FakeDns
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.tunnel.ParallelTorManager
import io.deltator.tunnel.TorRunner
import io.deltator.tunnel.TorSocksBridge
import io.deltator.util.AppLog as Log

/**
 * Standard Tor Multipath Backend.
 * Runs ParallelTorManager (with PreFlightBridgePing & Mobile Conflux Profile),
 * TorSocksBridge (with FakeDNS enabled), and HevSocks5Tunnel (tun2socks).
 */
class TorTunnelBackend(
    private val context: Context,
    private val sessionId: Int,
    private val basePort: Int,
    private val transportMode: String,
    private val customBridges: String,
    private val autoTransports: Set<String>,
    private val runMemory: Boolean,
    private val onProgress: (Map<String, TorRunner>) -> Unit
) : TunnelBackend {

    companion object {
        private const val TAG = "TorTunnelBackend"
    }

    override val id: String = EngineMode.TOR_RACING.id
    override val displayName: String = EngineMode.TOR_RACING.title

    @Volatile
    private var running = false

    @Volatile
    var activeRunner: TorRunner? = null
        private set

    override val isRunning: Boolean
        get() = running && (activeRunner?.isRunning() == true)

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean {
        Log.i(TAG, "Starting TorTunnelBackend (sessionId=$sessionId, basePort=$basePort)")

        // 1. Race Tor instances to select fastest bootstrap
        val winner = ParallelTorManager.race(
            context = context,
            basePort = basePort,
            sessionId = sessionId,
            transportMode = transportMode,
            customBridges = customBridges,
            autoTransports = autoTransports,
            runMemory = runMemory,
            onProgress = onProgress
        )
        activeRunner = winner

        // 2. Start TorSocksBridge with FakeDNS on proxyPort -> winner.torSocksPort
        TorSocksBridge.upstreamMode = TorSocksBridge.UpstreamMode.TOR
        TorSocksBridge.chainMode = when (Config.torChainMode) {
            Config.TOR_CHAIN_WARP -> TorSocksBridge.ChainMode.WARP
            Config.TOR_CHAIN_AMNEZIA -> TorSocksBridge.ChainMode.AMNEZIA
            Config.TOR_CHAIN_MASQUE -> TorSocksBridge.ChainMode.MASQUE
            Config.TOR_CHAIN_FREE_NODES -> TorSocksBridge.ChainMode.FREE_NODES
            else -> TorSocksBridge.ChainMode.NONE
        }
        val bridgeResult = TorSocksBridge.start(
            torSocksPort = winner.torSocksPort,
            torHost = "127.0.0.1",
            listenPort = Config.proxyPort,
            listenHost = "127.0.0.1"
        )
        if (bridgeResult.isFailure) {
            Log.e(TAG, "Failed to start TorSocksBridge on 127.0.0.1:${Config.proxyPort}: ${bridgeResult.exceptionOrNull()?.message}")
            return false
        }

        // 3. Start hev-socks5-tunnel (tun2socks) bound to tunFd
        val tunResult = HevSocks5Tunnel.start(
            tunFd = tunFd,
            socksAddress = "127.0.0.1",
            socksPort = Config.proxyPort,
            enableUdpTunneling = true,
            mtu = 1280,
            ipv4Address = "10.255.255.1",
            ipv6Address = "fd00::1",
            disableQuic = true,
            rejectNonDnsUdp = true
        )
        if (tunResult.isFailure) {
            Log.e(TAG, "hev-socks5-tunnel start failed: ${tunResult.exceptionOrNull()?.message}")
            return false
        }

        running = true
        Log.i(TAG, "TorTunnelBackend successfully established on TUN fd ${tunFd.fd}")
        return true
    }

    override fun stop() {
        running = false
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { TorSocksBridge.stop() } catch (_: Exception) {}
        try { ParallelTorManager.stopAll() } catch (_: Exception) {}
        activeRunner = null
        Log.i(TAG, "TorTunnelBackend stopped")
    }

    override fun getStats(): BackendStats? {
        val s = HevSocks5Tunnel.getStats() ?: return null
        return BackendStats(rxBytes = s.rxBytes, txBytes = s.txBytes)
    }
}

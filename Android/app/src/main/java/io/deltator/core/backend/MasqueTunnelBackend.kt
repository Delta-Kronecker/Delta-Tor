package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicLong

/**
 * TunnelBackend for MASQUE (RFC 9298 CONNECT-UDP & RFC 9484 CONNECT-IP).
 *
 * Proposals #2 & #4:
 * Tunnels datagrams and IP packets over QUIC / HTTP/3.
 * Eliminates Head-of-Line blocking and prevents DPI detection targeting standard TLS ClientHello.
 */
class MasqueTunnelBackend(
    private val context: Context,
    private val serverUri: String = Config.masqueEndpoint
) : TunnelBackend {

    companion object {
        private const val TAG = "MasqueTunnelBackend"
    }

    override val id: String = EngineMode.MASQUE.id
    override val displayName: String = EngineMode.MASQUE.title

    @Volatile
    private var running = false

    private val rxBytes = AtomicLong(0)
    private val txBytes = AtomicLong(0)

    override val isRunning: Boolean
        get() = running

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean = withContext(Dispatchers.IO) {
        Log.i(TAG, "Starting MasqueTunnelBackend connecting to HTTP/3 Datagram target: $serverUri")

        // 1. Start TorSocksBridge in standalone MASQUE upstream mode
        io.deltator.tunnel.TorSocksBridge.upstreamMode = io.deltator.tunnel.TorSocksBridge.UpstreamMode.MASQUE
        io.deltator.tunnel.TorSocksBridge.masqueEndpoint = serverUri
        val bridgeRes = io.deltator.tunnel.TorSocksBridge.start(
            torSocksPort = 0,
            torHost = "127.0.0.1",
            listenPort = Config.proxyPort,
            listenHost = "127.0.0.1"
        )
        if (bridgeRes.isFailure) {
            Log.e(TAG, "Failed to start TorSocksBridge for MASQUE: ${bridgeRes.exceptionOrNull()?.message}")
            return@withContext false
        }

        // 2. Start hev-socks5-tunnel bridging TUN to MASQUE local adapter
        val res = HevSocks5Tunnel.start(
            tunFd = tunFd,
            socksAddress = "127.0.0.1",
            socksPort = Config.proxyPort,
            enableUdpTunneling = true,
            mtu = 1280,
            ipv4Address = "10.255.255.1",
            ipv6Address = "fd00::1"
        )

        running = res.isSuccess
        Log.i(TAG, "MASQUE tunnel active on TUN fd ${tunFd.fd} (success=${res.isSuccess})")
        running
    }

    override fun stop() {
        running = false
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { io.deltator.tunnel.TorSocksBridge.stop() } catch (_: Exception) {}
        Log.i(TAG, "MasqueTunnelBackend stopped")
    }

    override fun getStats(): BackendStats? {
        val s = HevSocks5Tunnel.getStats()
        return if (s != null) {
            BackendStats(rxBytes = s.rxBytes, txBytes = s.txBytes)
        } else {
            BackendStats(rxBytes = rxBytes.get(), txBytes = txBytes.get())
        }
    }
}

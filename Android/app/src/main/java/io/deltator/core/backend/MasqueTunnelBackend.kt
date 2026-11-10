package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.tunnel.TorSocksBridge
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * MASQUE (HTTP/3 Datagram Proxy) Tunnel Backend.
 * Routes device TUN packets through HevSocks5Tunnel connected to TorSocksBridge in MASQUE mode.
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

    @Volatile private var running = false

    override val isRunning: Boolean get() = running

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean = withContext(Dispatchers.IO) {
        try {
            Log.i(TAG, "Starting MASQUE H3 Tunnel via TorSocksBridge: endpoint=$serverUri")
            TorSocksBridge.upstreamMode = TorSocksBridge.UpstreamMode.MASQUE
            TorSocksBridge.masqueEndpoint = serverUri
            if (!TorSocksBridge.isRunning()) {
                val br = TorSocksBridge.start(
                    torSocksPort = 0,
                    listenPort = Config.proxyPort,
                    listenHost = "127.0.0.1"
                )
                if (br.isFailure) {
                    Log.e(TAG, "Failed to start TorSocksBridge for MASQUE: ${br.exceptionOrNull()?.message}")
                    return@withContext false
                }
            }
            val res = HevSocks5Tunnel.start(
                tunFd = tunFd,
                socksAddress = "127.0.0.1",
                socksPort = Config.proxyPort,
                enableUdpTunneling = false,
                mtu = 1280
            )
            if (res.isFailure) {
                Log.e(TAG, "Failed to start HevSocks5Tunnel for MASQUE: ${res.exceptionOrNull()?.message}")
                return@withContext false
            }
            running = true
            Log.i(TAG, "MASQUE H3 Tunnel started successfully")
            true
        } catch (e: Exception) {
            Log.e(TAG, "MASQUE start failed: ${e.message}", e)
            running = false
            false
        }
    }

    override fun stop() {
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { TorSocksBridge.stop() } catch (_: Exception) {}
        running = false
        Log.i(TAG, "MasqueTunnelBackend stopped")
    }

    override fun getStats(): BackendStats? =
        HevSocks5Tunnel.getStats()?.let { BackendStats(it.rxBytes, it.txBytes) }
}

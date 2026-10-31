package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.feature.warp.WarpProfile
import io.deltator.feature.warp.WarpRegistrar
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicLong

/**
 * TunnelBackend for Cloudflare WARP Anycast.
 *
 * Proposals #3 & #4:
 * Manages 1-click auto-registered WARP credentials, Anycast IP endpoint rotation,
 * and WireGuard encapsulation with Cloudflare reserved client_id bytes.
 */
class WarpTunnelBackend(
    private val context: Context,
    private var profile: WarpProfile? = WarpProfile.fromJson(Config.warpConfig)
) : TunnelBackend {

    companion object {
        private const val TAG = "WarpTunnelBackend"
    }

    override val id: String = EngineMode.WARP_ANYCAST.id
    override val displayName: String = EngineMode.WARP_ANYCAST.title

    @Volatile
    private var running = false

    private val rxBytes = AtomicLong(0)
    private val txBytes = AtomicLong(0)

    override val isRunning: Boolean
        get() = running

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean = withContext(Dispatchers.IO) {
        Log.i(TAG, "Starting WarpTunnelBackend...")

        // Auto-register if no profile exists
        if (profile == null) {
            Log.i(TAG, "No existing WARP profile; auto-registering via Cloudflare API...")
            val regResult = WarpRegistrar.registerWarp()
            if (regResult.isSuccess) {
                profile = regResult.getOrThrow()
                Config.warpConfig = profile!!.toJson()
            } else {
                Log.e(TAG, "Failed to auto-register WARP: ${regResult.exceptionOrNull()?.message}")
                return@withContext false
            }
        }

        val activeProfile = profile!!
        Log.i(
            TAG,
            "WARP Tunnel starting: Endpoint=${activeProfile.endpoint}, Address=${activeProfile.addressV4}, Reserved=${activeProfile.reserved.size}B"
        )

        // 1. Start TorSocksBridge in standalone WARP upstream mode
        io.deltator.tunnel.TorSocksBridge.upstreamMode = io.deltator.tunnel.TorSocksBridge.UpstreamMode.WARP
        io.deltator.tunnel.TorSocksBridge.warpProfile = activeProfile
        val bridgeRes = io.deltator.tunnel.TorSocksBridge.start(
            torSocksPort = 0,
            torHost = "127.0.0.1",
            listenPort = Config.proxyPort,
            listenHost = "127.0.0.1"
        )
        if (bridgeRes.isFailure) {
            Log.e(TAG, "Failed to start TorSocksBridge for WARP: ${bridgeRes.exceptionOrNull()?.message}")
            return@withContext false
        }

        // 2. Route traffic through hev-socks5-tunnel
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
        Log.i(TAG, "WARP tunnel active on TUN fd ${tunFd.fd} (success=${res.isSuccess})")
        running
    }

    /**
     * Rotates Anycast endpoint if current one is throttled/blocked.
     */
    fun rotateEndpoint(): String? {
        val current = profile ?: return null
        val candidates = WarpRegistrar.ANYCAST_ENDPOINTS.filter { it != current.endpoint }
        val next = candidates.randomOrNull() ?: WarpRegistrar.ANYCAST_ENDPOINTS.first()
        val updated = current.copy(endpoint = next)
        profile = updated
        Config.warpConfig = updated.toJson()
        io.deltator.tunnel.TorSocksBridge.warpProfile = updated
        Log.i(TAG, "Rotated WARP Anycast endpoint to $next")
        return next
    }

    override fun stop() {
        running = false
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { io.deltator.tunnel.TorSocksBridge.stop() } catch (_: Exception) {}
        Log.i(TAG, "WarpTunnelBackend stopped")
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

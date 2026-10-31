package io.deltator.core.backend

import android.os.ParcelFileDescriptor

/**
 * Traffic statistics reported by a tunnel backend.
 */
data class BackendStats(
    val rxBytes: Long,
    val txBytes: Long
)

/**
 * Available hybrid engine tunnel modes.
 */
enum class EngineMode(
    val id: String,
    val title: String,
    val subtitle: String
) {
    TOR_RACING("tor", "Tor Racing", "Parallel bridge racing with Conflux multipath"),
    AMNEZIA_WG("amnezia_wg", "AmneziaWG", "DPI-resistant obfuscated WireGuard (AWG 1.0 - 3.1)"),
    WARP_ANYCAST("warp", "WARP Anycast", "1-click Cloudflare Anycast tunnel with reserved bytes"),
    MASQUE("masque", "MASQUE H3", "CONNECT-UDP/IP datagram proxy over QUIC/HTTP/3");

    companion object {
        fun fromId(id: String): EngineMode {
            return entries.find { it.id.equals(id, ignoreCase = true) } ?: TOR_RACING
        }
    }
}

/**
 * Unified interface for tunnel backends operating behind the Android VpnService interface.
 * Decouples the TUN network device from the specific proxy/tunnel engine.
 */
interface TunnelBackend {
    val id: String
    val displayName: String
    val isRunning: Boolean

    suspend fun start(tunFd: ParcelFileDescriptor): Boolean
    fun stop()
    fun getStats(): BackendStats?
}

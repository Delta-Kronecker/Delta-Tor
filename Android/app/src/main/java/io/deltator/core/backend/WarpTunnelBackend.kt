package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.feature.warp.WarpProfile
import io.deltator.feature.warp.WarpRegistrar
import io.deltator.tunnel.ExitLocator
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import org.amnezia.awg.backend.Backend
import org.amnezia.awg.backend.GoBackend
import org.amnezia.awg.backend.NoopTunnelActionHandler
import org.amnezia.awg.backend.Tunnel
import org.amnezia.awg.config.Config as WgConfig
import java.io.BufferedReader
import java.io.StringReader

/**
 * Cloudflare WARP (WireGuard) backend via org.amnezia.awg (zaneschepke/amneziawg-android).
 *
 * NOTE: Cloudflare WARP requires `reserved` (client_id) bytes in the WireGuard handshake.
 * The AmneziaWG Java config parser does not accept a `Reserved` peer attribute and the
 * underlying amneziawg-go userspace path may not honor WARP reserved bytes. This backend
 * therefore verifies real egress (handshake+connectivity) after UP and reports an honest
 * failure if WARP does not complete a working handshake.
 */
class WarpTunnelBackend(
    private val context: Context,
    private var profile: WarpProfile? = WarpProfile.fromJson(Config.warpConfig)
) : TunnelBackend, Tunnel {

    companion object {
        private const val TAG = "WarpTunnelBackend"
        private const val HANDSHAKE_WAIT_MS = 5_000L
        private const val EGRESS_PROBE_TIMEOUT_MS = 9_000
    }

    private val backend: Backend by lazy { GoBackend(context, NoopTunnelActionHandler()) }

    override val id: String = EngineMode.WARP_ANYCAST.id
    override val displayName: String = EngineMode.WARP_ANYCAST.title

    @Volatile private var running = false
    @Volatile private var tunnelState: Tunnel.State = Tunnel.State.DOWN

    override val isRunning: Boolean get() = running
    override fun getName(): String = "deltator_warp"
    override fun isIpv4ResolutionPreferred(): Boolean = true
    override fun onStateChange(newState: Tunnel.State) {
        tunnelState = newState
        running = newState == Tunnel.State.UP
    }

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean = withContext(Dispatchers.IO) {
        try { tunFd.close() } catch (_: Exception) {}
        try {
            if (profile == null) {
                Log.i(TAG, "No WARP profile; registering…")
                val reg = WarpRegistrar.registerWarp()
                if (reg.isSuccess) {
                    profile = reg.getOrThrow()
                    Config.warpConfig = profile!!.toJson()
                } else {
                    Log.e(TAG, "WARP register failed: ${reg.exceptionOrNull()?.message}")
                    return@withContext false
                }
            }

            // Try current profile/endpoint, then rotate endpoints with verification.
            val base = profile ?: return@withContext false
            val candidates = buildList {
                add(base.endpoint.ifBlank { WarpRegistrar.ANYCAST_ENDPOINTS.first() })
                addAll(WarpRegistrar.ANYCAST_ENDPOINTS)
            }.distinct().take(5)

            var lastError: String? = null
            for ((idx, ep) in candidates.withIndex()) {
                val p = base.copy(endpoint = ep)
                profile = p
                Config.warpConfig = p.toJson()
                Log.i(TAG, "WARP attempt ${idx + 1}/${candidates.size} endpoint=$ep reserved=${p.reserved.joinToString(",")}")

                bringDownQuietly()
                if (!bringUp(p)) {
                    lastError = "backend start failed"
                    continue
                }

                // Give the handshake time, then verify real egress through the tunnel.
                delay(HANDSHAKE_WAIT_MS)
                val exit = try {
                    ExitLocator.locateDirect(context, timeoutMs = EGRESS_PROBE_TIMEOUT_MS)
                } catch (e: Exception) {
                    Log.w(TAG, "WARP egress probe error: ${e.message}")
                    null
                }
                if (exit != null && exit.ip.isNotBlank()) {
                    Log.i(TAG, "WARP connected: endpoint=$ep exit=${exit.ip} ${exit.label()}")
                    return@withContext true
                }

                lastError = "no handshake/egress (likely Cloudflare reserved bytes unsupported by AWG backend)"
                Log.w(TAG, "WARP attempt endpoint=$ep: $lastError")
            }

            bringDownQuietly()
            Log.e(TAG, "WARP failed after ${candidates.size} endpoint(s): $lastError. " +
                "Reserved=${base.reserved.joinToString(",")}. A WARP-reserved-capable WG backend is required for Cloudflare WARP WG mode.")
            false
        } catch (e: Exception) {
            Log.e(TAG, "WARP start failed: ${e.message}", e)
            bringDownQuietly()
            false
        }
    }

    private fun bringUp(p: WarpProfile): Boolean {
        return try {
            val wg = toWgQuick(p, includeReserved = true)
            val cfg = try {
                WgConfig.parse(BufferedReader(StringReader(wg)))
            } catch (e: Exception) {
                Log.w(TAG, "WARP config parse with Reserved failed (${e.message}); retrying without Reserved")
                WgConfig.parse(BufferedReader(StringReader(toWgQuick(p, includeReserved = false))))
            }
            val st = backend.setState(this@WarpTunnelBackend, Tunnel.State.UP, cfg)
            tunnelState = st
            running = st == Tunnel.State.UP
            Log.i(TAG, "WARP backend state=$st endpoint=${p.endpoint}")
            running
        } catch (e: Exception) {
            Log.e(TAG, "WARP bringUp failed: ${e.message}", e)
            running = false
            false
        }
    }

    private fun bringDownQuietly() {
        try { backend.setState(this, Tunnel.State.DOWN, null) } catch (_: Exception) {}
        running = false
        tunnelState = Tunnel.State.DOWN
    }

    private fun toWgQuick(p: WarpProfile, includeReserved: Boolean = true): String = buildString {
        appendLine("[Interface]")
        appendLine("PrivateKey = ${p.privateKey}")
        // Prefer IPv4 to avoid VpnService IPv6 address permission issues on some ROMs.
        val addr = p.addressV4.takeIf { it.isNotBlank() } ?: p.addressV6
        if (addr.isNotBlank()) appendLine("Address = $addr")
        appendLine("DNS = 1.1.1.1, 1.0.0.1")
        appendLine("MTU = 1280")
        appendLine()
        appendLine("[Peer]")
        appendLine("PublicKey = ${p.peerPublicKey}")
        appendLine("AllowedIPs = 0.0.0.0/0, ::/0")
        val ep = p.endpoint.ifBlank { WarpRegistrar.ANYCAST_ENDPOINTS.first() }
        appendLine("Endpoint = $ep")
        appendLine("PersistentKeepalive = 25")
        // WARP reserved/client_id bytes (Cloudflare). Java parser/backend may not support this.
        if (includeReserved && p.reserved.size >= 3) {
            appendLine("Reserved = ${p.reserved[0].toInt() and 0xFF}, ${p.reserved[1].toInt() and 0xFF}, ${p.reserved[2].toInt() and 0xFF}")
        }
    }

    fun rotateEndpoint(): String? {
        val current = profile ?: return null
        val candidates = WarpRegistrar.ANYCAST_ENDPOINTS.filter { it != current.endpoint }
        val next = candidates.randomOrNull() ?: WarpRegistrar.ANYCAST_ENDPOINTS.first()
        val updated = current.copy(endpoint = next)
        profile = updated
        Config.warpConfig = updated.toJson()
        Log.i(TAG, "Rotated WARP endpoint to $next (reconnect to apply)")
        return next
    }

    override fun stop() {
        bringDownQuietly()
        Log.i(TAG, "WarpTunnelBackend stopped")
    }

    override fun getStats(): BackendStats? = try {
        val s = backend.getStatistics(this)
        BackendStats(rxBytes = s.totalRx(), txBytes = s.totalTx())
    } catch (_: Exception) { null }
}

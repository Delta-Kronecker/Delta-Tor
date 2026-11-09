package io.deltator.core.backend

import android.content.Context
import android.os.ParcelFileDescriptor
import io.deltator.Config
import io.deltator.feature.amnezia.AmneziaConfig
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.amnezia.awg.backend.Backend
import org.amnezia.awg.backend.GoBackend
import org.amnezia.awg.backend.NoopTunnelActionHandler
import org.amnezia.awg.backend.Tunnel
import org.amnezia.awg.config.Config as WgConfig
import java.io.BufferedReader
import java.io.StringReader

/**
 * Real AmneziaWG (AWG 1.x) backend via org.amnezia.awg (zaneschepke/amneziawg-android).
 * Applies AWG obfuscation params (Jc/Jmin/Jmax/S1/S2/H1-H4) supported by this backend.
 */
class AmneziaTunnelBackend(
    private val context: Context,
    private val config: AmneziaConfig = AmneziaConfig.fromJson(Config.amneziaWgConfig) ?: AmneziaConfig()
) : TunnelBackend, Tunnel {

    companion object { private const val TAG = "AmneziaTunnelBackend" }
    private val backend: Backend by lazy { GoBackend(context, NoopTunnelActionHandler()) }

    override val id: String = EngineMode.AMNEZIA_WG.id
    override val displayName: String = EngineMode.AMNEZIA_WG.title

    @Volatile private var running = false
    @Volatile private var tunnelState: Tunnel.State = Tunnel.State.DOWN

    override val isRunning: Boolean get() = running
    override fun getName(): String = "deltator_awg"
    override fun isIpv4ResolutionPreferred(): Boolean = true
    override fun onStateChange(newState: Tunnel.State) { tunnelState = newState; running = newState == Tunnel.State.UP }

    override suspend fun start(tunFd: ParcelFileDescriptor): Boolean = withContext(Dispatchers.IO) {
        try { tunFd.close() } catch (_: Exception) {}
        try {
            if (config.endpoint.isBlank() || config.peerPublicKey.isBlank()) {
                Log.w(TAG, "AmneziaWG config incomplete (endpoint/peer key missing)")
                return@withContext false
            }
            Log.i(TAG, "Starting AmneziaWG (AWG): Jc=${config.jc} Jmin=${config.jmin} Jmax=${config.jmax} S1=${config.s1} S2=${config.s2} H1=${config.h1} H2=${config.h2} H3=${config.h3} H4=${config.h4}")
            val wg = toWgQuick(config)
            val cfg = WgConfig.parse(BufferedReader(StringReader(wg)))
            val st = backend.setState(this@AmneziaTunnelBackend, Tunnel.State.UP, cfg)
            running = st == Tunnel.State.UP
            tunnelState = st
            Log.i(TAG, "AmneziaWG start state=$st endpoint=${config.endpoint}")
            running
        } catch (e: Exception) {
            Log.e(TAG, "AmneziaWG start failed: ${e.message}", e)
            running = false; false
        }
    }

    private fun toWgQuick(c: AmneziaConfig): String = buildString {
        appendLine("[Interface]")
        appendLine("PrivateKey = ${c.privateKey}")
        // Prefer IPv4 to avoid VpnService IPv6 address permission issues on some ROMs.
        if (c.address.isNotBlank()) appendLine("Address = ${c.address.split(',').first().trim()}")
        if (c.dns.isNotBlank()) appendLine("DNS = ${c.dns}")
        appendLine("MTU = 1280")
        // AWG 1.x obfuscation params
        if (c.jc > 0) appendLine("Jc = ${c.jc}")
        if (c.jmin > 0) appendLine("Jmin = ${c.jmin}")
        if (c.jmax > 0) appendLine("Jmax = ${c.jmax}")
        if (c.s1 > 0) appendLine("S1 = ${c.s1}")
        if (c.s2 > 0) appendLine("S2 = ${c.s2}")
        if (c.h1 != 1L) appendLine("H1 = ${c.h1}")
        if (c.h2 != 2L) appendLine("H2 = ${c.h2}")
        if (c.h3 != 3L) appendLine("H3 = ${c.h3}")
        if (c.h4 != 4L) appendLine("H4 = ${c.h4}")
        appendLine()
        appendLine("[Peer]")
        appendLine("PublicKey = ${c.peerPublicKey}")
        if (!c.presharedKey.isNullOrBlank()) appendLine("PresharedKey = ${c.presharedKey}")
        if (c.allowedIps.isNotBlank()) appendLine("AllowedIPs = ${c.allowedIps}")
        appendLine("Endpoint = ${c.endpoint}")
        appendLine("PersistentKeepalive = 25")
    }

    override fun stop() {
        try { backend.setState(this, Tunnel.State.DOWN, null) } catch (e: Exception) { Log.w(TAG, "AWG stop: ${e.message}") }
        running = false; tunnelState = Tunnel.State.DOWN
        Log.i(TAG, "AmneziaTunnelBackend stopped")
    }

    override fun getStats(): BackendStats? = try {
        val s = backend.getStatistics(this)
        BackendStats(rxBytes = s.totalRx(), txBytes = s.totalTx())
    } catch (_: Exception) { null }
}

package kittoku.osc

import android.net.VpnService
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.net.Socket
import java.util.UUID
import kittoku.osc.terminal.IPTerminal
import kittoku.osc.terminal.SSLTerminal


internal enum class Where {
    CERT,
    CERT_PATH,
    SSL,
    PROXY,
    SSTP_DATA,
    SSTP_CONTROL,
    SSTP_REQUEST,
    SSTP_HASH,
    PPP,
    PAP,
    CHAP,
    MSCHAPV2,
    EAP,
    LCP,
    LCP_MRU,
    LCP_AUTH,
    IPCP,
    IPCP_IP,
    IPV6CP,
    IPV6CP_IDENTIFIER,
    IP,
    IPv4,
    IPv6,
    ROUTE,
    INCOMING,
    OUTGOING,
}

internal data class ControlMessage(
    val from: Where,
    val result: Result,
    val supplement: String? = null
)

internal enum class Result {
    PROCEEDED,

    // common errors
    ERR_TIMEOUT,
    ERR_COUNT_EXHAUSTED,
    ERR_UNKNOWN_TYPE, // the data cannot be parsed
    ERR_UNEXPECTED_MESSAGE, // the data can be parsed, but it's received in the wrong time
    ERR_PARSING_FAILED,
    ERR_VERIFICATION_FAILED,

    // for SSTP
    ERR_NEGATIVE_ACKNOWLEDGED,
    ERR_ABORT_REQUESTED,
    ERR_DISCONNECT_REQUESTED,

    // for PPP
    ERR_TERMINATE_REQUESTED,
    ERR_PROTOCOL_REJECTED,
    ERR_CODE_REJECTED,
    ERR_AUTHENTICATION_FAILED,
    ERR_ADDRESS_REJECTED,
    ERR_OPTION_REJECTED,

    // for IP
    ERR_INVALID_ADDRESS,

    // for INCOMING
    ERR_INVALID_PACKET_SIZE,
}

/*
 * Copyright (c) 2024 kittoku
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this project's source tree.
 * (adapted from the original Open-SSTP-Client: settings come from
 * [SstpParams] instead of SharedPreferences, the service is reduced to the
 * pieces the core actually needs, and app-based routing rules are not ported)
 */
internal class SharedBridge(
    internal val params: SstpParams,
    internal val scope: CoroutineScope,
    /** VpnService.Builder of the owning service, null in [SstpMode.PROBE]. */
    internal val builder: VpnService.Builder?,
    /** Exempts a socket from the owning VPN tunnel. */
    internal val protect: (Socket?) -> Boolean,
    /** Line for every notable event and error of the session. */
    internal val log: (String) -> Unit,
    /** Called once when the session is torn down by whatever reason. */
    internal val onSessionEnded: () -> Unit,
    /** Called once when the negotiation reached its goal: a live tunnel
     *  carrying packets in [SstpMode.RUN], an answered IPCP in
     *  [SstpMode.PROBE]. */
    internal val onNegotiated: () -> Unit = {},
) {
    internal lateinit var handler: CoroutineExceptionHandler

    internal val controlMailbox = Channel<ControlMessage>(Channel.BUFFERED)

    internal var sslTerminal: SSLTerminal? = null
    internal var ipTerminal: IPTerminal? = null

    internal val HOME_USERNAME = params.username
    internal val HOME_PASSWORD = params.password
    internal val PPP_MRU = params.mru
    internal val PPP_MTU = params.mtu
    internal val PPP_AUTH_PROTOCOLS = params.authProtocols
    internal val PPP_IPv4_ENABLED = params.pppIPv4Enabled
    internal val PPP_IPv6_ENABLED = params.pppIPv6Enabled

    internal var hlak: ByteArray? = null
    internal val nonce = ByteArray(32)
    internal val guid = UUID.randomUUID().toString()
    internal var hashProtocol: Byte = 0

    private val mutex = Mutex()
    private var frameID = -1

    internal var currentMRU = PPP_MRU
    internal var currentAuth = ""
    internal val currentIPv4 = ByteArray(4)
    internal val currentIPv6 = ByteArray(8)
    internal val currentProposedDNS = ByteArray(4)

    internal fun isEnabled(authProtocol: String): Boolean {
        return authProtocol in PPP_AUTH_PROTOCOLS
    }

    internal fun sendControlMessage(message: ControlMessage) {
        controlMailbox.trySend(message)
    }

    internal fun attachSSLTerminal() {
        sslTerminal = SSLTerminal(this)
    }

    internal fun attachIPTerminal() {
        ipTerminal = IPTerminal(this)
    }

    internal suspend fun allocateNewFrameID(): Byte {
        mutex.withLock {
            frameID += 1
            return frameID.toByte()
        }
    }
}

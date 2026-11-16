package io.deltator.tunnel

import io.deltator.util.AppLog as Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * Tor's own resolver, reached on the loopback `DNSPort` every running Tor
 * runner opens for itself.
 *
 * This is the Android counterpart of the Windows `TorDns` relay. Where Windows
 * points the system's adapters at a loopback relay and forwards the raw DNS
 * payloads to the active runner's `DNSPort`, Android has no way to bind port 53
 * (a normal app cannot open a privileged port, and `addDnsServer` always talks
 * to port 53), so the interception happens in the data plane instead:
 *
 *  - TUN mode: hev-socks5-tunnel hands every DNS datagram to [TorSocksBridge],
 *    which calls [query] with the winning runner's port.
 *  - Chain mode: the SSTP core's [kittoku.osc.terminal.IPTerminal] intercepts
 *    UDP/53 read off its TUN and answers it through [query] as well.
 *
 * Either way a name is resolved by Tor itself (the query travels a Tor circuit
 * to the exit and is answered there), never by a public resolver reached over
 * Tor and never by the VPN Gate resolver at the far end of a chain.
 *
 * [query] sends one DNS payload to `127.0.0.1:<dnsPort>` and returns the answer
 * bytes, or null when Tor did not answer in time. A null is a *failed closed*
 * lookup: the caller drops the query rather than falling back to a resolver
 * outside Tor.
 */
object TorDns {
    private const val TAG = "TorDns"

    /** Generous, because a fresh circuit can take a moment to resolve. */
    private const val TIMEOUT_MS = 10_000

    /** A DNS message cannot exceed 65535 bytes (RFC 1035 section 4.2.2). */
    private const val MAX_MESSAGE = 65_535

    private val LOOPBACK: InetAddress = InetAddress.getByName("127.0.0.1")

    /**
     * Ask Tor's resolver on [dnsPort] about [payload]. Returns the raw DNS
     * response, or null when [dnsPort] is unset, [payload] is empty, or Tor did
     * not answer before [timeoutMs].
     */
    fun query(
        dnsPort: Int,
        payload: ByteArray,
        timeoutMs: Int = TIMEOUT_MS
    ): ByteArray? {
        if (dnsPort <= 0 || payload.isEmpty()) return null
        var socket: DatagramSocket? = null
        return try {
            // A fresh socket per query: the reply can only be addressed to this
            // ephemeral port, so no transaction id needs to be matched by hand.
            socket = DatagramSocket()
            socket.soTimeout = timeoutMs
            socket.send(DatagramPacket(payload, payload.size, LOOPBACK, dnsPort))

            val buffer = ByteArray(MAX_MESSAGE)
            val response = DatagramPacket(buffer, buffer.size)
            socket.receive(response)
            if (response.length <= 0) null else buffer.copyOf(response.length)
        } catch (e: Exception) {
            Log.w(TAG, "DNSPort query failed on 127.0.0.1:$dnsPort: ${e.message}")
            null
        } finally {
            try {
                socket?.close()
            } catch (_: Exception) {
            }
        }
    }
}

package kittoku.osc.terminal

import android.os.ParcelFileDescriptor
import android.util.Log
import kittoku.osc.ControlMessage
import kittoku.osc.Result
import kittoku.osc.SharedBridge
import kittoku.osc.Where
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.InetAddress
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import java.util.concurrent.ThreadFactory


/*
 * Copyright (c) 2024 kittoku
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this project's source tree.
 * (adapted from the original Open-SSTP-Client: IPv4 only, the VPN Gate DNS or
 * a public fallback is taken, and everything is routed into the tunnel)
 */

private const val TAG = "IPTerminal"

/** UDP/TCP DNS. */
private const val DNS_PORT = 53

private const val IPPROTO_TCP = 6
private const val IPPROTO_UDP = 17

/** IPv4, first byte's high nibble. */
private const val IPV4_VERSION = 0x40

internal class IPTerminal(private val bridge: SharedBridge) {
    private var fd: ParcelFileDescriptor? = null

    private var inputStream: FileInputStream? = null
    private var outputStream: FileOutputStream? = null

    /**
     * Every write to [outputStream] goes through this lock: tunnel data arrives
     * on the incoming coroutine while a DNS answer arrives on a [dnsExecutor]
     * thread, and a TUN accepts one whole IP packet per write.
     */
    private val writeLock = Any()

    /**
     * DNS answers are resolved off the TUN read path. A lookup is a round trip
     * through Tor, and blocking the read loop on it would stall every other
     * packet in the chain.
     */
    private val dnsExecutor = Executors.newFixedThreadPool(2, ThreadFactory { r ->
        Thread(r, "sstp-dns").also { it.isDaemon = true }
    })

    internal suspend fun initialize() {
        if (bridge.currentIPv4.contentEquals(ByteArray(4))) {
            bridge.controlMailbox.send(ControlMessage(Where.IPv4, Result.ERR_INVALID_ADDRESS))
            return
        }

        InetAddress.getByAddress(bridge.currentIPv4).also {
            bridge.builder!!.addAddress(it, 32)
        }

        // Only a route target: every query sent to it is intercepted here and
        // answered by Tor's own resolver (see below). It is never reached.
        if (!bridge.currentProposedDNS.contentEquals(ByteArray(4))) {
            InetAddress.getByAddress(bridge.currentProposedDNS).also {
                bridge.builder!!.addDnsServer(it)
            }
        } else {
            // Some hubs hand out no resolver; a public one keeps the tunnel
            // usable instead of silently producing an unresolvable VPN.
            bridge.builder!!.addDnsServer(InetAddress.getByName("9.9.9.9"))
        }

        bridge.builder!!.addRoute("0.0.0.0", 0)
        bridge.builder!!.setMtu(bridge.PPP_MTU)
        bridge.builder!!.setBlocking(true)

        // Keep this app's own sockets out of the tunnel it just created: Tor
        // underneath is still talking to its guards, and those links must not
        // be captured and pushed back into the chain.
        bridge.params.ownPackageName?.let {
            bridge.builder!!.addDisallowedApplication(it)
        }

        fd = bridge.builder!!.establish()!!.also {
            inputStream = FileInputStream(it.fileDescriptor)
            outputStream = FileOutputStream(it.fileDescriptor)
        }

        bridge.controlMailbox.send(ControlMessage(Where.IP, Result.PROCEEDED))
    }

    internal fun writePacket(start: Int, size: Int, buffer: ByteBuffer) {
        synchronized(writeLock) {
            outputStream?.write(buffer.array(), start, size)
        }
    }

    internal fun readPacket(buffer: ByteBuffer) {
        while (true) {
            buffer.clear()
            val read = inputStream?.read(buffer.array(), 0, bridge.PPP_MTU) ?: 0
            if (read <= 0) {
                // End of the TUN, or nothing read: hand back an empty packet, the
                // same terminal shape the original returned when the stream ended.
                buffer.limit(0)
                return
            }
            buffer.position(read)
            buffer.flip()

            // DNS is answered here and never travels the chain; anything else is
            // the caller's to encapsulate.
            if (!interceptDns(buffer.array(), read)) return
        }
    }

    internal fun close() {
        dnsExecutor.shutdownNow()
        try {
            fd?.close()
        } catch (_: Exception) {
        }
        fd = null
    }

    // --- DNS interception ---

    /**
     * True when the packet was consumed here. Every UDP query for port 53 is
     * forwarded to Tor's own resolver and its answer injected back to the TUN;
     * a TCP query for port 53 is dropped, because Tor's resolver answers UDP only
     * and letting it out would hand the name to the VPN Gate resolver behind the
     * chain. A null resolver (a session without a Tor engine) disables this and
     * lets the packet through untouched.
     */
    private fun interceptDns(packet: ByteArray, len: Int): Boolean {
        val resolver = bridge.params.dnsResolver ?: return false
        if (len < 20) return false
        if ((packet[0].toInt() and 0xF0) != IPV4_VERSION) return false

        val ihl = (packet[0].toInt() and 0x0F) * 4
        if (ihl < 20 || ihl + 8 > len) return false
        // A non-first fragment has no transport header to read; leave it alone.
        if ((packet[6].toInt() and 0x1F) != 0) return false

        val protocol = packet[9].toInt() and 0xFF
        if (protocol != IPPROTO_UDP && protocol != IPPROTO_TCP) return false

        val dstPort = ((packet[ihl + 2].toInt() and 0xFF) shl 8) or (packet[ihl + 3].toInt() and 0xFF)
        if (dstPort != DNS_PORT) return false

        if (protocol == IPPROTO_TCP) {
            Log.w(TAG, "dropping TCP DNS query: Tor's DNSPort answers UDP only")
            return true
        }

        val query = packet.copyOf(len)
        val payload = query.copyOfRange(ihl + 8, len)
        dnsExecutor.execute {
            try {
                val answer = resolver(payload) ?: return@execute
                if (answer.isEmpty()) return@execute
                writeRaw(buildResponse(query, ihl, answer))
            } catch (e: Exception) {
                Log.w(TAG, "DNS interception failed: ${e.message}")
            }
        }
        return true
    }

    /** Write one whole IP packet back into the TUN. */
    private fun writeRaw(bytes: ByteArray) {
        synchronized(writeLock) {
            try {
                outputStream?.write(bytes)
            } catch (e: Exception) {
                Log.w(TAG, "DNS answer write failed: ${e.message}")
            }
        }
    }

    /**
     * The UDP reply to [query]: the IPv4 header with the two addresses swapped,
     * a fresh header checksum, and the resolved DNS [answer] as the datagram.
     * The UDP checksum is left zero, which IPv4 defines as "not computed".
     */
    private fun buildResponse(query: ByteArray, ihl: Int, answer: ByteArray): ByteArray {
        val out = ByteArray(ihl + 8 + answer.size)
        System.arraycopy(query, 0, out, 0, ihl)

        // Swap source and destination addresses (offsets are fixed by IPv4).
        for (i in 0 until 4) {
            out[12 + i] = query[16 + i]
            out[16 + i] = query[12 + i]
        }

        val totalLen = out.size
        out[2] = ((totalLen shr 8) and 0xFF).toByte()
        out[3] = (totalLen and 0xFF).toByte()
        out[8] = 64 // TTL
        out[10] = 0
        out[11] = 0
        val checksum = ipChecksum(out, ihl)
        out[10] = ((checksum shr 8) and 0xFF).toByte()
        out[11] = (checksum and 0xFF).toByte()

        // UDP header: swap ports, set the length, no checksum.
        val udp = ihl
        out[udp] = query[udp + 2]
        out[udp + 1] = query[udp + 3]
        out[udp + 2] = query[udp]
        out[udp + 3] = query[udp + 1]
        val udpLen = 8 + answer.size
        out[udp + 4] = ((udpLen shr 8) and 0xFF).toByte()
        out[udp + 5] = (udpLen and 0xFF).toByte()
        out[udp + 6] = 0
        out[udp + 7] = 0

        System.arraycopy(answer, 0, out, udp + 8, answer.size)
        return out
    }

    /** The IPv4 header checksum (RFC 1071) over the first [len] bytes. */
    private fun ipChecksum(buf: ByteArray, len: Int): Int {
        var sum = 0
        var i = 0
        while (i + 1 < len) {
            sum += ((buf[i].toInt() and 0xFF) shl 8) or (buf[i + 1].toInt() and 0xFF)
            i += 2
        }
        if (i < len) sum += (buf[i].toInt() and 0xFF) shl 8
        while ((sum ushr 16) != 0) sum = (sum and 0xFFFF) + (sum ushr 16)
        return sum.inv() and 0xFFFF
    }
}

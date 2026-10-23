package kittoku.osc.terminal

import android.os.ParcelFileDescriptor
import kittoku.osc.ControlMessage
import kittoku.osc.Result
import kittoku.osc.SharedBridge
import kittoku.osc.Where
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.InetAddress
import java.nio.ByteBuffer


/*
 * Copyright (c) 2024 kittoku
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this project's source tree.
 * (adapted from the original Open-SSTP-Client: IPv4 only, the VPN Gate DNS or
 * a public fallback is taken, and everything is routed into the tunnel)
 */
internal class IPTerminal(private val bridge: SharedBridge) {
    private var fd: ParcelFileDescriptor? = null

    private var inputStream: FileInputStream? = null
    private var outputStream: FileOutputStream? = null

    internal suspend fun initialize() {
        if (bridge.currentIPv4.contentEquals(ByteArray(4))) {
            bridge.controlMailbox.send(ControlMessage(Where.IPv4, Result.ERR_INVALID_ADDRESS))
            return
        }

        InetAddress.getByAddress(bridge.currentIPv4).also {
            bridge.builder!!.addAddress(it, 32)
        }

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
        // nothing will be written until initialized
        // the position won't be changed
        outputStream?.write(buffer.array(), start, size)
    }

    internal fun readPacket(buffer: ByteBuffer) {
        buffer.clear()
        buffer.position(inputStream?.read(buffer.array(), 0, bridge.PPP_MTU) ?: buffer.position())
        buffer.flip()
    }

    internal fun close() {
        try {
            fd?.close()
        } catch (_: Exception) {
        }
        fd = null
    }
}

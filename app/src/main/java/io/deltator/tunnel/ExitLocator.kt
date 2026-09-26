package io.deltator.tunnel

import android.content.Context
import java.io.BufferedReader
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Resolves the public exit IP as seen through the app's SOCKS5 tunnel by doing
 * a SOCKS5 CONNECT + plain HTTP GET against a small echo service.
 */
object ExitLocator {
    private const val TAG = "ExitLocator"
    private val hosts = listOf("api.ipify.org", "icanhazip.com", "ifconfig.me")

    /** Returns the exit IPv4 as string, or null when every probe failed. */
    fun exitIp(socksHost: String, socksPort: Int, timeoutMs: Int = 12000): String? {
        for (host in hosts) {
            try {
                val ip = probe(socksHost, socksPort, host, timeoutMs)
                if (ip != null) return ip
            } catch (e: Exception) {
                // skip
            }
        }
        return null
    }

    private fun probe(socksHost: String, socksPort: Int, host: String, timeoutMs: Int): String? {
        Socket().use { socket ->
            socket.connect(InetSocketAddress(socksHost, socksPort), 6000)
            socket.soTimeout = timeoutMs
            val out = DataOutputStream(socket.getOutputStream())
            val inp = DataInputStream(socket.getInputStream())

            // SOCKS5 greeting: no auth
            out.write(byteArrayOf(0x05, 0x01, 0x00))
            out.flush()
            if (inp.readUnsignedByte() != 0x05) return null
            if (inp.readUnsignedByte() != 0x00) return null

            // CONNECT host:80 (remote DNS resolution)
            val hostBytes = host.toByteArray(Charsets.US_ASCII)
            val req = ByteArray(5 + hostBytes.size + 2)
            req[0] = 0x05
            req[1] = 0x01
            req[2] = 0x00
            req[3] = 0x03
            req[4] = hostBytes.size.toByte()
            System.arraycopy(hostBytes, 0, req, 5, hostBytes.size)
            req[req.size - 2] = 0x00
            req[req.size - 1] = 0x50 // port 80
            out.write(req)
            out.flush()

            if (inp.readUnsignedByte() != 0x05) return null
            if (inp.readUnsignedByte() != 0x00) return null
            inp.readUnsignedByte() // RSV
            when (inp.readUnsignedByte()) {
                0x01 -> repeat(4) { inp.readUnsignedByte() }
                0x04 -> repeat(16) { inp.readUnsignedByte() }
                0x03 -> {
                    val len = inp.readUnsignedByte()
                    if (len > 0) inp.readNBytes(len)
                }
                else -> return null
            }
            repeat(2) { inp.readUnsignedByte() } // PORT

            out.write("GET / HTTP/1.1\r\nHost: $host\r\nConnection: close\r\n\r\n".toByteArray(Charsets.US_ASCII))
            out.flush()

            BufferedReader(InputStreamReader(inp, Charsets.US_ASCII)).use { reader ->
                var line = reader.readLine()
                while (line != null) {
                    val t = line.trim()
                    if (t.matches(Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}$"))) return t
                    line = reader.readLine()
                }
            }
        }
        return null
    }
}
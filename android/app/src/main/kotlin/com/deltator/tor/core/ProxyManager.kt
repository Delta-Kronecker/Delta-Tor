package com.deltator.tor.core

import kotlinx.coroutines.*
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer

class ProxyManager {
    private var serverSocket: ServerSocket? = null
    private var job: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    fun startHttpProxy(listenPort: Int, socksPort: Int) {
        stopHttpProxy()

        job = scope.launch {
            try {
                val server = ServerSocket(listenPort, 64, java.net.InetAddress.getByName("127.0.0.1"))
                serverSocket = server

                while (isActive) {
                    try {
                        val client = server.accept()
                        launch { handleClient(client, socksPort) }
                    } catch (_: Exception) {
                        if (!isActive) break
                    }
                }
            } catch (_: Exception) {}
        }
    }

    fun stopHttpProxy() {
        job?.cancel()
        job = null
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
    }

    private fun handleClient(client: Socket, socksPort: Int) {
        try {
            client.soTimeout = 15000
            val input = client.getInputStream()
            val output = client.getOutputStream()

            val headerBuf = mutableListOf<Byte>()
            val buf = ByteArray(4096)
            while (true) {
                val n = input.read(buf)
                if (n <= 0) { client.close(); return }
                for (i in 0 until n) headerBuf.add(buf[i])
                val headerStr = String(headerBuf.toByteArray())
                if (headerStr.contains("\r\n\r\n")) break
                if (headerBuf.size > 65536) { client.close(); return }
            }

            val headerBytes = headerBuf.toByteArray()
            val headerStr = String(headerBytes)
            val headerEnd = headerStr.indexOf("\r\n\r\n")
            val headersRaw = headerStr.substring(0, headerEnd)
            val body = headerBytes.copyOfRange(headerEnd + 4, headerBytes.size)

            val firstLine = headersRaw.split("\r\n")[0]
            val parts = firstLine.split(" ", limit = 3)
            if (parts.size < 2) { client.close(); return }

            val method = parts[0]
            val target = parts[1]

            if (method == "CONNECT") {
                val (host, port) = if (target.contains(":")) {
                    val lastColon = target.lastIndexOf(':')
                    Pair(target.substring(0, lastColon), target.substring(lastColon + 1).toIntOrNull() ?: 443)
                } else {
                    Pair(target, 443)
                }

                try {
                    output.write("HTTP/1.1 200 Connection established\r\n\r\n".toByteArray())
                } catch (_: Exception) { client.close(); return }

                relayThroughSocks(client, socksPort, host, port)
            } else {
                val uri = java.net.URI(target)
                val host = uri.host ?: ""
                val port = if (uri.port > 0) uri.port else 80
                var path = uri.path ?: "/"
                if (uri.query != null) path += "?${uri.query}"

                val lines = headersRaw.split("\r\n").toMutableList()
                lines[0] = "$method $path HTTP/1.1"
                val newHeaders = lines.joinToString("\r\n") + "\r\n\r\n"

                relayThroughSocks(client, socksPort, host, port, newHeaders.toByteArray() + body)
            }
        } catch (_: Exception) {
        } finally {
            try { client.close() } catch (_: Exception) {}
        }
    }

    private fun relayThroughSocks(
        client: Socket,
        socksPort: Int,
        host: String,
        port: Int,
        initialData: ByteArray? = null
    ) {
        var tor: Socket? = null
        try {
            tor = Socket(java.net.InetAddress.getByName("127.0.0.1"), socksPort)
            tor.soTimeout = 30000
            tor.getOutputStream().write(byteArrayOf(0x05, 0x01, 0x00))
            val greeting = ByteArray(2)
            tor.getInputStream().read(greeting)
            if (greeting[1] != 0x00.toByte()) return

            val hostBytes = host.toByteArray()
            tor.getOutputStream().write(
                byteArrayOf(0x05, 0x01, 0x00, 0x03, hostBytes.size.toByte()) +
                hostBytes + port.toShort().let { byteArrayOf((it.toInt() shr 8).toByte(), it.toByte()) }
            )
            val connectResp = ByteArray(10)
            tor.getInputStream().read(connectResp)
            if (connectResp[1] != 0x00.toByte()) return

            if (initialData != null) tor.getOutputStream().write(initialData)

            tor.soTimeout = 30000
            client.soTimeout = 30000

            val buf = ByteArray(65536)
            while (true) {
                try {
                    if (client.getInputStream().available() > 0) {
                        val n = client.getInputStream().read(buf)
                        if (n <= 0) break
                        tor.getOutputStream().write(buf, 0, n)
                    }
                    if (tor.getInputStream().available() > 0) {
                        val n = tor.getInputStream().read(buf)
                        if (n <= 0) break
                        client.getOutputStream().write(buf, 0, n)
                    }
                    if (client.getInputStream().available() == 0 && tor.getInputStream().available() == 0) {
                        Thread.sleep(10)
                    }
                } catch (_: Exception) { break }
            }
        } catch (_: Exception) {
        } finally {
            try { client.close() } catch (_: Exception) {}
            try { tor?.close() } catch (_: Exception) {}
        }
    }
}

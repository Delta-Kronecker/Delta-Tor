package kittoku.osc.terminal

import kittoku.osc.ControlMessage
import kittoku.osc.Result
import kittoku.osc.SharedBridge
import kittoku.osc.Where
import kittoku.osc.extension.capacityAfterLimit
import kittoku.osc.extension.slide
import kittoku.osc.extension.toIntAsUByte
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.yield
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.security.cert.CertificateException
import java.security.cert.CertPathValidatorException
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLEngineResult
import javax.net.ssl.SSLSession
import javax.net.ssl.X509TrustManager


private const val HTTP_DELIMITER = "\r\n"
private const val HTTP_SUFFIX = "\r\n\r\n"

/** Slightly above the original: the TCP hop goes through Tor first, so the
 *  whole TLS+HTTP+SSTP negotiation carries the extra circuit latency. */
internal const val SSL_REQUEST_INTERVAL = 15_000L

private const val PROXY_CONNECT_TIMEOUT = 20_000

/*
 * Copyright (c) 2024 kittoku
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this project's source tree.
 * (adapted from the original Open-SSTP-Client: every connection is dialed
 * through Tor's SOCKS5 bridge, optional verification instead of a trusted
 * certificate directory, and Proxy-Authorization over HTTP CONNECT was
 * replaced by a raw SOCKS5 handshake)
 */
internal class SSLTerminal(private val bridge: SharedBridge) {
    private val mutex = Mutex()

    private var socket: Socket? = null
    private lateinit var socketInputStream: InputStream
    private lateinit var socketOutputStream: OutputStream
    private lateinit var inboundBuffer: ByteBuffer
    private lateinit var outboundBuffer: ByteBuffer

    private lateinit var engine: SSLEngine

    private var jobInitialize: Job? = null

    private val sslHostname = bridge.params.hostname
    private val sslPort = bridge.params.port

    internal fun initialize() {
        jobInitialize = bridge.scope.launch(bridge.handler) {
            if (!establishSSL()) return@launch

            if (!establishHttp()) return@launch

            bridge.controlMailbox.send(ControlMessage(Where.SSL, Result.PROCEEDED))
        }
    }

    private fun trustAllContext(): SSLContext = SSLContext.getInstance("TLS").also {
        it.init(null, arrayOf(
            object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<X509Certificate>?, authType: String?) {}
                override fun checkServerTrusted(chain: Array<X509Certificate>?, authType: String?) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
            }
        ), null)
    }

    private suspend fun fatal(where: Where, result: Result, supplement: String? = null): Boolean {
        bridge.controlMailbox.send(ControlMessage(where, result, supplement))
        return false
    }

    private fun readFully(buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val n = socketInputStream.read(buffer, offset, buffer.size - offset)
            if (n < 0) throw IOException("proxy closed the connection")
            offset += n
        }
    }

    /** SOCKS5 handshake against Tor (no authentication), keep-alive tunnel. */
    private suspend fun establishProxy(): Boolean {
        // method negotiation: client says "I offer NO-AUTH only"
        socketOutputStream.write(byteArrayOf(0x05, 0x01, 0x00))
        socketOutputStream.flush()

        val greeting = ByteArray(2)
        readFully(greeting)
        if (greeting[0] != 0x05.toByte()) {
            return fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, "not a SOCKS5 proxy")
        }
        if (greeting[1] != 0x00.toByte()) {
            return fatal(Where.PROXY, Result.ERR_AUTHENTICATION_FAILED, "SOCKS5 did not accept NO-AUTH")
        }

        // CONNECT to the VPN Gate endpoint; the hostname is never resolved
        // locally, Tor does it on the far side of the circuit.
        val request = mutableListOf<Byte>().apply {
            add(0x05); add(0x01); add(0x00)
            if (Regex("^[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3}$").matches(sslHostname)) {
                add(0x01)
                sslHostname.split('.').forEach { add(it.toInt().toByte()) }
            } else if (sslHostname.contains(':')) { // IPv6 literal, no resolver involved
                add(0x04)
                InetAddress.getByName(sslHostname).address.forEach { add(it) }
            } else {
                val name = sslHostname.toByteArray(Charsets.US_ASCII)
                add(0x03)
                add(name.size.toByte())
                name.forEach { add(it) }
            }
            add((sslPort shr 8).toByte())
            add(sslPort.toByte())
        }.toByteArray()

        socketOutputStream.write(request)
        socketOutputStream.flush()

        val head = ByteArray(4)
        readFully(head)
        if (head[0] != 0x05.toByte()) {
            return fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, "broken SOCKS5 reply")
        }
        if (head[1] != 0x00.toByte()) {
            return fatal(Where.PROXY, Result.ERR_UNEXPECTED_MESSAGE, "SOCKS5 CONNECT refused (rep=${head[1]})")
        }

        // skim the bound address so the stream sits exactly at the TLS bytes
        when (head[3]) {
            0x01.toByte() -> readFully(ByteArray(4))
            0x03.toByte() -> {
                val length = ByteArray(1)
                readFully(length)
                readFully(ByteArray(length[0].toInt() and 0xFF))
            }
            0x04.toByte() -> readFully(ByteArray(16))
        }
        readFully(ByteArray(2))

        return true
    }

    private suspend fun establishSSL(): Boolean {
        val sslContext = if (bridge.params.doVerifyCert) {
            SSLContext.getDefault()
        } else {
            trustAllContext()
        }

        engine = sslContext.createSSLEngine(sslHostname, sslPort)
        engine.useClientMode = true

        socket = Socket().also {
            it.connect(
                InetSocketAddress(bridge.params.proxyHostname, bridge.params.proxyPort),
                PROXY_CONNECT_TIMEOUT
            )
            socketInputStream = it.getInputStream()
            socketOutputStream = it.getOutputStream()
        }

        if (!establishProxy()) {
            return false
        }

        inboundBuffer = ByteBuffer.allocate(engine.session.packetBufferSize).also { it.limit(0) }
        outboundBuffer = ByteBuffer.allocate(engine.session.packetBufferSize)

        try {
            startSSLHandshake()
        } catch (e: CertificateException) {
            val cause = e.cause
            if (cause is CertPathValidatorException) {
                return fatal(Where.CERT_PATH, Result.ERR_VERIFICATION_FAILED, generateCertPathLog(cause))
            } else {
                throw e
            }
        }

        if (bridge.params.doVerifyHostname) {
            if (!HttpsURLConnection.getDefaultHostnameVerifier().verify(sslHostname, engine.session)) {
                return fatal(Where.SSL, Result.ERR_VERIFICATION_FAILED)
            }
        }

        return true
    }

    private fun generateCertPathLog(exception: CertPathValidatorException): String {
        var log = "[MESSAGE]\n${exception.message}\n\n"

        log += "[CERT PATH]\n"
        exception.certPath.certificates.forEachIndexed { i, cert ->
            log += "-----CERT at $i-----\n"
            log += "$cert\n"
            log += "-----END CERT-----\n\n"
        }

        log += "[FAILED CERT INDEX]\n"
        log += if (exception.index == -1) "NOT DEFINED" else exception.index.toString()

        return log
    }

    private suspend fun startSSLHandshake() {
        val tempBuffer = ByteBuffer.allocate(0)

        engine.beginHandshake()

        while (true) {
            yield()

            when (engine.handshakeStatus) {
                SSLEngineResult.HandshakeStatus.NEED_WRAP -> {
                    val result = send(tempBuffer)
                    if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.FINISHED) {
                        break
                    }
                }

                SSLEngineResult.HandshakeStatus.NEED_UNWRAP -> {
                    val result = receive(tempBuffer)
                    if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.FINISHED) {
                        break
                    }
                }

                else -> {
                    throw NotImplementedError(engine.handshakeStatus.name)
                }
            }
        }
    }

    private suspend fun establishHttp(): Boolean {
        val buffer = ByteBuffer.allocate(getApplicationBufferSize())

        val request = arrayOf(
            "SSTP_DUPLEX_POST /sra_{BA195980-CD49-458b-9E23-C84EE0ADCD75}/ HTTP/1.1",
            "Content-Length: 18446744073709551615",
            "Host: $sslHostname",
            "SSTPCORRELATIONID: {${bridge.guid}}"
        ).joinToString(separator = HTTP_DELIMITER, postfix = HTTP_SUFFIX).toByteArray(Charsets.US_ASCII)

        buffer.put(request)
        buffer.flip()

        send(buffer)

        buffer.position(0)
        buffer.limit(0)

        var response = ""
        outer@ while (true) {
            receive(buffer)

            for (i in 0 until buffer.remaining()) {
                response += buffer.get().toIntAsUByte().toChar()

                if (response.endsWith(HTTP_SUFFIX)) {
                    break@outer
                }
            }
        }

        if (!response.split(HTTP_DELIMITER)[0].contains("200")) {
            return fatal(Where.SSL, Result.ERR_UNEXPECTED_MESSAGE, response.split(HTTP_DELIMITER)[0])
        }

        socket!!.soTimeout = 1_000
        bridge.protect(socket)
        return true
    }

    internal fun getSession(): SSLSession {
        return engine.session
    }

    internal fun getServerCertificate(): ByteArray {
        return engine.session.peerCertificates[0].encoded
    }

    internal fun getApplicationBufferSize(): Int {
        return engine.session.applicationBufferSize
    }

    internal fun receive(buffer: ByteBuffer): SSLEngineResult {
        var startPayload: Int
        var result: SSLEngineResult

        while (true) {
            startPayload = buffer.position()
            buffer.position(buffer.limit())
            buffer.limit(buffer.capacity())

            result = engine.unwrap(inboundBuffer, buffer)

            when (result.status) {
                SSLEngineResult.Status.OK -> {
                    break
                }

                SSLEngineResult.Status.BUFFER_OVERFLOW -> {
                    buffer.limit(buffer.position())
                    buffer.position(startPayload)

                    buffer.slide()
                }

                SSLEngineResult.Status.BUFFER_UNDERFLOW -> {
                    buffer.limit(buffer.position())
                    buffer.position(startPayload)

                    inboundBuffer.slide()

                    try {
                        val readSize = socketInputStream.read(
                            inboundBuffer.array(),
                            inboundBuffer.limit(),
                            inboundBuffer.capacityAfterLimit
                        )

                        inboundBuffer.limit(inboundBuffer.limit() + readSize)
                    } catch (_: SocketTimeoutException) { }
                }

                else -> {
                    throw NotImplementedError(result.status.name)
                }
            }
        }

        buffer.limit(buffer.position())
        buffer.position(startPayload)

        return result
    }

    internal suspend fun send(buffer: ByteBuffer): SSLEngineResult {
        mutex.withLock {
            var result: SSLEngineResult

            while (true) {
                outboundBuffer.clear()

                result = engine.wrap(buffer, outboundBuffer)
                if (result.status != SSLEngineResult.Status.OK) {
                    throw NotImplementedError(result.status.name)
                }

                socketOutputStream.write(
                    outboundBuffer.array(),
                    0,
                    outboundBuffer.position()
                )

                if (!buffer.hasRemaining()) {
                    socketOutputStream.flush()

                    break
                }
            }

            return result
        }
    }

    internal fun close() {
        jobInitialize?.cancel()
        try {
            socket?.close()
        } catch (_: Exception) {
        }
    }
}

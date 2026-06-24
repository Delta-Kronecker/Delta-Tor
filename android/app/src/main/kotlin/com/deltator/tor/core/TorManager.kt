package com.deltator.tor.core

import android.content.Context
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.Socket
import java.security.MessageDigest
import javax.net.ssl.SSLContext

enum class TorState {
    IDLE, CONNECTING, CONNECTED, FAILED, STOPPED
}

class TorManager(
    private val context: Context,
    val label: String,
    private val socksPort: Int,
    private val ctrlPort: Int,
    private val httpPort: Int
) {
    private val _state = MutableStateFlow(TorState.IDLE)
    val state: StateFlow<TorState> = _state.asStateFlow()

    private val _bootstrapPercent = MutableStateFlow(0)
    val bootstrapPercent: StateFlow<Int> = _bootstrapPercent.asStateFlow()

    private val _statusText = MutableStateFlow("Idle")
    val statusText: StateFlow<String> = _statusText.asStateFlow()

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    private var torProcess: Process? = null
    private var readJob: Job? = null

    suspend fun start(
        torrcContent: String,
        ptDir: String,
        timeoutSeconds: Int = 180,
        onConnected: ((label: String, socksPort: Int, ctrlPort: Int, httpPort: Int) -> Unit)? = null
    ) {
        if (_state.value == TorState.CONNECTING || _state.value == TorState.CONNECTED) return

        _state.value = TorState.CONNECTING
        _bootstrapPercent.value = 0
        _statusText.value = "Starting..."
        _logs.value = emptyList()

        val dataDir = File(context.filesDir, "data_$socksPort").also { it.mkdirs() }

        copyGeoIpFiles(dataDir)

        val torrcFile = File(dataDir, "torrc")
        torrcFile.writeText(torrcContent)

        val torBinary = findTorBinary() ?: run {
            _state.value = TorState.FAILED
            _statusText.value = "tor binary not found"
            return
        }

        try {
            val pb = ProcessBuilder(torBinary, "-f", torrcFile.absolutePath)
                .directory(dataDir)
                .redirectErrorStream(true)

            val env = pb.environment()
            env["TOR_PT_STATE_LOCATION"] = File(dataDir, "pt_state").apply { mkdirs() }.absolutePath

            torProcess = pb.start()

            val reader = BufferedReader(InputStreamReader(torProcess!!.inputStream))

            withContext(Dispatchers.IO) {
                var lastPercent = -1
                var lastMoveTime = System.currentTimeMillis()
                val timeoutMs = timeoutSeconds * 1000L

                reader.useLines { lines ->
                    for (line in lines) {
                        addLog(line)

                        if (line.contains("Reading config failed") || line.contains("Failed to parse/validate config")) {
                            _statusText.value = "Config error"
                            _state.value = TorState.FAILED
                            return@useLines
                        }

                        val match = Regex("Bootstrapped (\\d+)%").find(line)
                        if (match != null) {
                            val pct = match.groupValues[1].toInt()
                            _bootstrapPercent.value = pct
                            _statusText.value = "Bootstrapped $pct%"
                            lastMoveTime = System.currentTimeMillis()

                            if (pct == 100 && _state.value != TorState.CONNECTED) {
                                _state.value = TorState.CONNECTED
                                _statusText.value = "Connected!"
                                onConnected?.invoke(label, socksPort, ctrlPort, httpPort)
                            }
                        }

                        if (_state.value == TorState.CONNECTING &&
                            lastPercent >= 0 &&
                            System.currentTimeMillis() - lastMoveTime > timeoutMs
                        ) {
                            _statusText.value = "Timeout at $lastPercent%"
                            _state.value = TorState.FAILED
                            stop()
                            return@useLines
                        }

                        if (match != null) lastPercent = match.groupValues[1].toInt()
                    }
                }
            }
        } catch (e: Exception) {
            addLog("Error: ${e.message}")
            _state.value = TorState.FAILED
            _statusText.value = "Launch error: ${e.message}"
        }
    }

    fun stop() {
        _state.value = TorState.STOPPED
        _statusText.value = "Stopped"
        _bootstrapPercent.value = 0

        try {
            torProcess?.destroy()
            torProcess?.waitFor(3, java.util.concurrent.TimeUnit.SECONDS)
            torProcess?.destroyForcibly()
        } catch (_: Exception) {}

        torProcess = null
    }

    fun isRunning(): Boolean = _state.value == TorState.CONNECTED || _state.value == TorState.CONNECTING

    fun isConnected(): Boolean = _state.value == TorState.CONNECTED

    private fun addLog(line: String) {
        val current = _logs.value.toMutableList()
        current.add(line)
        if (current.size > 500) {
            current.removeFirst()
        }
        _logs.value = current
    }

    private fun copyGeoIpFiles(dataDir: File) {
        val assetDir = File(context.filesDir, "tor_bundle")
        if (!assetDir.exists()) return

        listOf("geoip", "geoip6").forEach { name ->
            val src = File(assetDir, name)
            val dst = File(dataDir, name)
            if (src.exists() && !dst.exists()) {
                src.copyTo(dst)
            }
        }
    }

    private fun findTorBinary(): String? {
        val bundleDir = File(context.filesDir, "tor_bundle")
        val candidates = listOf(
            File(bundleDir, "tor/tor"),
            File(context.filesDir, "tor/tor"),
            File(bundleDir, "tor")
        )
        return candidates.firstOrNull { it.exists() && it.canExecute() }?.absolutePath
    }

    fun requestNewCircuit(): Boolean {
        return try {
            val cookieFile = File(context.filesDir, "data_$socksPort/control_auth_cookie")
            if (!cookieFile.exists()) return false

            val cookieHex = cookieFile.readBytes().joinToString("") { "%02x".format(it) }

            val socket = Socket("127.0.0.1", ctrlPort)
            socket.soTimeout = 5000

            socket.getOutputStream().write("AUTHENTICATE $cookieHex\r\n".toByteArray())
            socket.getInputStream().read(ByteArray(256))

            socket.getOutputStream().write("SIGNAL NEWNYM\r\n".toByteArray())
            val resp = ByteArray(256)
            socket.getInputStream().read(resp)

            socket.close()
            String(resp).contains("250")
        } catch (e: Exception) {
            false
        }
    }

    fun getExitIpAndCountry(): Pair<String, String> {
        return try {
            val socket = Socket("127.0.0.1", socksPort)
            socket.soTimeout = 15000

            socket.getOutputStream().write(byteArrayOf(0x05, 0x01, 0x00))
            val greeting = ByteArray(2)
            socket.getInputStream().read(greeting)

            val host = "check.torproject.org".toByteArray()
            socket.getOutputStream().write(
                byteArrayOf(0x05, 0x01, 0x00, 0x03, host.size.toByte()) +
                host + 443.toShort().toByteArrayBE()
            )

            val connectResp = ByteArray(10)
            socket.getInputStream().read(connectResp)

            val sslSocket = SSLContext.getDefault().socketFactory
                .createSocket(socket, "check.torproject.org", 443, true) as javax.net.ssl.SSLSocket

            sslSocket.outputStream.write(
                "GET /api/ip HTTP/1.1\r\nHost: check.torproject.org\r\nConnection: close\r\n\r\n".toByteArray()
            )

            val response = sslSocket.inputStream.readBytes().toString(Charsets.UTF_8)
            sslSocket.close()

            val ipMatch = Regex("\"IP\"\\s*:\\s*\"([^\"]+)\"").find(response)
            val ip = ipMatch?.groupValues?.get(1) ?: "unknown"

            Pair(ip, lookupCountry(ip))
        } catch (e: Exception) {
            Pair("—", "—")
        }
    }

    private fun lookupCountry(ip: String): String {
        val services = listOf(
            Triple("ipapi.co", "/$ip/json/", "country_code"),
            Triple("ip-api.com", "/json/$ip", "countryCode"),
            Triple("ipwho.is", "/$ip", "country_code")
        )
        for ((host, path, key) in services) {
            try {
                val socket = Socket("127.0.0.1", socksPort)
                socket.soTimeout = 12000

                socket.getOutputStream().write(byteArrayOf(0x05, 0x01, 0x00))
                socket.getInputStream().read(ByteArray(2))

                val hostBytes = host.toByteArray()
                socket.getOutputStream().write(
                    byteArrayOf(0x05, 0x01, 0x00, 0x03, hostBytes.size.toByte()) +
                    hostBytes + 443.toShort().toByteArrayBE()
                )
                socket.getInputStream().read(ByteArray(10))

                val sslSocket = SSLContext.getDefault().socketFactory
                    .createSocket(socket, host, 443, true) as javax.net.ssl.SSLSocket

                sslSocket.outputStream.write(
                    "GET $path HTTP/1.1\r\nHost: $host\r\nConnection: close\r\n\r\n".toByteArray()
                )

                val response = sslSocket.inputStream.readBytes().toString(Charsets.UTF_8)
                sslSocket.close()

                val match = Regex("\"$key\"\\s*:\\s*\"([^\"]+)\"").find(response)
                val code = match?.groupValues?.get(1) ?: ""
                if (code.length >= 2) return code.uppercase()
            } catch (_: Exception) {}
        }
        return "?"
    }

    private fun Short.toByteArrayBE(): ByteArray =
        byteArrayOf((toInt() shr 8).toByte(), toInt().toByte())

    fun destroy() {
        stop()
        try {
            File(context.filesDir, "data_$socksPort").deleteRecursively()
        } catch (_: Exception) {}
    }
}

package com.deltator.tor.core

import android.content.Context
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.BufferedReader
import java.io.File
import java.io.FileOutputStream
import java.io.InputStreamReader
import java.net.Socket
import java.util.zip.GZIPInputStream
import javax.net.ssl.SSLContext

enum class TorState {
    IDLE, CONNECTING, CONNECTED, FAILED, STOPPED
}

class TorManager(
    private val context: Context,
    val label: String,
    val socksPort: Int,
    val ctrlPort: Int,
    val httpPort: Int
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

    suspend fun start(
        torrcContent: String,
        ptDir: String,
        timeoutSeconds: Int = 180,
        onConnected: ((label: String, socksPort: Int, ctrlPort: Int, httpPort: Int) -> Unit)? = null,
        onLog: ((String) -> Unit)? = null
    ) {
        if (_state.value == TorState.CONNECTING || _state.value == TorState.CONNECTED) return

        _state.value = TorState.CONNECTING
        _bootstrapPercent.value = 0
        _statusText.value = "Starting..."
        _logs.value = emptyList()

        addLog("[DEBUG] === TorManager.start() called ===")
        onLogCallback = onLog
        addLog("[DEBUG] label=$label socksPort=$socksPort ctrlPort=$ctrlPort")
        addLog("[DEBUG] ptDir=$ptDir")
        addLog("[DEBUG] timeoutSeconds=$timeoutSeconds")

        val dataDir = File(context.filesDir, "data_$socksPort").also { it.mkdirs() }
        addLog("[DEBUG] dataDir=${dataDir.absolutePath} exists=${dataDir.exists()}")

        copyGeoIpFiles(dataDir)

        val torrcFile = File(dataDir, "torrc")
        torrcFile.writeText(torrcContent)
        addLog("[DEBUG] torrc written to ${torrcFile.absolutePath} size=${torrcFile.length()}")

        val torBinary = findTorBinary()
        if (torBinary == null) {
            _state.value = TorState.FAILED
            _statusText.value = "tor binary not found"
            addLog("[ERROR] No tor binary found!")
            addLog("[ERROR] Checked paths:")
            listOf("tor/libTor.so", "tor/tor", "tor/pluggable_transports/lyrebird").forEach { p ->
                val f = File(context.filesDir, p)
                addLog("[ERROR]   ${f.absolutePath} exists=${f.exists()} canExec=${f.canExecute()}")
            }
            addLog("[ERROR] filesDir contents:")
            context.filesDir.listFiles()?.forEach { f ->
                addLog("[ERROR]   ${f.name} ${if (f.isDirectory) "DIR" else "FILE ${f.length()}"}")
            }
            File(context.filesDir, "tor").listFiles()?.forEach { f ->
                addLog("[ERROR]   tor/${f.name} ${if (f.isDirectory) "DIR" else "FILE ${f.length()}"}")
            }
            return
        }

        addLog("[DEBUG] torBinary=$torBinary")
        addLog("[DEBUG] torBinary exists=${File(torBinary).exists()} canExecute=${File(torBinary).canExecute()}")
        addLog("[DEBUG] torBinary size=${File(torBinary).length()}")

        try {
            addLog("[DEBUG] Building ProcessBuilder...")
            val pb = ProcessBuilder(torBinary, "-f", torrcFile.absolutePath)
                .directory(dataDir)
                .redirectErrorStream(true)

            val env = pb.environment()
            val ptStateDir = File(dataDir, "pt_state").apply { mkdirs() }
            env["TOR_PT_STATE_LOCATION"] = ptStateDir.absolutePath
            addLog("[DEBUG] TOR_PT_STATE_LOCATION=${ptStateDir.absolutePath}")

            addLog("[DEBUG] Starting process...")
            torProcess = pb.start()
            addLog("[DEBUG] Process started, pid=${torProcess?.toString()}")

            val reader = BufferedReader(InputStreamReader(torProcess!!.inputStream))

            withContext(Dispatchers.IO) {
                var lastPercent = -1
                var lastMoveTime = System.currentTimeMillis()
                val timeoutMs = timeoutSeconds * 1000L
                var lineCount = 0

                reader.useLines { lines ->
                    for (line in lines) {
                        lineCount++
                        addLog(line)

                        if (line.contains("Reading config failed") || line.contains("Failed to parse/validate config")) {
                            _statusText.value = "Config error"
                            _state.value = TorState.FAILED
                            addLog("[ERROR] Config parse failed!")
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
                            addLog("[ERROR] Timeout at $lastPercent%")
                            stop()
                            return@useLines
                        }

                        if (match != null) lastPercent = match.groupValues[1].toInt()
                    }
                }
                addLog("[DEBUG] Process exited. Total lines: $lineCount")
            }

            val exitCode = torProcess?.waitFor()
            addLog("[DEBUG] Process exit code: $exitCode")
            if (_state.value == TorState.CONNECTING) {
                _state.value = TorState.FAILED
                _statusText.value = "Process exited with code $exitCode"
            }
        } catch (e: Exception) {
            addLog("[ERROR] Exception: ${e.javaClass.simpleName}: ${e.message}")
            e.stackTrace.forEach { addLog("[ERROR]   at ${it}") }
            _state.value = TorState.FAILED
            _statusText.value = "Error: ${e.message}"
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

    private var onLogCallback: ((String) -> Unit)? = null

    private fun addLog(line: String) {
        val current = _logs.value.toMutableList()
        current.add(line)
        if (current.size > 500) current.removeFirst()
        _logs.value = current
        onLogCallback?.invoke(line)
    }

    private fun copyGeoIpFiles(dataDir: File) {
        val geoipSrc = File(context.filesDir, "data/geoip")
        val geoip6Src = File(context.filesDir, "data/geoip6")

        listOf("geoip" to geoipSrc, "geoip6" to geoip6Src).forEach { (name, src) ->
            val dst = File(dataDir, name)
            if (src.exists() && !dst.exists()) {
                src.copyTo(dst)
            }
        }
    }

    private fun findTorBinary(): String? {
        val candidates = listOf(
            File(context.filesDir, "tor/libTor.so"),
            File(context.filesDir, "tor/tor"),
            File(context.filesDir, "tor/pluggable_transports/lyrebird")
        )
        val found = candidates.firstOrNull { it.exists() }
        if (found != null) {
            try {
                Runtime.getRuntime().exec(arrayOf("chmod", "755", found.absolutePath)).waitFor()
            } catch (_: Exception) {}
        }
        return found?.absolutePath
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
            Pair("\u2014", "\u2014")
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

package com.deltator.tor.core

import android.content.Context
import android.content.Intent
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.torproject.android.services.TorService
import org.torproject.android.services.TorServiceLocalBroadcastReceiver
import android.content.BroadcastReceiver
import android.os.Handler
import android.os.Looper
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.Socket
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

    private var broadcastReceiver: BroadcastReceiver? = null
    private val handler = Handler(Looper.getMainLooper())

    suspend fun start(
        torrcContent: String,
        ptDir: String,
        timeoutSeconds: Int = 180,
        onConnected: ((label: String, socksPort: Int, ctrlPort: Int, httpPort: Int) -> Unit)? = null
    ) {
        if (_state.value == TorState.CONNECTING || _state.value == TorState.CONNECTED) return

        _state.value = TorState.CONNECTING
        _bootstrapPercent.value = 0
        _statusText.value = "Starting Tor..."
        _logs.value = emptyList()

        try {
            extractAndPrepareTor()

            writeTorrc(torrcContent)

            registerReceiver(onConnected)

            startTorService()

            waitForBootstrap(timeoutSeconds)
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
            unregisterReceiver()
            val intent = Intent(context, TorService::class.java)
            intent.action = TorService.ACTION_STOP
            context.startService(intent)
        } catch (_: Exception) {}

        handler.postDelayed({
            try {
                val intent = Intent(context, TorService::class.java)
                intent.action = TorService.ACTION_STOP
                context.startService(intent)
            } catch (_: Exception) {}
        }, 3000)
    }

    fun isRunning(): Boolean = _state.value == TorState.CONNECTED || _state.value == TorState.CONNECTING
    fun isConnected(): Boolean = _state.value == TorState.CONNECTED

    private fun extractAndPrepareTor() {
        val torDir = File(context.filesDir, "tor")
        if (torDir.exists() && File(torDir, "tor").exists()) return

        try {
            val inputStream = context.assets.open("tor-expert-bundle-android-aarch64-15.0.16.tar.gz")
            val tarIn = java.util.zip.GZIPInputStream(inputStream)
            val tarArchive = org.apache.commons.compress.archivers.tar.TarArchiveInputStream(tarIn)

            var entry = tarArchive.nextTarEntry
            while (entry != null) {
                val outFile = File(context.filesDir, entry.name)
                if (entry.isDirectory) {
                    outFile.mkdirs()
                } else {
                    outFile.parentFile?.mkdirs()
                    outFile.outputStream().use { output ->
                        tarArchive.copyTo(output)
                    }
                }
                entry = tarArchive.nextTarEntry
            }
            tarArchive.close()

            chmodBinary(File(context.filesDir, "tor/tor"))
            chmodBinary(File(context.filesDir, "tor/pluggable_transports/lyrebird"))
            chmodBinary(File(context.filesDir, "tor/pluggable_transports/conjure-client"))

            addLog("[Bundle] Tor bundle extracted successfully")
        } catch (e: Exception) {
            addLog("[Bundle] Extraction failed: ${e.message}")
        }
    }

    private fun chmodBinary(file: File) {
        if (file.exists()) {
            try {
                Runtime.getRuntime().exec(arrayOf("chmod", "755", file.absolutePath)).waitFor()
            } catch (_: Exception) {}
        }
    }

    private fun writeTorrc(content: String) {
        val torrcDir = File(context.filesDir, "torrc_dir")
        torrcDir.mkdirs()
        val torrcFile = File(torrcDir, "torrc")
        torrcFile.writeText(content)
    }

    private fun registerReceiver(onConnected: ((String, Int, Int, Int) -> Unit)?) {
        broadcastReceiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context?, intent: Intent?) {
                if (intent?.action == TorServiceLocalBroadcastReceiver.BROADCAST_TOR_STATUS) {
                    val status = intent.getStringExtra(TorServiceLocalBroadcastReceiver.TOR_STATUS_EXTRA)
                    handleTorStatus(status, onConnected)
                }
            }
        }
        val filter = android.content.IntentFilter(TorServiceLocalBroadcastReceiver.BROADCAST_TOR_STATUS)
        context.registerReceiver(broadcastReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
    }

    private fun unregisterReceiver() {
        try {
            broadcastReceiver?.let { context.unregisterReceiver(it) }
        } catch (_: Exception) {}
        broadcastReceiver = null
    }

    private fun handleTorStatus(status: String?, onConnected: ((String, Int, Int, Int) -> Unit)?) {
        when (status) {
            TorService.STATUS_STARTING -> {
                _statusText.value = "Starting..."
                addLog("[Tor] Starting...")
            }
            TorService.STATUS_ON -> {
                _bootstrapPercent.value = 100
                _statusText.value = "Connected!"
                _state.value = TorState.CONNECTED
                addLog("[Tor] Connected!")
                onConnected?.invoke(label, socksPort, ctrlPort, httpPort)
            }
            TorService.STATUS_OFF -> {
                if (_state.value == TorState.CONNECTING) {
                    _state.value = TorState.FAILED
                    _statusText.value = "Tor stopped unexpectedly"
                }
            }
            TorService.STATUS_STOPPING -> {
                _statusText.value = "Stopping..."
            }
            else -> {
                addLog("[Tor] Status: $status")
            }
        }
    }

    private suspend fun waitForBootstrap(timeoutSeconds: Int) {
        val startTime = System.currentTimeMillis()
        val timeoutMs = timeoutSeconds * 1000L

        while (_state.value == TorState.CONNECTING) {
            if (System.currentTimeMillis() - startTime > timeoutMs) {
                _statusText.value = "Timeout waiting for bootstrap"
                _state.value = TorState.FAILED
                stop()
                return
            }
            delay(1000)
        }
    }

    private fun addLog(line: String) {
        val current = _logs.value.toMutableList()
        current.add(line)
        if (current.size > 500) current.removeFirst()
        _logs.value = current
    }

    fun requestNewCircuit(): Boolean {
        return try {
            val ctrlSocket = Socket("127.0.0.1", ctrlPort)
            ctrlSocket.soTimeout = 5000
            ctrlSocket.getOutputStream().write("SIGNAL NEWNYM\r\n".toByteArray())
            val resp = ByteArray(256)
            ctrlSocket.getInputStream().read(resp)
            ctrlSocket.close()
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
    }
}

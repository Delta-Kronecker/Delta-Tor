package com.deltator.tor.core

import android.content.Context
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

data class SlotState(
    val label: String,
    val def: SlotDef,
    val state: TorState = TorState.IDLE,
    val bootstrapPercent: Int = 0,
    val statusText: String = "Idle",
    val healthText: String = "\u25CF \u2014",
    val healthOk: Boolean = false,
    val avgLatency: Float = 0f,
    val lastLatency: Float = 0f,
    val enabled: Boolean = true
)

class MultiConnectManager(
    private val context: Context,
    private val settings: SettingsManager,
    private val bridgeManager: BridgeManager
) {
    private val _slots = MutableStateFlow<List<SlotState>>(emptyList())
    val slots: StateFlow<List<SlotState>> = _slots.asStateFlow()

    private val _isRunning = MutableStateFlow(false)
    val isRunning: StateFlow<Boolean> = _isRunning.asStateFlow()

    private val _activeProxyLabel = MutableStateFlow<String?>(null)
    val activeProxyLabel: StateFlow<String?> = _activeProxyLabel.asStateFlow()

    fun setActiveProxy(label: String?) {
        _activeProxyLabel.value = label
    }

    private val torManagers = mutableMapOf<String, TorManager>()
    private val healthJobs = mutableMapOf<String, Job>()
    private val pingHistory = mutableMapOf<String, MutableList<Float>>()
    private val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())

    fun initialize(savedSlots: List<SlotDef>) {
        _slots.value = savedSlots.map { SlotState(label = it.name, def = it) }
    }

    fun startAll() {
        if (_isRunning.value) return
        _isRunning.value = true

        _slots.value.forEachIndexed { index, slot ->
            if (slot.enabled) {
                startSlotInternal(slot.def, index)
            }
        }
    }

    fun stopAll() {
        _isRunning.value = false
        healthJobs.values.forEach { it.cancel() }
        healthJobs.clear()
        pingHistory.clear()

        torManagers.values.forEach { it.destroy() }
        torManagers.clear()

        _slots.value = _slots.value.map {
            it.copy(
                state = TorState.IDLE,
                bootstrapPercent = 0,
                statusText = "Stopped",
                healthText = "\u25CF \u2014",
                healthOk = false
            )
        }

        _activeProxyLabel.value = null
    }

    fun startSlot(label: String) {
        val idx = _slots.value.indexOfFirst { it.label == label }
        if (idx < 0) return
        val slot = _slots.value[idx]
        if (!slot.enabled) return
        startSlotInternal(slot.def, idx)
    }

    fun stopSlot(label: String) {
        healthJobs.remove(label)?.cancel()
        pingHistory.remove(label)
        torManagers.remove(label)?.destroy()

        _slots.value = _slots.value.map {
            if (it.label == label) it.copy(
                state = TorState.STOPPED,
                bootstrapPercent = 0,
                statusText = "Stopped"
            ) else it
        }
    }

    fun toggleSlot(label: String) {
        val idx = _slots.value.indexOfFirst { it.label == label }
        if (idx < 0) return

        val current = _slots.value[idx]
        val newEnabled = !current.enabled
        _slots.value = _slots.value.toMutableList().also { it[idx] = current.copy(enabled = newEnabled) }

        if (_isRunning.value) {
            if (newEnabled) {
                startSlotInternal(current.def, idx)
            } else {
                stopSlot(label)
            }
        }
    }

    fun deleteSlot(label: String) {
        stopSlot(label)
        _slots.value = _slots.value.filter { it.label != label }
    }

    fun addSlot(def: SlotDef) {
        _slots.value = _slots.value + SlotState(label = def.name, def = def)
    }

    fun retrySlot(label: String) {
        stopSlot(label)
        startSlot(label)
    }

    fun getSlotLogs(label: String): List<String> {
        return torManagers[label]?.logs?.value ?: emptyList()
    }

    fun saveSlotDefs() {
        val defs = _slots.value.map { it.def }
        settings.setString("multi_slots", defs.joinToString("\n") {
            "${it.name}|${it.source}|${it.category ?: ""}|${it.transport ?: ""}|${it.ip ?: ""}|${it.noBridge}"
        })
    }

    fun loadSlotDefs(): List<SlotDef> {
        val saved = settings.getString("multi_slots")
        if (saved.isBlank()) return defaultSlots()
        return saved.lines().filter { it.isNotBlank() }.map { line ->
            val parts = line.split("|")
            SlotDef(
                name = parts.getOrElse(0) { "Mode" },
                source = parts.getOrElse(1) { "Delta-Kronecker" },
                category = parts.getOrElse(2) { "" }.ifBlank { null },
                transport = parts.getOrElse(3) { "obfs4" }.ifBlank { null },
                ip = parts.getOrElse(4) { "IPv4" }.ifBlank { null },
                noBridge = parts.getOrElse(5) { "false" }.toBooleanStrictOrNull() ?: false
            )
        }
    }

    private fun startSlotInternal(def: SlotDef, index: Int) {
        val (socks, ctrl, http) = Config.slotPorts(index)
        val tor = TorManager(context, def.name, socks, ctrl, http)
        torManagers[def.name] = tor

        updateSlotState(def.name, TorState.CONNECTING, 0, "Starting...")

        scope.launch {
            val ptDir = File(context.filesDir, "tor_bundle/tor/pluggable_transports").absolutePath
            val dataDir = File(context.filesDir, "data_$socks").absolutePath

            val bridgeLines = resolveBridges(def)
            val torrc = settings.generateTorrc(dataDir, socks, ctrl, bridgeLines, ptDir)

            tor.start(torrc, ptDir, settings.getInt("auto_connect_timeout")) { label, sPort, cPort, hPort ->
                startHealthLoop(label, sPort)
            }

            tor.state.collect { state ->
                when (state) {
                    TorState.CONNECTED -> updateSlotState(def.name, TorState.CONNECTED, 100, "Connected!")
                    TorState.FAILED -> updateSlotState(def.name, TorState.FAILED, 0, "Failed")
                    TorState.STOPPED -> updateSlotState(def.name, TorState.STOPPED, 0, "Stopped")
                    else -> {}
                }
            }
        }
    }

    private fun resolveBridges(def: SlotDef): List<String> {
        if (def.noBridge) return emptyList()

        return when (def.source) {
            "Default (Built-in)" -> bridgeManager.getBuiltInBridges(def.transport ?: "snowflake")
            "Direct (No Bridge)" -> emptyList()
            else -> {
                val cat = def.category ?: "Tested & Active"
                val trans = def.transport ?: "obfs4"
                val ip = def.ip ?: "IPv4"

                if (settings.getBoolean("use_custom_bridges")) {
                    bridgeManager.getCustomBridges(
                        settings.getString("custom_bridges"),
                        settings.getInt("bridges_in_torrc"),
                        settings.getBoolean("shuffle_bridges")
                    )
                } else {
                    bridgeManager.getBridgeLines(
                        cat, trans, ip,
                        settings.getInt("bridges_in_torrc"),
                        settings.getBoolean("shuffle_bridges")
                    )
                }
            }
        }
    }

    private fun startHealthLoop(label: String, socksPort: Int) {
        val job = scope.launch {
            while (isActive) {
                delay(15000)

                val tor = torManagers[label] ?: break
                if (!tor.isConnected()) continue

                val (ok, latency) = checkHealth(socksPort)

                synchronized(pingHistory) {
                    val history = pingHistory.getOrPut(label) { mutableListOf() }
                    history.add(latency)
                    if (history.size > 20) history.removeFirst()
                }

                val avgLatency = synchronized(pingHistory) {
                    val history = pingHistory[label] ?: emptyList()
                    if (history.isNotEmpty()) history.average().toFloat() else 0f
                }

                val healthText = if (ok) {
                    "\u25CF Online  ${latency.toInt()} ms  (avg ${avgLatency.toInt()} ms)"
                } else {
                    "\u25CF Offline  (avg ${avgLatency.toInt()} ms)"
                }

                updateSlotHealth(label, healthText, ok, avgLatency, latency)
            }
        }
        healthJobs[label] = job
    }

    private fun checkHealth(socksPort: Int, timeoutMs: Long = 15000): Pair<Boolean, Float> {
        return try {
            val start = System.currentTimeMillis()
            val socket = java.net.Socket()
            socket.connect(java.net.InetSocketAddress("127.0.0.1", socksPort), timeoutMs.toInt())
            socket.soTimeout = timeoutMs.toInt()

            socket.getOutputStream().write(byteArrayOf(0x05, 0x01, 0x00))
            val greeting = ByteArray(2)
            socket.getInputStream().read(greeting)
            if (greeting[1] != 0x00.toByte()) return Pair(false, timeoutMs.toFloat())

            val host = Config.CHECK_HOST.toByteArray()
            socket.getOutputStream().write(
                byteArrayOf(0x05, 0x01, 0x00, 0x03, host.size.toByte()) +
                host + 443.toShort().toByteArrayBE()
            )
            val connectResp = ByteArray(10)
            socket.getInputStream().read(connectResp)
            if (connectResp[1] != 0x00.toByte()) return Pair(false, timeoutMs.toFloat())

            val sslSocket = javax.net.ssl.SSLContext.getDefault().socketFactory
                .createSocket(socket, Config.CHECK_HOST, 443, true) as javax.net.ssl.SSLSocket

            sslSocket.outputStream.write(
                "GET ${Config.CHECK_PATH} HTTP/1.1\r\nHost: ${Config.CHECK_HOST}\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n".toByteArray()
            )

            val resp = ByteArray(512)
            val read = sslSocket.inputStream.read(resp)
            val latency = (System.currentTimeMillis() - start).toFloat()
            sslSocket.close()

            if (read > 0) {
                val respStr = String(resp, 0, read)
                Pair(respStr.contains("204") || respStr.contains("HTTP/1."), latency)
            } else {
                Pair(false, timeoutMs.toFloat())
            }
        } catch (e: Exception) {
            Pair(false, timeoutMs.toFloat())
        }
    }

    private fun Short.toByteArrayBE(): ByteArray =
        byteArrayOf((toInt() shr 8).toByte(), toInt().toByte())

    private fun updateSlotState(label: String, state: TorState, pct: Int, text: String) {
        _slots.value = _slots.value.map {
            if (it.label == label) it.copy(
                state = state,
                bootstrapPercent = pct,
                statusText = text
            ) else it
        }
    }

    private fun updateSlotHealth(label: String, text: String, ok: Boolean, avg: Float, last: Float) {
        _slots.value = _slots.value.map {
            if (it.label == label) it.copy(
                healthText = text,
                healthOk = ok,
                avgLatency = avg,
                lastLatency = last
            ) else it
        }
    }

    private fun defaultSlots() = listOf(
        SlotDef("Snowflake", "Default (Built-in)", null, "snowflake", null, false),
        SlotDef("obfs4 \u00b7 Tested IPv4", "Delta-Kronecker", "Tested & Active", "obfs4", "IPv4", false),
        SlotDef("Vanilla \u00b7 Tested IPv4", "Delta-Kronecker", "Tested & Active", "vanilla", "IPv4", false),
        SlotDef("WebTunnel \u00b7 Tested", "Delta-Kronecker", "Tested & Active", "webtunnel", "IPv4", false)
    )

    fun destroy() {
        scope.cancel()
        torManagers.values.forEach { it.destroy() }
    }
}

package com.deltator.tor.viewmodel

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.deltator.tor.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import java.io.File
import java.io.FileOutputStream

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val context = application
    val settings = SettingsManager(application)
    val bridgeManager = BridgeManager(application)
    val proxyManager = ProxyManager()

    private var torManager: TorManager? = null
    private var autoConnectActive = false

    val sourceOptions = listOf("Default (Built-in)", "Delta-Kronecker Tor-Bridges-Collector", "Custom Bridges")
    val categoryOptions = listOf("Tested & Active", "Fresh (72h)", "Full Archive")
    val transportOptions = listOf("obfs4", "webtunnel", "vanilla")
    val ipOptions = listOf("IPv4", "IPv6", "Both")

    private val _selectedSource = MutableStateFlow("Delta-Kronecker Tor-Bridges-Collector")
    val selectedSource: StateFlow<String> = _selectedSource.asStateFlow()

    private val _selectedCategory = MutableStateFlow("Tested & Active")
    val selectedCategory: StateFlow<String> = _selectedCategory.asStateFlow()

    private val _selectedTransport = MutableStateFlow("obfs4")
    val selectedTransport: StateFlow<String> = _selectedTransport.asStateFlow()

    private val _selectedIp = MutableStateFlow("IPv4")
    val selectedIp: StateFlow<String> = _selectedIp.asStateFlow()

    private val _noBridge = MutableStateFlow(false)
    val noBridge: StateFlow<Boolean> = _noBridge.asStateFlow()

    private val _connectionProgress = MutableStateFlow(0)
    val connectionProgress: StateFlow<Int> = _connectionProgress.asStateFlow()

    private val _statusText = MutableStateFlow("Initializing...")
    val statusText: StateFlow<String> = _statusText.asStateFlow()

    private val _isConnected = MutableStateFlow(false)
    val isConnected: StateFlow<Boolean> = _isConnected.asStateFlow()

    private val _isProxyEnabled = MutableStateFlow(false)
    val isProxyEnabled: StateFlow<Boolean> = _isProxyEnabled.asStateFlow()

    private val _exitIp = MutableStateFlow("\u2014")
    val exitIp: StateFlow<String> = _exitIp.asStateFlow()

    private val _country = MutableStateFlow("\u2014")
    val country: StateFlow<String> = _country.asStateFlow()

    private val _uptime = MutableStateFlow("\u2014")
    val uptime: StateFlow<String> = _uptime.asStateFlow()

    private val _torStatus = MutableStateFlow("\u2014")
    val torStatus: StateFlow<String> = _torStatus.asStateFlow()

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    private val _bridgeCount = MutableStateFlow("")
    val bridgeCount: StateFlow<String> = _bridgeCount.asStateFlow()

    private val _bridgeUpdated = MutableStateFlow("")
    val bridgeUpdated: StateFlow<String> = _bridgeUpdated.asStateFlow()

    private val _bridgeDownloadProgress = MutableStateFlow("")
    val bridgeDownloadProgress: StateFlow<String> = _bridgeDownloadProgress.asStateFlow()

    private var startTime: Long = 0
    private var uptimeJob: Job? = null
    private var keepAliveJob: Job? = null
    private var watchdogJob: Job? = null
    private var autoTestJob: Job? = null

    init {
        viewModelScope.launch { initialize() }
    }

    private suspend fun initialize() {
        addLog("[Init] DeltaTor starting...")
        addLog("[Init] filesDir: ${context.filesDir.absolutePath}")
        _statusText.value = "Extracting Tor bundle..."
        extractTorBundle()

        _statusText.value = "Downloading bridges..."
        bridgeManager.downloadAllBridges { current, total, msg ->
            _bridgeDownloadProgress.value = msg
        }

        _statusText.value = "Ready"
        refreshBridgeInfo()
    }

    private fun extractTorBundle() {
        val torDir = File(context.filesDir, "tor")
        if (torDir.exists() && File(torDir, "libTor.so").exists()) {
            addLog("[Bundle] Tor bundle already extracted")
            return
        }

        addLog("[Bundle] Starting extraction...")
        val success = SimpleTarExtractor.extractTarGz(
            context,
            "tor-expert-bundle-android-aarch64-15.0.16.tar.gz",
            context.filesDir
        ) { addLog(it) }

        if (success) {
            listOf(
                File(context.filesDir, "tor/libTor.so"),
                File(context.filesDir, "tor/pluggable_transports/lyrebird"),
                File(context.filesDir, "tor/pluggable_transports/conjure-client")
            ).forEach { bin ->
                if (bin.exists()) {
                    try {
                        Runtime.getRuntime().exec(arrayOf("chmod", "755", bin.absolutePath)).waitFor()
                        addLog("[Bundle] chmod +x ${bin.name}")
                    } catch (_: Exception) {}
                }
            }

            val ptConfig = File(context.filesDir, "tor/pluggable_transports/pt_config.json")
            if (ptConfig.exists()) {
                ptConfig.copyTo(File(context.filesDir, "pt_config.json"), overwrite = true)
            }
            addLog("[Bundle] Extraction complete!")
        } else {
            addLog("[Bundle] EXTRACTION FAILED!")
        }
    }

    fun setSelectedSource(source: String) {
        _selectedSource.value = source
        refreshBridgeInfo()
    }

    fun setSelectedCategory(cat: String) { _selectedCategory.value = cat; refreshBridgeInfo() }
    fun setSelectedTransport(trans: String) { _selectedTransport.value = trans; refreshBridgeInfo() }
    fun setSelectedIp(ip: String) { _selectedIp.value = ip; refreshBridgeInfo() }
    fun setNoBridge(no: Boolean) { _noBridge.value = no; refreshBridgeInfo() }

    fun getAvailableTransports(): List<String> {
        return when (_selectedSource.value) {
            "Default (Built-in)" -> listOf("obfs4", "snowflake", "meek")
            "Custom Bridges" -> listOf("obfs4", "webtunnel", "vanilla")
            else -> listOf("obfs4", "webtunnel", "vanilla")
        }
    }

    fun refreshBridgeInfo() {
        val src = _selectedSource.value
        if (src == "Default (Built-in)") {
            _bridgeCount.value = "${bridgeManager.getBuiltInBridges(_selectedTransport.value).size}"
            _bridgeUpdated.value = "Built-in"
            return
        }

        if (src == "Custom Bridges") {
            val custom = settings.getString("custom_bridges")
            _bridgeCount.value = "${custom.lines().count { it.isNotBlank() }}"
            _bridgeUpdated.value = "Custom"
            return
        }

        val count = bridgeManager.getBridgeCountForSelection(
            _selectedCategory.value, _selectedTransport.value, _selectedIp.value
        )
        _bridgeCount.value = if (count > 0) "$count" else "\u2014"

        val lastMod = bridgeManager.getLastModified(
            _selectedCategory.value, _selectedTransport.value, _selectedIp.value
        )
        _bridgeUpdated.value = if (lastMod > 0) {
            java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", java.util.Locale.getDefault())
                .format(java.util.Date(lastMod))
        } else "\u2014"
    }

    fun startConnect() {
        if (torManager?.isRunning() == true) return
        addLog("[Connect] Starting manual connection...")
        resetStats()

        viewModelScope.launch {
            val tor = createTorManager()
            torManager = tor
            _statusText.value = "Starting Tor..."

            val ptDir = File(context.filesDir, "tor/pluggable_transports").absolutePath
            val dataDir = context.filesDir.absolutePath

            val bridgeLines = getBridgeLines()
            val torrc = settings.generateTorrc(dataDir, Config.SOCKS_PORT, Config.CTRL_PORT, bridgeLines, ptDir)

            addLog("[Tor] Config generated, starting TorService...")
            addLog("[Tor] SOCKS: ${Config.SOCKS_PORT}  HTTP: ${Config.HTTP_PROXY_PORT}")

            tor.start(torrc, ptDir, settings.getInt("auto_connect_timeout"),
                onConnected = null,
                onLog = { line -> addLog(line) }
            )

            tor.state.collect { state ->
                when (state) {
                    TorState.CONNECTED -> {
                        _isConnected.value = true
                        _connectionProgress.value = 100
                        _statusText.value = "Tor is fully connected."
                        _torStatus.value = "Connected"
                        startTime = System.currentTimeMillis()
                        startUptimeTicker()
                        if (settings.getBoolean("auto_proxy_on_connect")) enableProxy()
                        startAutoTest()
                        startWatchdog()
                        startKeepAlive()
                        testConnection()
                    }
                    TorState.FAILED -> {
                        _statusText.value = "Connection failed."
                        _torStatus.value = "Failed"
                    }
                    else -> {}
                }
            }
        }
    }

    fun startAutoConnect() {
        if (autoConnectActive) return
        autoConnectActive = true
        addLog("[Auto] Starting auto-connect...")
        resetStats()

        viewModelScope.launch {
            val lastSuccess = settings.getLastSuccess()
            if (lastSuccess != null) {
                _statusText.value = "Auto: trying memory config..."
                addLog("[Auto] Trying last successful: ${lastSuccess.first}/${lastSuccess.second}/${lastSuccess.third}")
                if (tryAutoConfig(lastSuccess.first, lastSuccess.second, lastSuccess.third)) {
                    addLog("[Auto] Connected with memory config!")
                    return@launch
                }
            }

            val sequence = Config.AUTO_SEQUENCE.filter { (cat, trans, ip) ->
                lastSuccess?.let { !(it.first == cat && it.second == trans && it.third == ip) } ?: true
            }

            for ((index, triple) in sequence.withIndex()) {
                if (!autoConnectActive) break
                val (cat, trans, ip) = triple
                _statusText.value = "Auto: [${index + 1}/${sequence.size}] $cat/$trans/$ip"
                addLog("[Auto] Trying ${index + 1}/${sequence.size}: $cat / $trans / $ip")

                if (tryAutoConfig(cat, trans, ip)) {
                    addLog("[Auto] Connected with $cat / $trans / $ip")
                    return@launch
                }
            }

            _statusText.value = "Auto-connect failed. Try updating bridges."
            addLog("[Auto] All bridge groups exhausted.")
            autoConnectActive = false
        }
    }

    private suspend fun tryAutoConfig(cat: String, trans: String, ip: String): Boolean {
        return try {
            stopConnect()
            delay(1000)

            val tor = createTorManager()
            torManager = tor

            val ptDir = File(context.filesDir, "tor/pluggable_transports").absolutePath
            val dataDir = context.filesDir.absolutePath

            val bridgeLines = bridgeManager.getBridgeLines(
                cat, trans, ip,
                settings.getInt("bridges_in_torrc"),
                settings.getBoolean("shuffle_bridges")
            )
            val torrc = settings.generateTorrc(dataDir, Config.SOCKS_PORT, Config.CTRL_PORT, bridgeLines, ptDir)

            val result = withTimeoutOrNull(settings.getInt("auto_connect_timeout") * 1000L) {
                tor.start(torrc, ptDir, settings.getInt("auto_connect_timeout"))
                tor.state.first { it == TorState.CONNECTED || it == TorState.FAILED }
            }

            if (result == TorState.CONNECTED) {
                settings.saveLastSuccess(cat, trans, ip)
                true
            } else {
                tor.stop()
                tor.destroy()
                false
            }
        } catch (e: Exception) {
            false
        }
    }

    fun stopConnect() {
        autoConnectActive = false
        keepAliveJob?.cancel()
        watchdogJob?.cancel()
        autoTestJob?.cancel()
        uptimeJob?.cancel()

        torManager?.stop()
        torManager?.destroy()
        torManager = null

        if (_isProxyEnabled.value) disableProxy()
        proxyManager.stopHttpProxy()

        _isConnected.value = false
        _connectionProgress.value = 0
        _statusText.value = "Tor stopped."
        _torStatus.value = "\u2014"
        _exitIp.value = "\u2014"
        _country.value = "\u2014"
        _uptime.value = "\u2014"
    }

    fun testConnection() {
        viewModelScope.launch(Dispatchers.IO) {
            addLog("[Test] Checking connection...")
            val tor = torManager ?: return@launch
            val (ip, country) = tor.getExitIpAndCountry()
            _exitIp.value = ip
            _country.value = country
            addLog("[Test] Exit IP: $ip  Country: $country")
        }
    }

    fun requestNewCircuit() {
        viewModelScope.launch(Dispatchers.IO) {
            val tor = torManager ?: return@launch
            val success = tor.requestNewCircuit()
            if (success) {
                addLog("[Circuit] New circuit requested")
                _statusText.value = "New circuit obtained."
                delay(2000)
                testConnection()
            } else {
                addLog("[Circuit] Request failed")
            }
        }
    }

    fun toggleProxy() {
        if (_isProxyEnabled.value) disableProxy() else enableProxy()
    }

    private fun enableProxy() {
        proxyManager.startHttpProxy(Config.HTTP_PROXY_PORT, Config.SOCKS_PORT)
        _isProxyEnabled.value = true
        addLog("[Proxy] System proxy enabled. HTTP: 127.0.0.1:${Config.HTTP_PROXY_PORT}")
    }

    private fun disableProxy() {
        proxyManager.stopHttpProxy()
        _isProxyEnabled.value = false
        addLog("[Proxy] System proxy disabled.")
    }

    fun updateBridges() {
        viewModelScope.launch {
            _bridgeDownloadProgress.value = "Downloading bridges..."
            bridgeManager.downloadAllBridges { current, total, msg ->
                _bridgeDownloadProgress.value = msg
            }
            _bridgeDownloadProgress.value = ""
            refreshBridgeInfo()
        }
    }

    fun saveLog(): String? {
        return try {
            val dir = File(context.filesDir, "logs").also { it.mkdirs() }
            val stamp = java.text.SimpleDateFormat("yyyyMMdd_HHmmss", java.util.Locale.getDefault())
                .format(java.util.Date())
            val file = File(dir, "tor_log_$stamp.txt")
            file.writeText(_logs.value.joinToString("\n"))
            file.absolutePath
        } catch (e: Exception) {
            null
        }
    }

    private fun startUptimeTicker() {
        uptimeJob?.cancel()
        uptimeJob = viewModelScope.launch {
            while (isActive) {
                if (startTime > 0) {
                    val elapsed = ((System.currentTimeMillis() - startTime) / 1000).toInt()
                    val h = elapsed / 3600
                    val m = (elapsed % 3600) / 60
                    val s = elapsed % 60
                    _uptime.value = String.format("%02d:%02d:%02d", h, m, s)
                }
                delay(1000)
            }
        }
    }

    private fun startAutoTest() {
        autoTestJob?.cancel()
        autoTestJob = viewModelScope.launch {
            delay(500)
            testConnection()
            while (isActive) {
                delay(60000)
                testConnection()
            }
        }
    }

    private fun startKeepAlive() {
        keepAliveJob?.cancel()
        if (!settings.getBoolean("keep_alive_enabled")) return
        keepAliveJob = viewModelScope.launch {
            while (isActive) {
                delay(settings.getInt("keep_alive_interval") * 1000L)
                try {
                    val tor = torManager ?: continue
                    if (!tor.isConnected()) continue
                    tor.getExitIpAndCountry()
                } catch (_: Exception) {}
            }
        }
    }

    private fun startWatchdog() {
        watchdogJob?.cancel()
        if (!settings.getBoolean("watchdog_enabled")) return
        watchdogJob = viewModelScope.launch {
            while (isActive) {
                delay(settings.getInt("watchdog_interval") * 1000L)
                val tor = torManager ?: continue
                if (!tor.isRunning() && _isConnected.value) {
                    addLog("[Watchdog] Tor died - restarting...")
                    stopConnect()
                    delay(2000)
                    startConnect()
                }
            }
        }
    }

    private fun getBridgeLines(): List<String> {
        val src = _selectedSource.value
        val limit = settings.getInt("bridges_in_torrc")
        val shuffle = settings.getBoolean("shuffle_bridges")

        if (_noBridge.value) return emptyList()

        if (settings.getBoolean("use_custom_bridges")) {
            return bridgeManager.getCustomBridges(settings.getString("custom_bridges"), limit, shuffle)
        }

        return when (src) {
            "Default (Built-in)" -> bridgeManager.getBuiltInBridges(_selectedTransport.value)
            "Custom Bridges" -> bridgeManager.getCustomBridges(settings.getString("custom_bridges"), limit, shuffle)
            else -> bridgeManager.getBridgeLines(_selectedCategory.value, _selectedTransport.value, _selectedIp.value, limit, shuffle)
        }
    }

    private fun createTorManager(): TorManager {
        return TorManager(context, "Main", Config.SOCKS_PORT, Config.CTRL_PORT, Config.HTTP_PROXY_PORT)
    }

    private fun resetStats() {
        _connectionProgress.value = 0
        _exitIp.value = "\u2014"
        _country.value = "\u2014"
        _uptime.value = "\u2014"
        _torStatus.value = "\u2014"
        startTime = 0
    }

    private fun addLog(msg: String) {
        _logs.value = _logs.value + msg
    }

    override fun onCleared() {
        super.onCleared()
        stopConnect()
        proxyManager.stopHttpProxy()
    }
}

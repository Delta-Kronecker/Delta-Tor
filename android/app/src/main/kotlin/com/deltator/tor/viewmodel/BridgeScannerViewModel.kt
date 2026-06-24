package com.deltator.tor.viewmodel

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.deltator.tor.core.BridgeManager
import com.deltator.tor.core.BridgeScanner
import com.deltator.tor.core.ScanResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.File

class BridgeScannerViewModel(application: Application) : AndroidViewModel(application) {
    private val bridgeManager = BridgeManager(application)
    private val scanner = BridgeScanner()

    private val _isScanning = MutableStateFlow(false)
    val isScanning: StateFlow<Boolean> = _isScanning.asStateFlow()

    private val _progress = MutableStateFlow(0)
    val progress: StateFlow<Int> = _progress.asStateFlow()

    private val _progressText = MutableStateFlow("Ready.")
    val progressText: StateFlow<String> = _progressText.asStateFlow()

    private val _results = MutableStateFlow<List<ScanResult>>(emptyList())
    val results: StateFlow<List<ScanResult>> = _results.asStateFlow()

    private val _summary = MutableStateFlow("")
    val summary: StateFlow<String> = _summary.asStateFlow()

    private val _workingBridges = mutableListOf<String>()

    fun startScan(category: String, transport: String, ip: String, workers: Int, timeout: Int) {
        if (_isScanning.value) return

        val file = bridgeManager.getBridgeFile(category, transport, ip)
        if (!file.exists()) {
            _progressText.value = "Bridge file not found. Update bridges first."
            return
        }

        val lines = file.readLines().filter { it.isNotBlank() }
        if (lines.isEmpty()) {
            _progressText.value = "Bridge file is empty."
            return
        }

        _results.value = emptyList()
        _workingBridges.clear()
        _isScanning.value = true
        _progress.value = 0
        _summary.value = ""

        viewModelScope.launch(Dispatchers.Default) {
            val scanResults = scanner.scan(
                bridgeLines = lines,
                transport = transport,
                workers = workers,
                timeoutSeconds = timeout,
                onProgress = { done, total ->
                    _progress.value = (done * 100 / total)
                    _progressText.value = "Scanning... $done/$total"
                },
                onResult = { result ->
                    _results.value = _results.value + result
                    if (result.reachable) {
                        val bridgeLine = lines.firstOrNull { it.contains(result.host) }
                        if (bridgeLine != null) _workingBridges.add(bridgeLine)
                    }
                }
            )

            val ok = scanResults.count { it.reachable }
            val total = scanResults.size
            _progressText.value = "Done."
            _summary.value = "$ok reachable  /  ${total - ok} unreachable  /  $total total"
            _isScanning.value = false
        }
    }

    fun stopScan() {
        _isScanning.value = false
    }

    fun exportWorkingBridges(filePath: String): Boolean {
        return try {
            File(filePath).writeText(_workingBridges.joinToString("\n"))
            true
        } catch (e: Exception) {
            false
        }
    }
}

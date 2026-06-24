package com.deltator.tor.viewmodel

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.deltator.tor.core.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class MultiConnectViewModel(application: Application) : AndroidViewModel(application) {
    private val context = application
    private val settings = SettingsManager(application)
    private val bridgeManager = BridgeManager(application)

    val manager = MultiConnectManager(context, settings, bridgeManager)

    val slots: StateFlow<List<SlotState>> = manager.slots
    val isRunning: StateFlow<Boolean> = manager.isRunning
    val activeProxyLabel: StateFlow<String?> = manager.activeProxyLabel

    private val _autoProxyEnabled = MutableStateFlow(false)
    val autoProxyEnabled: StateFlow<Boolean> = _autoProxyEnabled.asStateFlow()

    init {
        val saved = manager.loadSlotDefs()
        manager.initialize(saved)
    }

    fun startAll() = manager.startAll()
    fun stopAll() = manager.stopAll()
    fun toggleSlot(label: String) = manager.toggleSlot(label)
    fun deleteSlot(label: String) {
        manager.deleteSlot(label)
        manager.saveSlotDefs()
    }

    fun retrySlot(label: String) = manager.retrySlot(label)

    fun addSlot(def: SlotDef) {
        manager.addSlot(def)
        manager.saveSlotDefs()
    }

    fun setProxyToSlot(label: String) {
        val current = activeProxyLabel.value
        if (current == label && !_autoProxyEnabled.value) {
            manager.setActiveProxy(null)
            return
        }

        val idx = slots.value.indexOfFirst { it.label == label }
        if (idx < 0) return
        val ports = Config.slotPorts(idx)
        val socksPort = ports.first
        val httpPort = ports.third

        manager.setActiveProxy(label)

        viewModelScope.launch {
            val proxyManager = ProxyManager()
            proxyManager.startHttpProxy(httpPort, socksPort)
        }
    }

    fun toggleAutoProxy() {
        _autoProxyEnabled.value = !_autoProxyEnabled.value
        if (_autoProxyEnabled.value) {
            startAutoProxyCheck()
        }
    }

    private fun startAutoProxyCheck() {
        viewModelScope.launch {
            while (_autoProxyEnabled.value) {
                kotlinx.coroutines.delay(5000)

                val best = manager.slots.value
                    .filter { it.healthOk && it.state == TorState.CONNECTED }
                    .minByOrNull { it.avgLatency }

                if (best != null && best.label != activeProxyLabel.value) {
                    setProxyToSlot(best.label)
                }
            }
        }
    }

    fun getSlotLogs(label: String): List<String> = manager.getSlotLogs(label)

    override fun onCleared() {
        super.onCleared()
        manager.destroy()
    }
}

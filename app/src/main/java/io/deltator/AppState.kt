package io.deltator

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Shared connection state, observed by the Compose UI and updated by [TorVpnService].
 */
object AppState {

    data class VpnState(
        val connecting: Boolean = false,
        val torRunning: Boolean = false,
        val connected: Boolean = false,
        val transports: Map<String, Int> = emptyMap(),
        val transport: String = "",
        val error: String? = null,
        val txBytes: Long = 0,
        val rxBytes: Long = 0
    )

    private val _state = MutableStateFlow(VpnState())
    val state: StateFlow<VpnState> = _state.asStateFlow()

    @Volatile
    var vpnStarted = false
        private set

    override fun toString(): String = "AppState"

    fun markStarted() {
        vpnStarted = true
    }

    fun markStopped() {
        vpnStarted = false
        _state.value = VpnState()
    }

    fun update(block: (VpnState) -> VpnState) {
        _state.update(block)
    }
}
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
        val rxBytes: Long = 0,
        val txSpeed: Float = 0f,
        val rxSpeed: Float = 0f,
        val connectedAtMillis: Long = 0,
        val exitCode: String = "",
        val exitName: String = ""
    )

    data class BridgeState(
        val updating: Boolean = false,
        val lastUpdateMillis: Long = 0,
        val vanilla: Int = 0,
        val obfs4: Int = 0,
        val webtunnel: Int = 0,
        val error: String? = null
    )

    data class ReleaseState(
        val checking: Boolean = false,
        val latestVersion: String = "",
        val latestUrl: String = "",
        val newer: Boolean = false
    )

    private val _state = MutableStateFlow(VpnState())
    val state: StateFlow<VpnState> = _state.asStateFlow()

    private val _bridgeState = MutableStateFlow(BridgeState())
    val bridgeState: StateFlow<BridgeState> = _bridgeState.asStateFlow()

    private val _releaseState = MutableStateFlow(ReleaseState(checking = true))
    val releaseState: StateFlow<ReleaseState> = _releaseState.asStateFlow()

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

    fun updateBridge(block: (BridgeState) -> BridgeState) {
        _bridgeState.update(block)
    }

    fun updateRelease(block: (ReleaseState) -> ReleaseState) {
        _releaseState.update(block)
    }
}
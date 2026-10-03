package io.deltator

import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Shared connection state, observed by the Compose UI and updated by [TorVpnService].
 */
object AppState {

    /** What a modal notice is about, so the UI can word itself for the reason. */
    enum class NoticeKind {
        /** A specific transport stalled and the app restarted in auto by itself. */
        AutoRecovery,

        /**
         * An auto race stalled without snowflake in it, and snowflake was added.
         * Its own kind rather than a flavour of [AutoRecovery] because the two
         * report different things: the user either picked a mode that is blocked,
         * or left the choice to the app and the app's own default set was not
         * enough here.
         */
        SnowflakeRecovery,

        /** One-time explainer shown the first time the app is ever opened. */
        FirstRun
    }

    /**
     * A notice the user never asked for and cannot miss.
     *
     * Separate from [VpnState.error] because of *when* it has to be readable. An
     * error belongs to the connection that failed and is cleared by the next one;
     * these notices describe an action the app took on the user's behalf, and the
     * restart they caused is what wipes the error they would otherwise live in.
     * So this hangs off the state until the UI acknowledges it by id.
     *
     * The id is monotonic rather than a boolean: the same notice must be able to
     * happen twice, and a flag keyed on nothing would leave the second occurrence
     * indistinguishable from the first being still on screen.
     */
    data class Notice(
        val id: Long,
        val kind: NoticeKind,
        val title: String,
        val body: String
    )

    data class VpnState(
        val connecting: Boolean = false,
        val torRunning: Boolean = false,
        val connected: Boolean = false,
        /** Up but carrying nothing: the link or the circuits are being restored. */
        val reconnecting: Boolean = false,
        /**
         * A stop is in progress: the TUN is down and every Tor/lyrebird process
         * is being killed. Nothing may be started while this is set, because a
         * start would race the teardown for the ports.
         */
        val stopping: Boolean = false,
        val transports: Map<String, Int> = emptyMap(),
        val transport: String = "",
        val error: String? = null,
        /**
         * A modal notice awaiting acknowledgement, or null once it is dismissed.
         * Deliberately not cleared by a connect or a teardown: see [Notice].
         */
        val notice: Notice? = null,
        val txBytes: Long = 0,
        val rxBytes: Long = 0,
        val txSpeed: Float = 0f,
        val rxSpeed: Float = 0f,
        val connectedAtMillis: Long = 0,
        val exitCode: String = "",
        val exitName: String = "",
        val exitIp: String = ""
    )

    data class BridgeState(
        val updating: Boolean = false,
        val lastUpdateMillis: Long = 0,
        val vanilla: Int = 0,
        val obfs4: Int = 0,
        val webtunnel: Int = 0,
        val snowflake: Int = 0,
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

    /** Makes every notice distinguishable from the last one of the same kind. */
    private val noticeSeq = AtomicLong(0)

    override fun toString(): String = "AppState"

    /**
     * Raise a notice and return the id the UI has to acknowledge.
     *
     * Replacing an unacknowledged notice rather than queueing is deliberate: the
     * old one described a connect attempt that no longer exists, so keeping it on
     * screen would be reporting on a state the app has already left.
     */
    fun postNotice(kind: NoticeKind, title: String, body: String): Long {
        val id = noticeSeq.incrementAndGet()
        _state.update { it.copy(notice = Notice(id, kind, title, body)) }
        return id
    }

    /**
     * Acknowledge a notice. The id is checked so a dismissal that arrives after a
     * newer notice replaced this one cannot take that newer one down with it.
     */
    fun clearNotice(id: Long) {
        _state.update { if (it.notice?.id == id) it.copy(notice = null) else it }
    }

    fun markStarted() {
        vpnStarted = true
    }

    fun markStopped() {
        vpnStarted = false
        // Teardown resets the whole state, and three things have to survive it:
        // the stop flag, which is what keeps the UI locked until the cores are
        // confirmed gone; the error, which is the only explanation there is for a
        // connection that failed on its own; and the notice, because a recovery
        // notice is posted *before* its retry runs and that retry is allowed to
        // fail -- dropping it here would leave the user with a connection that
        // silently changed transports and then died, and no explanation for
        // either half of that.
        _state.update {
            VpnState(stopping = it.stopping, error = it.error, notice = it.notice)
        }
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
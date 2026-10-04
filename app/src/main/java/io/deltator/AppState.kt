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
        /**
         * A connect in a mode other than auto stalled, and the app stopped it,
         * switched to auto and started again by itself.
         *
         * The only automatic change there is, and it is deliberately not offered
         * to auto itself: auto is already the plan the app would choose, so there
         * is nothing to switch to, and a plan that rewrites itself while it is
         * already running is not a plan any more.
         */
        AutoRecovery,

        /** One-time explainer shown the first time the app is ever opened. */
        FirstRun
    }

    /**
     * One block of a notice body, with the direction it has to be laid out in.
     *
     * A notice can carry more than one script. The first-run explainer is
     * English, Persian and Russian in the same dialog, and one Text cannot lay
     * that out correctly: paragraph direction is resolved from the paragraph's
     * own content, so a Persian paragraph that happens to start with a Latin
     * word or a bracketed term is laid out left-to-right and its sentences end
     * up reordered, with the punctuation on the wrong side. Splitting the body
     * into blocks lets each be laid out in its own direction, which is the only
     * way to get the Persian one to read right-to-left.
     */
    data class NoticeBlock(val text: String, val rtl: Boolean = false)

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
        val blocks: List<NoticeBlock>
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
        val exitIp: String = "",
        /**
         * Set while proxy mode is on and Tor is up, as the address to configure a
         * client with: "socks5://127.0.0.1:9050".
         *
         * A separate field rather than something the UI derives from transport
         * state, because the whole point of proxy mode is that the user has to be
         * told the address somewhere. There is no tunnel to imply it, and the
         * notification is the one surface guaranteed to be on screen.
         */
        val socksEndpoint: String = ""
    )

    data class BridgeState(
        val updating: Boolean = false,
        val lastUpdateMillis: Long = 0,
        val vanilla: Int = 0,
        val obfs4: Int = 0,
        val webtunnel: Int = 0,
        val snowflake: Int = 0,
        val fresh: Int = 0,
        val combined: Int = 0,
        /**
         * Bridges remembered per transport by [io.deltator.tunnel.BridgeMemory]:
         * what a reconnect would start from instead of the full list. Keyed by the
         * same transport names as the counts above.
         */
        val memory: Map<String, Int> = emptyMap(),
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

    /**
     * The proxy address on its own.
     *
     * A screen that needs only this should not collect [state]: the bootstrap
     * writes a fresh [VpnState] on every runner progress snapshot, so a collector
     * of the whole thing recomposes dozens of times a second for the whole
     * connect. In a LazyColumn that is not free -- the text fields in the ADVANCED
     * drawer re-measure on every one of those passes and the content below them
     * visibly shifts while the log is streaming. This flow only emits when the
     * address itself changes.
     */
    private val _socksEndpoint = MutableStateFlow("")
    val socksEndpoint: StateFlow<String> = _socksEndpoint.asStateFlow()

    /**
     * The connection mode the app is really using, which is not always the one
     * the drawer last showed.
     *
     * Auto recovery switches the mode to auto by itself, from the service, while
     * the drawer is sitting there holding its own copy of the choice. The form is
     * remembered, so without this the dropdown went on showing the mode that was
     * replaced while the app connected with the new one -- a settings screen that
     * is wrong about the only thing it is for.
     *
     * Written by whoever changes the mode, which is two places: the form, which
     * writes the preference and this together, and the service, which changes the
     * preference after a recovery.
     */
    private val _mode = MutableStateFlow("")
    val mode: StateFlow<String> = _mode.asStateFlow()

    fun setMode(mode: String) {
        if (_mode.value != mode) _mode.value = mode
    }

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
    fun postNotice(kind: NoticeKind, title: String, body: String): Long =
        postNoticeBlocks(kind, title, listOf(NoticeBlock(body)))

    /** As [postNotice], for a body made of blocks laid out in different directions. */
    fun postNoticeBlocks(kind: NoticeKind, title: String, blocks: List<NoticeBlock>): Long {
        val id = noticeSeq.incrementAndGet()
        _state.update { it.copy(notice = Notice(id, kind, title, blocks)) }
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
        update {
            VpnState(stopping = it.stopping, error = it.error, notice = it.notice)
        }
    }

    fun update(block: (VpnState) -> VpnState) {
        _state.update(block)
        // Kept in step here rather than by whoever writes the state, so the
        // narrow flow above cannot drift away from the field it mirrors.
        val address = _state.value.socksEndpoint
        if (address != _socksEndpoint.value) _socksEndpoint.value = address
    }

    fun updateBridge(block: (BridgeState) -> BridgeState) {
        _bridgeState.update(block)
    }

    fun updateRelease(block: (ReleaseState) -> ReleaseState) {
        _releaseState.update(block)
    }
}
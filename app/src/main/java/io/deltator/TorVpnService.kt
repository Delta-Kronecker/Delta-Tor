package io.deltator

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import io.deltator.tunnel.BridgeCountries
import io.deltator.tunnel.BridgeMemory
import io.deltator.tunnel.ExitLocator
import io.deltator.tunnel.ExitNodes
import io.deltator.tunnel.HevSocks5Tunnel
import io.deltator.tunnel.ParallelTorManager
import io.deltator.tunnel.TorRunner
import io.deltator.tunnel.TorSocksBridge
import io.deltator.tunnel.TorrcSettings
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Unwinds a transport race that the stall watchdog has given up on.
 *
 * Thrown from the race's progress callback, which is the only place in the poll
 * loop that can see the stall, and caught by the attempt that started the race.
 * It is not an error: nothing failed, the app simply decided the plan was wrong
 * and is about to run a different one. Carrying the recovery on the exception is
 * what keeps that decision on the same stack as the attempt it belongs to.
 */
private class RestartInAuto(val recovery: AppState.NoticeKind) :
    RuntimeException("connect restarted via $recovery")

class TorVpnService : VpnService() {

    companion object {
        const val ACTION_CONNECT = "io.deltator.CONNECT"
        const val ACTION_DISCONNECT = "io.deltator.DISCONNECT"
        const val ACTION_START_VPN = "io.deltator.START_VPN"
        const val ACTION_STOP_VPN = "io.deltator.STOP_VPN"
        const val CHANNEL_VPN_STATUS = "vpn_status"
        const val CHANNEL_UPDATES = "deltator_updates"
        const val NOTIFICATION_ID = 1

        private const val TAG = "TorVpnService"
        private const val VPN_MTU = 1280
        private const val VPN_ADDRESS = "10.255.255.1"
        private const val VPN_ROUTE = "0.0.0.0"
        private const val DEFAULT_DNS = "8.8.8.8"

        /** How often the connected tunnel is asked whether Tor can still carry traffic. */
        private const val PROBE_INTERVAL_MS = 5_000L

        /**
         * The same check while nothing is wrong, to keep the request itself rare.
         *
         * The first two steps of a healthy tunnel. After that the interval opens
         * up: a probe is a real request through Tor, not a local question, and a
         * connection nobody is using was paying for one every twenty seconds to
         * be told what it already knew.
         */
        private const val PROBE_INTERVAL_HEALTHY_MS = 20_000L
        private const val PROBE_INTERVAL_QUIET_MS = 45_000L
        private const val PROBE_INTERVAL_IDLE_MS = 90_000L

        /**
         * Consecutive failed probes before the transport is rebuilt. Two is one
         * full interval of grace: long enough that a single dropped request does
         * not throw away a working circuit, short enough that a real outage is
         * noticed while the user is still looking at the screen.
         */
        private const val PROBES_BEFORE_RECOVERY = 2

        /**
         * Failures after which the wait is abandoned and the rebuild happens
         * anyway. A transport that has answered nothing at all for this many
         * probes in a row is not merely busy building a circuit, so waiting out
         * the grace window would only delay an already obvious answer.
         */
        private const val PROBES_TO_IGNORE_GRACE = 6

        /**
         * Slack on top of the Tor-derived grace in [probeGraceMs]. A circuit being
         * built is silent, and from the outside that is indistinguishable from a
         * tunnel that is gone, so the grace has to outlast one build plus the wait
         * a request makes for it. This is only the margin on top of that sum, not
         * the whole grace -- it used to be the whole thing, which quietly assumed
         * a 30s SocksTimeout because that is what the template shipped with.
         */
        private const val PROBE_GRACE_MARGIN_MS = 5_000L

        /**
         * A restore is a clean Tor that still has to build its first circuit, so
         * for a while after it the tunnel is honestly allowed to be quiet. The
         * app used to start rebuilding again the moment two probes ran into that
         * silence, which is a loop: each rebuild costs a full bootstrap and the
         * next rebuild is due before the tunnel ever carried anything.
         */
        private const val PROBE_RECOVERY_COOLDOWN_MS = 90_000L

        /** Upper bound on one liveness probe; a dead Tor would otherwise hang it. */
        private const val PROBE_TIMEOUT_MS = 6_000

        /**
         * How often the traffic counters are read and republished while bytes are
         * actually moving.
         */
        private const val STATS_INTERVAL_BUSY_MS = 1_000L

        /**
         * The same, for a tunnel nobody is using. A connected VPN spends most of
         * its life idle, and idle is exactly when there is nothing new to draw:
         * the counters stand still and republishing them once a second only woke
         * the device up to redraw numbers that had not changed.
         */
        private const val STATS_INTERVAL_IDLE_MS = 5_000L

        /**
         * Backstop for the CPU lock, not the way it normally ends. The lock is
         * released as soon as the tunnel is up, and this only exists so a process
         * that dies mid-bootstrap cannot hold the CPU awake indefinitely.
         */
        private const val WAKE_LOCK_TIMEOUT_MS = 10 * 60 * 1000L

        /**
         * How long the UI stays in STOPPING after the cores are gone. Long enough
         * that the phase is actually seen instead of flashing past, and it also
         * covers the teardown itself when that is the slower of the two.
         */
        private const val STOPPING_MIN_MS = 3_000L

        /**
         * How long the race may sit on the same bootstrap percentage before the
         * app stops waiting and changes the plan underneath the user.
         *
         * A minute is chosen from the other end: this is only reachable when no
         * runner has finished a single percent step, and Tor's own bootstrap for a
         * working bridge clears 5% long before that. A genuinely slow but alive
         * path is climbing the whole time, so a flat line for a full minute means
         * nothing is coming, not that something is being slow.
         */
        private const val STALL_RECOVERY_AFTER_MS = 60_000L

        /** Title shared by every automatic-recovery notice. */
        const val RECOVERY_TITLE = "DELTATOR CHANGED THE CONNECTION MODE"
    }

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var vpnInterface: ParcelFileDescriptor? = null

    /**
     * Why the last [establishVpnInterface] attempt returned nothing, when it can
     * put it better than "it failed". Null once an attempt succeeds.
     */
    private var establishFailureReason: String? = null
    private var activeRunner: TorRunner? = null
    private var statsJob: Job? = null
    private var linkWatchJob: Job? = null
    private var exitLocatorJob: Job? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var wakeLock: PowerManager.WakeLock? = null

    // Stall watchdog state, reset once per connect attempt.
    //
    // The high-water mark rather than the latest reading is what makes this work:
    // bootstrap percentages are not monotonic per runner -- a runner that is
    // fetching microdescriptors can report 40, then 25 again -- so watching the
    // latest number would reset the timer on noise and a dead race could sit
    // there forever. Only a new maximum counts as progress.
    private var stallBest = -1
    private var stallSince = 0L
    private var stallFired = false

    /**
     * Recoveries already spent in this connect, by kind.
     *
     * The loop that retries on a recovery is only provably finite because each
     * kind fires once: after a switch to auto the mode rule can no longer match,
     * and after snowflake is added the snowflake rule can no longer match. If
     * either write silently failed, this set is what stops the two from taking
     * turns restarting the app forever.
     */
    private val recoveriesSpent = mutableSetOf<AppState.NoticeKind>()

    /** Outstanding [holdCpu] calls, so nested windows release only when both end. */
    private var cpuHolds = 0

    /**
     * Keep the CPU awake, or leave it awake if it already is.
     *
     * A PARTIAL_WAKE_LOCK stops the device from suspending at all, which is what
     * bootstrap and a transport rebuild need and what a healthy tunnel does not.
     * It used to be taken once at connect and released only at teardown, so a VPN
     * that connected and then sat idle held the CPU out of suspend for ten
     * minutes, on the device's least power-friendly setting, for no work at all.
     * Now the two windows that genuinely have work in flight are the only ones
     * that hold it.
     *
     * Counted rather than flagged, because a rebuild that gives up hands over to a
     * full reconnect: those two windows overlap for a moment and the CPU must not
     * be released by whichever of them finishes first.
     */
    @Synchronized
    private fun holdCpu() {
        if (cpuHolds == 0) {
            if (wakeLock == null) {
                wakeLock = (getSystemService(POWER_SERVICE) as PowerManager)
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DeltaTor:vpn")
                    .apply { setReferenceCounted(false) }
            }
            try {
                wakeLock?.acquire(WAKE_LOCK_TIMEOUT_MS)
            } catch (e: Exception) {
                Log.w(TAG, "Could not take the CPU lock: ${e.message}")
            }
        }
        cpuHolds++
    }

    /** Give the CPU back once every window that asked for it has finished. */
    @Synchronized
    private fun releaseCpu() {
        if (cpuHolds == 0) return
        cpuHolds--
        if (cpuHolds == 0) dropCpu()
    }

    /** Release the lock whatever asked for it. The shutdown path. */
    @Synchronized
    private fun dropCpu() {
        cpuHolds = 0
        try {
            wakeLock?.takeIf { it.isHeld }?.release()
        } catch (_: Exception) {
        }
        wakeLock = null
    }

    /**
     * Liveness of the link, kept by a [ConnectivityManager.NetworkCallback] so a
     * dropped connection is known without waiting for a request to time out.
     */
    @Volatile private var linkUp = true
    private val recovering = AtomicBoolean(false)
    private var failedProbes = 0

    /** What one liveness probe found, as far as it could tell. */
    private enum class ProbeResult { Ok, Live, Dead }

    /** When the tunnel last answered nothing at all, 0 while it is talking. */
    private var deadSince = 0L

    /** When the transport was last rebuilt, so a flap cannot rebuild in a loop. */
    private var lastRecoveryAt = 0L

    /**
     * Consecutive healthy probes with nothing else happening, which is what lets a
     * tunnel nobody is using be checked less and less often.
     */
    @Volatile private var quietProbes = 0

    /** Traffic carried since the last probe, which tells quiet apart from idle. */
    @Volatile private var bytesAtLastProbe = 0L

    /** Id of the connect session whose lines this service emits. */
    @Volatile private var currentSession: Int = 0

    /**
     * What the connected notification last rendered, so an idle tunnel can leave
     * the notification alone instead of reposting the same two numbers.
     */
    private var lastRenderedText = ""

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> connect()
            ACTION_DISCONNECT -> disconnect()
            ACTION_START_VPN -> startVpn()
            ACTION_STOP_VPN -> stopVpn()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        // Teardown blocks until every Tor/lyrebird process has exited and its
        // ports are released, so it must never run on the main thread.
        Thread({ teardown() }, "deltator-teardown").apply { isDaemon = true }.start()
        super.onDestroy()
    }

    private fun connect() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring connect: a stop is still tearing the cores down")
            return
        }
        if (AppState.vpnStarted) {
            Log.i(TAG, "Already running")
            return
        }
        AppState.markStarted()
        currentSession = Log.beginSession("connect")
        recoveriesSpent.clear()
        AppState.update { it.copy(connecting = true, connected = false, reconnecting = false, torRunning = false, error = null, transports = emptyMap(), transport = "", socksEndpoint = "") }

        startForeground(NOTIFICATION_ID, buildNotification("Connecting\u2026", progress = true, progressValue = 0))

        holdCpu()

        serviceScope.launch {
            try {
                runConnectFlow()
            } catch (e: Exception) {
                Log.e(TAG, "Connect failed", e)
                fail("Connect failed: ${e.message}")
            } finally {
                releaseCpu()
            }
        }
    }

    /**
     * Connect, and retry in auto if the user's own choice turns out to be blocked.
     *
     * A recovery returns from [runConnectAttempt] instead of being applied here,
     * so the retry is a plain second turn of the loop rather than a connect
     * re-entered from inside itself: one place owns the attempt, one owns the
     * decision to make another, and nothing has to unwind a nested launch to get
     * there.
     */
    private suspend fun runConnectFlow() {
        while (true) {
            val recovery = runConnectAttempt() ?: return
            applyRecovery(recovery)
        }
    }

    /**
     * One connect attempt against whatever the configuration says right now.
     *
     * Returns the [AppState.NoticeKind] of a recovery that has to be applied
     * before trying again, or null when the attempt finished -- connected, or
     * failed for real. Null covers the failure case because [fail] has already
     * reported it and there is nothing left to retry.
     */
    private suspend fun runConnectAttempt(): AppState.NoticeKind? {
        val proxyPort = Config.proxyPort
        val proxyHost = "127.0.0.1"

        TorSocksBridge.debugLogging = Config.debugMode
        TorSocksBridge.domainRouter = io.deltator.tunnel.DomainRouter.DISABLED

        // Step 1: Resolve the bridge lists for the selected mode and start its
        // runner(s). In auto mode vanilla / obfs4 / webtunnel race together, plus
        // a fourth "memory" runner on the bridges that provably worked last time.
        // Monitor each transport's bootstrap progress; the first to reach 100%
        // wins and the losing transports are stopped by the manager.
        val mode = ParallelTorManager.MODES
            .firstOrNull { it == Config.transportMode }
            ?: ParallelTorManager.TRANSPORT_AUTO
        val autoNames = Config.autoTransports
            .filter { it in ParallelTorManager.BRIDGE_SOURCES }
            .ifEmpty { ParallelTorManager.BRIDGE_SOURCES.keys.toList() }
        val modeLabel = if (mode == ParallelTorManager.TRANSPORT_AUTO) {
            autoNames.joinToString(" / ") +
                if (BridgeMemory.countAll(applicationContext) > 0) " / memory" else ""
        } else {
            // A single transport also races its own memory twin whenever that pool
            // has proven bridges, so say so instead of surprising the user later.
            mode + if (BridgeMemory.count(applicationContext, mode) > 0) " + memory" else ""
        }
        Log.i(TAG, "Transport mode: $modeLabel")
        updateNotification("Connecting via $modeLabel \u2026", progress = true, progressValue = 0)

        // What to try if this race turns out to be going nowhere. Snowflake sits
        // out of auto by default, so an auto race that stalls has a specific and
        // actionable cause: the transports that are racing are probably all
        // blocked here, and snowflake is the one that usually is not.
        val snowflakeInAuto = Config.autoTransports.contains(
            ParallelTorManager.TRANSPORT_SNOWFLAKE
        )

        // Watchdog armed for this attempt only; a later connect starts over.
        stallBest = -1
        stallSince = SystemClock.elapsedRealtime()
        stallFired = false

        val w = try {
            ParallelTorManager.race(
                context = applicationContext,
                basePort = proxyPort,
                sessionId = currentSession,
                transportMode = Config.transportMode,
                customBridges = Config.customBridges,
                autoTransports = Config.autoTransports
            ) { snapshot ->
                val progress = snapshot.mapValues { (name, r) -> if (r.failed != null) -1 else r.progress() }
                AppState.update { it.copy(transports = progress) }
                val maxProg = (progress.values.maxOrNull() ?: 0).coerceAtLeast(0)
                val detail = progress.entries.joinToString("  ") { (n, p) ->
                    "$n=${if (p < 0) "FAIL" else "$p%"}"
                }
                updateNotification(detail, progress = true, progressValue = maxProg)

                // Failed runners report -1 and must not count as a high-water mark,
                // or a race where everything died instantly would look like
                // progress up to 0 and never trip.
                val live = progress.values.filter { it >= 0 }.maxOrNull() ?: 0
                if (live > stallBest) {
                    stallBest = live
                    stallSince = SystemClock.elapsedRealtime()
                }
                if (!stallFired && live < 100 &&
                    SystemClock.elapsedRealtime() - stallSince >= STALL_RECOVERY_AFTER_MS
                ) {
                    stallFired = true
                    val recovery = when {
                        mode != ParallelTorManager.TRANSPORT_AUTO -> AppState.NoticeKind.AutoRecovery
                        !snowflakeInAuto -> AppState.NoticeKind.SnowflakeRecovery
                        else -> null
                    }
                    if (recovery != null && recoveriesSpent.add(recovery)) {
                        Log.w(
                            TAG,
                            "no progress past $live% for ${STALL_RECOVERY_AFTER_MS / 1000}s " +
                                "in $mode; recovering via $recovery"
                        )
                        // Tear the runners down here rather than waiting for the
                        // race to notice: stopAll bumps the generation, so the
                        // loop unwinds on its next checkGeneration instead of
                        // polling a set of processes that are already gone.
                        ParallelTorManager.stopAll()
                        throw RestartInAuto(recovery)
                    }
                }
            }
        } catch (e: RestartInAuto) {
            return e.recovery
        } catch (e: Exception) {
            fail(e.message ?: "All transports failed to bootstrap")
            return null
        }
        activeRunner = w
        // The race is over, so the watchdog has served its purpose.
        AppState.update {
            it.copy(transports = mapOf(w.name to 100), transport = w.name)
        }
        Log.i(TAG, "Winner transport: ${w.name} (SOCKS5 $proxyHost:${w.torSocksPort})")

        // Step 2: Start the SOCKS5 bridge between TUN and the winning Tor instance.
        // The bridge and Tor stay alive across VPN stop/start.
        // The losing runners were killed by race(); their processes are gone by
        // now, but wait for the kernel to release the ports anyway so the bind
        // below cannot lose a race against a dying socket.
        withContext(Dispatchers.IO) {
            ParallelTorManager.awaitPortFree(proxyHost, proxyPort)
        }
        val bridgeResult = TorSocksBridge.start(
            torSocksPort = w.torSocksPort,
            torHost = "127.0.0.1",
            listenPort = proxyPort,
            listenHost = proxyHost
        )
        if (bridgeResult.isFailure) {
            fail(bridgeResult.exceptionOrNull()?.message ?: "Failed to start bridge")
            return null
        }
        AppState.update { it.copy(torRunning = true) }

        // Step 3+4: TUN interface + tun2socks
        establishTunnel(w, proxyHost, proxyPort)
        return null
    }

    /**
     * Change the plan the next attempt will use, and tell the user it happened.
     *
     * The notice is posted before the retry starts, not after it succeeds: the
     * retry may itself stall, and a report that only appears on success would
     * leave the user with a connect that silently changed transports and no
     * explanation for it. The dialog survives the retry because [AppState.Notice]
     * is not cleared by a connect.
     */
    private fun applyRecovery(recovery: AppState.NoticeKind) {
        val wasMode = Config.transportMode
        when (recovery) {
            AppState.NoticeKind.AutoRecovery -> {
                Config.transportMode = ParallelTorManager.TRANSPORT_AUTO
                Log.i(TAG, "Recovery: $wasMode stalled, switching to auto")
            }
            AppState.NoticeKind.SnowflakeRecovery -> {
                Config.autoTransports =
                    Config.autoTransports + ParallelTorManager.TRANSPORT_SNOWFLAKE
                Log.i(TAG, "Recovery: auto stalled without snowflake, adding it to auto")
            }
            // Not a recovery the service can perform; posted by the UI instead.
            AppState.NoticeKind.FirstRun -> return
        }

        // The old attempt's runners are already gone and the per-transport map
        // describes processes that no longer exist, so clear it rather than leave
        // the retry drawing progress bars for the dead race.
        AppState.update {
            it.copy(transports = emptyMap(), transport = "", error = null, connecting = true)
        }
        // A new session id so the retry's transport log does not interleave with
        // the attempt the user was watching fail.
        currentSession = Log.beginSession("connect")

        AppState.postNotice(
            recovery,
            RECOVERY_TITLE,
            when (recovery) {
                AppState.NoticeKind.AutoRecovery ->
                    "$wasMode made no progress for a minute, which on this network " +
                        "usually means it is blocked.\n\n" +
                        "DeltaTor stopped that attempt, switched the connection mode to " +
                        "Auto and started again. Auto races every transport that works " +
                        "here, so it is more likely to find one.\n\n" +
                        "Auto is now your connection mode. If you would rather pick " +
                        "the transport yourself again, change it in Settings."
                AppState.NoticeKind.FirstRun -> ""
                AppState.NoticeKind.SnowflakeRecovery ->
                    "The transports Auto was racing made no progress for a minute, " +
                        "which usually means they are all blocked on this network.\n\n" +
                        "DeltaTor stopped that attempt, added Snowflake to Auto and " +
                        "started again. Snowflake reaches Tor through a volunteer proxy " +
                        "in a browser, so it is often the one that still works where " +
                        "direct bridges do not.\n\n" +
                        "Auto now includes Snowflake for every future connect. You can " +
                        "remove it again in Settings."
            }
        )
        updateNotification("Restarting in auto \u2026", progress = true, progressValue = 0)
    }

    /** Establish the TUN interface and tun2socks on top of the running Tor engine. */
    private suspend fun establishTunnel(w: TorRunner, proxyHost: String, proxyPort: Int) {
        // Proxy mode never brings up a tunnel, so it must not announce one and must
        // not report the missing descriptor as a failure. Tor is bootstrapped and
        // listening at this point either way; all that differs is whether the
        // device is pointed at it.
        if (Config.proxyOnlyMode) {
            val endpoint = "socks5://$proxyHost:$proxyPort"
            AppState.update {
                it.copy(
                    connecting = false,
                    connected = false,
                    reconnecting = false,
                    error = null,
                    transports = mapOf(w.name to 100),
                    connectedAtMillis = System.currentTimeMillis(),
                    socksEndpoint = endpoint
                )
            }
            startForeground(
                NOTIFICATION_ID,
                buildNotification("Proxy ready \u00b7 $endpoint", progress = false)
            )
            lastRenderedText = ""
            quietProbes = 0
            bytesAtLastProbe = 0L
            startExitLocator(proxyHost, proxyPort)
            Log.i(TAG, "Proxy mode ready. Winner: ${w.name}, SOCKS5 at $endpoint")
            Log.endSession("proxy ready via ${w.name}")
            // No stats poller and no link watch: both read bytes off the tunnel,
            // which does not exist here, so they would spin on a null handle and
            // paint nothing. The exit lookup above still works, because it talks
            // to Tor over SOCKS rather than through the tunnel.
            return
        }

        updateNotification("Establishing VPN \u2026", progress = true, progressValue = 0)

        vpnInterface = establishVpnInterface()
        if (vpnInterface == null) {
            // The builder knows more about why it gave up than a generic failure
            // string does -- "no apps picked" and "the interface could not be
            // created" are different problems for the person reading this.
            fail(establishFailureReason ?: "Failed to establish VPN interface")
            return
        }

        delay(200)

        val tunResult = HevSocks5Tunnel.start(
            tunFd = vpnInterface!!,
            socksAddress = proxyHost,
            socksPort = proxyPort,
            enableUdpTunneling = true,
            mtu = VPN_MTU,
            ipv4Address = VPN_ADDRESS
        )
        if (tunResult.isFailure) {
            fail(tunResult.exceptionOrNull()?.message ?: "Failed to start tunnel")
            return
        }

        AppState.update {
            it.copy(
                connecting = false,
                connected = true,
                reconnecting = false,
                error = null,
                transports = mapOf(w.name to 100),
                connectedAtMillis = System.currentTimeMillis(),
                exitCode = "",
                exitName = "",
                exitIp = "",
                socksEndpoint = ""
            )
        }
        startForeground(NOTIFICATION_ID, buildNotification("Connected via ${w.name} \u00b7 Tor Network", progress = false))
        lastRenderedText = ""
        quietProbes = 0
        bytesAtLastProbe = 0L
        startStatsPolling()
        startLinkWatch()
        startExitLocator(proxyHost, proxyPort)
        Log.i(TAG, "DeltaTor connected. Winner: ${w.name}, SOCKS5 at $proxyHost:$proxyPort")
        Log.endSession("connected via ${w.name}")
    }

    /** Re-establish the VPN on top of an already-running Tor engine. */
    private fun startVpn() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring start: a stop is still tearing the cores down")
            return
        }
        if (AppState.state.value.connected) {
            Log.i(TAG, "VPN already active")
            return
        }
        // In proxy mode the tunnel is never what "stopped", so restarting it
        // cannot be what brings it back either. Say so instead of silently
        // returning, because the button is still on screen and pressing it and
        // watching nothing happen is the worst possible answer.
        if (Config.proxyOnlyMode) {
            Log.i(TAG, "Ignoring start: proxy mode is on, there is no VPN to start")
            return
        }
        val w = activeRunner
        if (w == null || !w.isReady() || !TorSocksBridge.isRunning()) {
            Log.e(TAG, "Tor not running; full reconnect required")
            fail("Tor is not running. Reconnect.")
            return
        }
        serviceScope.launch {
            try {
                AppState.update { it.copy(connecting = true, connected = false, error = null) }
                establishTunnel(w, "127.0.0.1", Config.proxyPort)
            } catch (e: Exception) {
                Log.e(TAG, "Start VPN failed", e)
                fail("Start VPN failed: ${e.message}")
            }
        }
    }

    /**
     * Turn the VPN off without touching the Tor engine.
     *
     * The service, the SOCKS5 bridge and every Tor core stay exactly as they are;
     * only the TUN interface and the tun2socks data path go away, so the device
     * stops routing through Tor while the engine stays bootstrapped and ready.
     * [startVpn] puts the VPN back on top of the same core without a re-bootstrap,
     * which is the whole point of keeping this separate from [disconnect].
     *
     * Because no process is killed here, there is nothing to wait for: the state
     * flips to torRunning straight away instead of holding the STOPPING word for
     * the three seconds a real core teardown needs.
     */
    private fun stopVpn() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring stop: a stop is already tearing the cores down")
            return
        }
        // Stopping the VPN tears the tunnel down. In proxy mode there is no tunnel,
        // and the listener the user was told to point their browser at is the
        // thing being kept, so honouring this would shut the proxy off and leave
        // them with a dead address and no error. Disconnect is what stops proxy
        // mode, and it goes through the normal path below.
        if (Config.proxyOnlyMode) {
            Log.i(TAG, "Ignoring stop: proxy mode is on, use Disconnect to shut the proxy down")
            return
        }
        val s = AppState.state.value
        if (!s.connected && !s.connecting && !s.reconnecting) {
            Log.i(TAG, "Nothing to stop: the VPN is not up")
            return
        }
        Log.i(TAG, "Stopping the VPN only; Tor stays running")
        AppState.update {
            it.copy(
                stopping = true,
                connecting = false,
                connected = false,
                reconnecting = false,
                error = null,
                socksEndpoint = ""
            )
        }
        updateNotification("Turning the VPN off \u2026", progress = true, progressValue = 0)
        serviceScope.launch {
            teardownVpn()
            // The engine was never touched, so torRunning stays true on purpose.
            AppState.update { it.copy(stopping = false, torRunning = activeRunner != null) }
            Log.endSession("VPN off \u00b7 Tor still running")
            Log.i(TAG, "VPN off: tor is still up, ready for a restart without re-bootstrapping")
            if (activeRunner != null) {
                // No stats poller here: the tunnel that fed it is gone, so it
                // would only spin on a null stats handle and repaint nothing.
                lastRenderedText = ""
                updateNotification(
                    "Tor running \u00b7 VPN off",
                    progress = false,
                    progressValue = 0
                )
            } else {
                // No runner survived; drop to the fully stopped state.
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    private fun establishVpnInterface(): ParcelFileDescriptor? {
        // Cleared per attempt so a stale reason from an earlier connect can never
        // be reported for a later one.
        establishFailureReason = null
        // Guard, not the feature itself. establishTunnel returns before ever
        // calling this when proxy mode is on, and it is what makes the proxy
        // branch a single early return rather than a condition threaded through
        // the builder below. If it ever were reached, the null it returns is a
        // clean failure: the caller reports "Failed to establish VPN interface"
        // and stops, rather than handing a null descriptor to tun2socks.
        if (Config.proxyOnlyMode) {
            // The listener is the loopback address on the configured port, the
            // same values connect() passes to establishTunnel. They are local
            // variables there, not properties, so this repeats them rather than
            // naming something that does not exist at this scope.
            Log.i(TAG, "Proxy mode: not establishing a TUN, SOCKS5 stays on 127.0.0.1:${Config.proxyPort}")
            return null
        }
        return try {
            val builder = Builder()
                .setSession("DeltaTor")
                .setMtu(VPN_MTU)
                .addAddress(VPN_ADDRESS, 32)
                .addDnsServer(DEFAULT_DNS)
            // Exclude our own app so Tor/Snowflake sockets go direct (not through TUN)
            // In both modes our own traffic must stay outside the VPN interface,
            // because Tor listens on 127.0.0.1 and the interface is what would
            // capture loopback traffic otherwise. In allowlist mode (VPN-only) we
            // do not put ourselves on the allowlist, so we are bypassed anyway;
            // keeping this explicit avoids relying on that detail across Android
            // versions.
            try {
                builder.addDisallowedApplication(packageName)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to exclude self from VPN", e)
            }
            // Split tunnelling, off: the device behaves exactly as it always has and
            // every app goes through Tor. No allow or disallow entry is added
            // beyond this app's own exclusion.
            //
            // On, the global mode decides what the user's picks mean. BYPASS
            // excludes them, so they are the exceptions and the rest is
            // tunnelled. VPN does the opposite: it allows them and nothing else,
            // so they are the only apps tunnelled and everything else reaches
            // the internet directly. That is the platform's own allow-list
            // semantics rather than an inversion done by hand -- enumerating
            // every installed package to subtract the picks would need the same
            // QUERY_ALL_PACKAGES visibility anyway and would break on any app
            // installed while the list was built.
            //
            // The default route is added in every mode, VPN-only included. The
            // allow-list is what keeps the other apps out, not the absence of a
            // route: Builder applies the per-app filter when it captures, and the
            // allowed apps still need a route to reach the tunnel at all. Leaving
            // the route out of the VPN-only branch (commit 86c2604) gave that
            // branch nothing to route, while its no-picks fallback put the blanket
            // route back and tunnelled the whole device -- which is why the mode
            // looked like it did nothing no matter what was picked.
            //
            // A package uninstalled since the list was written makes the builder
            // throw, and one bad entry must not cost the user the whole tunnel, so
            // each is applied on its own and the rest still go through. If that
            // leaves nothing routable in VPN-only mode the connect is refused with
            // a reason instead of quietly capturing every app.
            val picks = Config.splitTunnelSelected.filter { it != packageName }
            val vpnOnly = Config.splitTunnelMode == Config.SPLIT_MODE_VPN
            // Unconditional, so the log can tell "switch off" apart from "on but
            // read wrong". With the switch off there was nothing printed at all,
            // and a silent log is indistinguishable from a mode that does nothing.
            Log.i(
                TAG,
                "Split tunnel: enabled=${Config.splitTunnelEnabled} mode=${Config.splitTunnelMode} " +
                    "picks=${Config.splitTunnelSelected.size} routable=${picks.size}"
            )
            if (Config.splitTunnelEnabled) {
                var applied = 0
                var failed = 0
                for (pkg in picks) {
                    try {
                        if (vpnOnly) {
                            builder.addAllowedApplication(pkg)
                        } else {
                            builder.addDisallowedApplication(pkg)
                        }
                        applied++
                    } catch (e: Exception) {
                        failed++
                        Log.w(TAG, "Split tunnel: cannot route $pkg: ${e.message}", e)
                    }
                }
                builder.addRoute(VPN_ROUTE, 0)
                Log.i(
                    TAG,
                    if (vpnOnly) {
                        "Split tunnel: VPN-only, $applied of ${picks.size} pick(s) through Tor " +
                            "[${picks.take(5).joinToString()}], everything else direct, " +
                            "$failed skipped"
                    } else {
                        "Split tunnel: $applied of ${picks.size} pick(s) bypass Tor " +
                            "[${picks.take(5).joinToString()}], all others tunnelled, " +
                            "$failed skipped"
                    }
                )
                if (vpnOnly && applied == 0) {
                    val reason = if (picks.isEmpty()) {
                        "VPN-only split tunnelling has no apps picked, so nothing would go " +
                            "through Tor. Pick at least one app, or let picked apps bypass Tor."
                    } else {
                        "None of the ${picks.size} app(s) picked for VPN-only split tunnelling " +
                            "could be routed, so nothing would go through Tor. Re-pick them."
                    }
                    establishFailureReason = reason
                    Log.e(TAG, reason)
                    return null
                }
            } else {
                builder.addRoute(VPN_ROUTE, 0)
                Log.i(TAG, "Split tunnel: off, every app goes through Tor")
            }
            builder.setBlocking(false)
            builder.establish()
        } catch (e: Exception) {
            Log.e(TAG, "establish() failed", e)
            null
        }
    }

    /**
     * Publish the traffic counters, and only when there is something new to say.
     *
     * The counters are read every second while bytes are moving, because that is
     * when they are the point. Once the tunnel goes quiet they are read every few
     * seconds and the notification is only redrawn when its text actually differs
     * from what is already on screen.
     *
     * That check is the whole point. The notification used to be rebuilt and
     * reposted unconditionally, once a second, for as long as the VPN was on: a
     * fresh builder, three fresh PendingIntents and a Binder call to SystemUI,
     * sixty times a minute, to redraw two numbers that had not moved. Reposting a
     * notification that says the same thing is not free even when nothing
     * changes, and a VPN that nobody is using is the state it spends most of its
     * life in.
     */
    private fun startStatsPolling() {
        statsJob?.cancel()
        lastRenderedText = ""
        statsJob = serviceScope.launch {
            var lastTx = 0L
            var lastRx = 0L
            var lastTime = 0L
            while (isActive) {
                val stats = HevSocks5Tunnel.getStats()
                if (stats == null) {
                    delay(STATS_INTERVAL_IDLE_MS)
                    continue
                }

                val now = System.currentTimeMillis()
                val dtSeconds = ((now - lastTime).coerceAtLeast(1000) / 1000f).coerceAtLeast(0.001f)
                val upSpeed = if (lastTime > 0) (stats.txBytes - lastTx).toFloat() / dtSeconds else 0f
                val downSpeed = if (lastTime > 0) (stats.rxBytes - lastRx).toFloat() / dtSeconds else 0f
                val moving = stats.txBytes != lastTx || stats.rxBytes != lastRx
                lastTx = stats.txBytes
                lastRx = stats.rxBytes
                lastTime = now

                AppState.update {
                    it.copy(
                        txBytes = stats.txBytes,
                        rxBytes = stats.rxBytes,
                        txSpeed = upSpeed,
                        rxSpeed = downSpeed
                    )
                }

                // Reconnecting is what the user is watching, so it keeps the fast
                // cadence; a still tunnel has nothing to hurry.
                val busy = moving || AppState.state.value.reconnecting
                val text = trafficText(upSpeed, downSpeed, stats.txBytes, stats.rxBytes)
                if (busy || text != lastRenderedText) {
                    notifyTraffic(text)
                    lastRenderedText = text
                }
                delay(if (busy) STATS_INTERVAL_BUSY_MS else STATS_INTERVAL_IDLE_MS)
            }
        }
    }

    /** The connected notification's title and body, as one string to compare. */
    private fun trafficText(
        upSpeed: Float,
        downSpeed: Float,
        txBytes: Long,
        rxBytes: Long
    ): String {
        val reconnecting = AppState.state.value.reconnecting
        return if (reconnecting) {
            "DeltaTor \u2014 Reconnecting|Restoring the tunnel\u2026"
        } else {
            "DeltaTor \u2014 Connected|\u2191 ${formatBytes(upSpeed)}/s  \u2193 ${formatBytes(downSpeed)}/s\n" +
                "Total: \u2191 ${formatBytes(txBytes)}  \u2193 ${formatBytes(rxBytes)}"
        }
    }

    private fun notifyTraffic(text: String) {
        val parts = text.split('|')
        val builder = NotificationCompat.Builder(this, CHANNEL_VPN_STATUS)
            .setSmallIcon(R.drawable.ic_tor)
            .setContentTitle(parts[0])
            .setContentText(parts[1])
            .setStyle(NotificationCompat.BigTextStyle())
            .setContentIntent(mainPendingIntent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
        postNotification(withStateActions(builder).build())
    }

    /**
     * Give the notification the two actions that make sense right now.
     *
     * "Disconnect" is always there and always means the same thing: kill the VPN
     * and every Tor core. The other action depends on which half of the engine is
     * alive. With the VPN up, the useful one is to take the device off Tor while
     * the core stays bootstrapped; with the VPN already off, it is the other way
     * round. Offering "Stop VPN" on an already stopped VPN would be a button that
     * can only ever answer "nothing to stop", and with the engine shut down
     * neither action applies, so the row is left off.
     */
    private fun withStateActions(
        builder: NotificationCompat.Builder
    ): NotificationCompat.Builder {
        val s = AppState.state.value
        // Real icons, not addAction(0, ...). Zero is a resource id that resolves to
        // nothing, and a row whose icon cannot be loaded is dropped whole by some
        // ROMs -- which is what made these buttons appear on some phones and not
        // others. See the ic_notification_* drawables.
        builder.addAction(R.drawable.ic_notification_disconnect, "Disconnect", disconnectPendingIntent)
        // In proxy mode there is no tunnel, so neither VPN action has anything to
        // act on: startVpn and stopVpn both refuse while Config.proxyOnlyMode is
        // set. Shipping them anyway put a live-looking button in the shade that
        // did nothing when tapped. Disconnect is the only action that still works.
        if (s.connected) {
            builder.addAction(R.drawable.ic_notification_stop, "Stop VPN", stopVpnPendingIntent)
        } else if (s.torRunning && !Config.proxyOnlyMode) {
            builder.addAction(R.drawable.ic_notification_start, "Start VPN", startVpnPendingIntent)
        }
        return builder
    }

    // --- Link loss and recovery ------------------------------------------------

    /**
     * Watch the link while the tunnel is up.
     *
     * A dropped connection used to be invisible: the Tor process keeps printing
     * 100%, the interface keeps claiming to be connected, and the wait for Tor to
     * retry its guard connections on its own schedule was as long as Tor decided
     * it should be. This loop asks the tunnel every few seconds whether Tor can
     * still complete a request, and repairs it when the answer stays no.
     */
    private fun startLinkWatch() {
        registerNetworkCallback()
        linkWatchJob?.cancel()
        linkWatchJob = serviceScope.launch {
            while (isActive) {
                val before = AppState.state.value
                // A healthy tunnel is only checked now and then, since the probe
                // is a real (if tiny) request through Tor; a link that is down or
                // already failing is watched closely.
                val urgent = before.reconnecting || !linkUp || failedProbes > 0
                delay(if (urgent) PROBE_INTERVAL_MS else healthyInterval())

                val state = AppState.state.value
                if (!state.connected || recovering.get()) continue

                if (!linkUp) {
                    // Nothing can pass until the link is back; only say so.
                    quietProbes = 0
                    if (!state.reconnecting) markReconnecting("network down")
                    continue
                }

                // Traffic is its own evidence that the tunnel is working, and it
                // also means a dead link would have been noticed by the user.
                val bytes = AppState.state.value.txBytes + AppState.state.value.rxBytes
                val carried = bytes != bytesAtLastProbe
                bytesAtLastProbe = bytes

                when (probeTunnelUsable()) {
                    ProbeResult.Ok -> {
                        deadSince = 0
                        failedProbes = 0
                        lastRecoveryAt = 0
                        quietProbes = if (carried) 0 else quietProbes + 1
                        if (state.reconnecting) {
                            Log.i(TAG, "Tunnel usable again")
                            clearReconnecting()
                        }
                        continue
                    }
                    // Tor is talking and said no. A busy exit or a destination
                    // that refuses port 80 is not a dead transport, and killing
                    // the runner over it is how a working connection ended up
                    // rebuilding itself every few seconds.
                    ProbeResult.Live -> {
                        failedProbes = 0
                        quietProbes = 0
                        continue
                    }
                    ProbeResult.Dead -> quietProbes = 0
                }

                val since = System.currentTimeMillis()
                if (deadSince == 0L) deadSince = since
                failedProbes++
                val deadForMs = since - deadSince
                Log.w(TAG, "Tunnel silent for ${deadForMs / 1000}s ($failedProbes probe(s))")

                // One answer can be missing because a circuit is mid-build, and
                // from out here that is indistinguishable from a dead tunnel, so
                // the wall clock decides, not the count. Two failures are only
                // the fast path for a tunnel that really has nothing.
                val mustRebuild = failedProbes >= PROBES_BEFORE_RECOVERY &&
                    (deadForMs >= probeGraceMs() || failedProbes >= PROBES_TO_IGNORE_GRACE)

                if (!mustRebuild) continue

                failedProbes = 0
                deadSince = 0
                if (since - lastRecoveryAt < PROBE_RECOVERY_COOLDOWN_MS) {
                    Log.i(
                        TAG,
                        "Not rebuilding ${activeRunner?.name} again so soon, " +
                            "${(PROBE_RECOVERY_COOLDOWN_MS - (since - lastRecoveryAt)) / 1000}s left of the cooldown"
                    )
                    continue
                }
                lastRecoveryAt = since
                recoverTransport()
            }
        }
    }

    /**
     * How long to wait before the next probe of a tunnel that has been answering.
     *
     * A healthy link gets checked often at first and then less often, and any of
     * traffic, a network change or a less than perfect answer puts it back at the
     * front. The point is that the backstop stays a backstop: the cases where a
     * link dies without the system telling us are exactly the cases where the
     * interval is short, because something just happened.
     */
    private fun healthyInterval(): Long = when {
        quietProbes < 2 -> PROBE_INTERVAL_HEALTHY_MS
        quietProbes < 5 -> PROBE_INTERVAL_QUIET_MS
        else -> PROBE_INTERVAL_IDLE_MS
    }

    /**
     * Ask the live tunnel whether Tor can still complete a request.
     *
     * A SOCKS5 CONNECT to a fixed address is the honest test: the reply only
     * turns into 0x00 once Tor has a built circuit, and the socket is closed
     * right after, so a probe costs one circuit check and no payload. Bootstrap
     * percentage cannot be used for this, it is printed once and never revoked
     * when the network disappears.
     *
     * Three outcomes, not two, because they say different things and the caller
     * acts on the difference:
     *  - ok: Tor reached the destination. The tunnel is doing its job.
     *  - live: Tor answered SOCKS5 but not with 0x00. That is a rejection, not a
     *    corpse: a busy exit refusing port 80, or a destination that does not
     *    answer. Treating it as a dead link is what made the whole connection
     *    flap over an exit that dislikes the probe.
     *  - dead: no SOCKS5 answer at all. Either the bridge never spoke, or Tor
     *    never answered within the timeout because it had no circuit. Only this
     *    is a real reason to rebuild, and even this one only after [probeGraceMs]
     *    has passed, because a circuit that is being built right now looks
     *    exactly the same from out here.
     *
     * The ok answer is the one that is not written down. A probe every twenty
     * seconds or slower saying "still fine" is noise in a log the user reads to
     * find out what went wrong, and it is the only message this loop would ever
     * produce on a connection that never goes wrong.
     */
    private fun probeTunnelUsable(): ProbeResult {
        var stage = "connect"
        return try {
            Socket().use { s ->
                stage = "bridge"
                s.connect(InetSocketAddress("127.0.0.1", Config.proxyPort), PROBE_TIMEOUT_MS)
                s.soTimeout = PROBE_TIMEOUT_MS
                val out = s.getOutputStream()
                val input = s.getInputStream()
                out.write(byteArrayOf(0x05, 0x01, 0x00)) // greeting, no auth
                out.flush()
                val version = input.read()
                val method = input.read()
                if (version != 0x05 || method != 0x00) {
                    return logProbe("bridge rejected greeting: $version/$method", ProbeResult.Dead)
                }
                stage = "tor"
                // CONNECT 1.1.1.1:80, then close without sending a byte.
                out.write(byteArrayOf(0x05, 0x01, 0x00, 0x01, 1, 1, 1, 1, 0x00, 0x50))
                out.flush()
                val reply = input.read()
                if (reply == 0x00) {
                    return ProbeResult.Ok
                } else if (reply < 0) {
                    logProbe("Tor closed without answering", ProbeResult.Dead)
                } else {
                    logProbe(
                        "Tor answered 0x%02x, the destination refused it".format(reply),
                        ProbeResult.Live
                    )
                }
            }
        } catch (e: Exception) {
            // No SOCKS5 answer at all. Named, because the difference between
            // "the bridge would not take the connection" and "Tor never
            // answered" is the difference between fixing the app and fixing Tor.
            logProbe("no answer at $stage: ${e.message}", ProbeResult.Dead)
        }
    }

    private fun logProbe(detail: String, result: ProbeResult): ProbeResult {
        Log.d(TAG, "probe ($detail)")
        return result
    }

    /**
     * Grace before a rebuild follows the Tor that has to build the circuit the
     * tunnel is waiting for: CircuitBuildTimeout plus the SocksTimeout a request
     * spends waiting for one. A shorter grace calls a rebuild normal, and a
     * normal rebuild is a full bootstrap that takes longer than the grace, which
     * is the loop the cooldown below exists to break.
     *
     * Both terms are read from the template so editing it cannot silently desync
     * this from what Tor will actually do. That was not true before: the SocksTimeout
     * term was a hard-coded 30s folded into the grace constant, so any template that
     * changed SocksTimeout -- including the tail block, which is what a real
     * connect uses -- left the grace short and a healthy rebuild looking like a
     * dead link.
     */
    private fun probeGraceMs(): Long =
        (TorrcSettings.intValue("CircuitBuildTimeout", 40) +
            TorrcSettings.intValue("SocksTimeout", 30)) * 1000L + PROBE_GRACE_MARGIN_MS

    /**
     * Rebuild the transport that was carrying traffic, keeping the tunnel up.
     *
     * The replacement runs on the same port with the same bridge lines, so the
     * app's SOCKS bridge only needs a repoint and every app connection survives
     * the swap; nothing on the TUN side is torn down. If even that cannot
     * bootstrap, the ordinary connect flow takes over, which is slower but
     * re-races every transport from scratch.
     */
    private suspend fun recoverTransport() {
        if (!recovering.compareAndSet(false, true)) return
        val startedAt = System.currentTimeMillis()
        holdCpu()
        try {
            val previous = activeRunner
            if (previous == null) {
                escalateToFullReconnect("no active transport")
                return
            }
            markReconnecting("rebuilding ${previous.name}")
            val winner = try {
                ParallelTorManager.restartTransport(
                    context = applicationContext,
                    basePort = Config.proxyPort,
                    sessionId = currentSession,
                    name = previous.name
                ) { snapshot ->
                    AppState.update {
                        it.copy(
                            transports = snapshot.mapValues { (n, r) ->
                                if (r.failed != null) -1 else r.progress()
                            }
                        )
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Transport rebuild failed: ${e.message}")
                escalateToFullReconnect(e.message ?: "transport rebuild failed")
                return
            }

            TorSocksBridge.repoint(winner.torSocksPort)
            activeRunner = winner
            AppState.update {
                it.copy(
                    transports = mapOf(winner.name to 100),
                    transport = winner.name,
                    reconnecting = false
                )
            }
            val seconds = (System.currentTimeMillis() - startedAt) / 1000
            Log.i(TAG, "Recovered via ${winner.name} in ${seconds}s (SOCKS5 ${winner.torSocksPort})")
            startForeground(
                NOTIFICATION_ID,
                buildNotification("Reconnected via ${winner.name} \u00b7 ${seconds}s", progress = false)
            )
            lastRenderedText = ""
            // A fresh transport has just proved itself, so the careful cadence
            // starts again rather than carrying over whatever it ended on.
            quietProbes = 0
            bytesAtLastProbe = AppState.state.value.txBytes + AppState.state.value.rxBytes
            // The new Tor has its own exit circuits, so the reported country is
            // stale until it is looked up again.
            startExitLocator("127.0.0.1", Config.proxyPort)
        } catch (e: Exception) {
            Log.e(TAG, "Recovery failed", e)
            escalateToFullReconnect(e.message ?: "recovery failed")
        } finally {
            recovering.set(false)
            releaseCpu()
        }
    }

    /**
     * Hand over to the normal connect flow. The teardown runs in a fresh job so
     * the cancellation inside [teardown] cannot abort the handover itself.
     */
    private fun escalateToFullReconnect(reason: String) {
        Log.w(TAG, "Falling back to a full reconnect ($reason)")
        markReconnecting("reconnecting")
        serviceScope.launch {
            teardown()
            connect()
        }
    }

    private fun markReconnecting(reason: String) {
        // A link event can arrive just after the user hit stop. Reconnecting now
        // would start cores again in the middle of the teardown, so the stop
        // wins and the button stays locked.
        if (AppState.state.value.stopping) {
            Log.w(TAG, "Ignoring reconnect ($reason): a stop is tearing the cores down")
            return
        }
        Log.w(TAG, "Reconnecting: $reason")
        AppState.update { it.copy(reconnecting = true) }
        updateNotification("Reconnecting\u2026", progress = false, progressValue = 0)
    }

    private fun clearReconnecting() {
        AppState.update { it.copy(reconnecting = false) }
    }

    private fun registerNetworkCallback() {
        if (networkCallback != null) return
        val cm = getSystemService(CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onLost(network: Network) {
                // Losing one network is routine when the system moves between
                // Wi-Fi and cellular, so only a real absence of any counts.
                if (cm.activeNetwork != null) return
                linkUp = false
                Log.w(TAG, "Network lost")
                if (AppState.state.value.connected) markReconnecting("network down")
            }

            override fun onAvailable(network: Network) {
                if (linkUp) return
                linkUp = true
                failedProbes = 0
                quietProbes = 0
                Log.i(TAG, "Network available again")
            }
        }
        try {
            cm.registerDefaultNetworkCallback(callback)
            networkCallback = callback
            linkUp = cm.activeNetwork != null
        } catch (e: Exception) {
            Log.w(TAG, "Network callback unavailable: ${e.message}")
        }
    }

    private fun unregisterNetworkCallback() {
        val callback = networkCallback ?: return
        networkCallback = null
        try {
            (getSystemService(CONNECTIVITY_SERVICE) as? ConnectivityManager)
                ?.unregisterNetworkCallback(callback)
        } catch (e: Exception) {
            Log.d(TAG, "unregisterNetworkCallback: ${e.message}")
        }
    }

    private fun formatBytes(bytes: Long): String {
        if (bytes < 1) return "0 B"
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        var v = bytes.toFloat()
        var idx = 0
        while (v >= 1024 && idx < units.size - 1) {
            v /= 1024f
            idx++
        }
        return if (idx == 0) "${v.toInt()} ${units[idx]}" else String.format("%.1f %s", v, units[idx])
    }

    private fun formatBytes(bytes: Float): String {
        return formatBytes(bytes.toLong())
    }

    private fun startExitLocator(proxyHost: String, proxyPort: Int) {
        // Every recovery starts the locator again, so the previous run is replaced
        // rather than left to finish: otherwise N recoveries mean N loops of up to
        // six 8s probes competing for the circuit that was just rebuilt.
        exitLocatorJob?.cancel()
        exitLocatorJob = serviceScope.launch {
            val selected = ExitNodes.currentCodes()
                .map { it.trim().uppercase() }
                .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
                .distinct()
            try {
                if (selected.isNotEmpty()) {
                    // The very first circuit of the session is the fast, unrestricted
                    // one; the post-bootstrap SETCONF + NEWNYM needs a moment to steer
                    // the next circuits into the selected countries before the reported
                    // location can match the choice.
                    Log.i("ExitNode", "waiting for live exit switch before locating (selected: ${selected.joinToString(",")})")
                    delay(5_000)
                }
                val tries = if (selected.isEmpty()) 1 else 6
                for (attempt in 1..tries) {
                    val info = ExitLocator.locate(this@TorVpnService, proxyHost, proxyPort, timeoutMs = 8_000)
                    if (info == null) {
                        Log.w("ExitNode", "location probe attempt $attempt/$tries failed (no traffic yet?)")
                    } else {
                        AppState.update {
                            it.copy(
                                exitIp = info.ip,
                                exitCode = info.countryCode,
                                exitName = info.countryName.ifBlank { info.city }
                            )
                        }
                        val match = info.countryCode.uppercase() in selected
                        Log.i(
                            "ExitNode",
                            "located ${info.ip} \u00b7 ${info.label()}" +
                                (if (selected.isEmpty()) "" else " \u00b7 ${if (match) "MATCHES ${selected.joinToString(",")}" else "NOT yet ${selected.joinToString(",")}"}") +
                                (if (info.asn.isNotBlank()) " \u00b7 ${info.asn}" else "")
                        )
                        if (selected.isEmpty() || match) return@launch
                    }
                    if (attempt < tries) delay(8_000)
                }
                if (selected.isNotEmpty()) {
                    Log.w("ExitNode", "did not observe a ${selected.joinToString(",")} exit after $tries tries; connection still up")
                }
            } catch (e: Exception) {
                Log.w("ExitNode", "exit locator failed: ${e.message}")
            }
        }
    }

    private fun fail(message: String) {
        // A connect that was already in flight when the user hit stop can fail
        // on its way out. Reporting that now would clear the stop flag while the
        // teardown is still running, which unlocks the button and lets a start
        // race the cores for their ports, and it would run a second teardown
        // concurrently with the first. The stop owns the teardown; the failure
        // is only worth recording if nothing was stopping.
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring '$message': a stop is already tearing the cores down")
            return
        }
        Log.e(TAG, message)
        Log.endSession("failed \u00b7 $message")
        AppState.update { it.copy(connecting = false, connected = false, reconnecting = false, torRunning = false, stopping = false, error = message, socksEndpoint = "") }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        AppState.markStopped()
        teardown()
    }

    private fun disconnect() {
        if (AppState.state.value.stopping) {
            Log.i(TAG, "Ignoring disconnect: a stop is already tearing the cores down")
            return
        }
        val s = AppState.state.value
        if (!s.connected && !s.connecting && !s.reconnecting && !s.torRunning && s.socksEndpoint.isEmpty()) {
            Log.i(TAG, "Nothing to disconnect")
            return
        }
        val startedAt = SystemClock.elapsedRealtime()
        Log.i(TAG, "Disconnecting: TUN, tunnel and every Tor core")
        AppState.update {
            it.copy(
                stopping = true,
                connecting = false,
                connected = false,
                reconnecting = false,
                error = null,
                socksEndpoint = ""
            )
        }
        updateNotification("Stopping", progress = true, progressValue = 0)
        serviceScope.launch {
            teardown()
            val left = STOPPING_MIN_MS - (SystemClock.elapsedRealtime() - startedAt)
            if (left > 0) {
                Log.i(TAG, "cores are gone, holding STOPPING for another ${left}ms")
                delay(left)
            }
            Log.endSession("disconnected by user")
            Log.i(TAG, "disconnected: no Tor core is left running")
            AppState.update { it.copy(stopping = false, torRunning = false) }
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    /**
     * Take down the TUN and the tun2socks data path, and nothing else.
     *
     * This is the half of [teardown] that [stopVpn] uses: the VPN interface is
     * closed so the device stops routing through Tor, but the SOCKS5 bridge and
     * every Tor core survive, still bound to their ports and still bootstrapped.
     */
    private fun teardownVpn() {
        statsJob?.cancel()
        statsJob = null
        linkWatchJob?.cancel()
        linkWatchJob = null
        exitLocatorJob?.cancel()
        exitLocatorJob = null
        quietProbes = 0
        bytesAtLastProbe = 0L
        unregisterNetworkCallback()
        try { HevSocks5Tunnel.stop() } catch (_: Exception) {}
        try { vpnInterface?.close() } catch (_: Exception) {}
        vpnInterface = null
        if (AppState.vpnStarted) {
            AppState.markStopped()
        }
    }

    private fun teardown() {
        teardownVpn()
        try { TorSocksBridge.stop() } catch (_: Exception) {}
        // Blocks until every Tor/lyrebird process is really gone and the ports are
        // released, so a connect right after a stop cannot hit EADDRINUSE.
        try { ParallelTorManager.stopAllAndWait(Config.proxyPort) } catch (_: Exception) {}
        activeRunner = null
        dropCpu()
        if (AppState.vpnStarted) {
            AppState.markStopped()
        }
    }

    private fun buildNotification(text: String, progress: Boolean, progressValue: Int = 0): Notification {
        return withStateActions(
            NotificationCompat.Builder(this, CHANNEL_VPN_STATUS)
                .setSmallIcon(R.drawable.ic_tor)
                .setContentTitle("DeltaTor")
                .setContentText(text)
                .setContentIntent(mainPendingIntent)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .setProgress(100, progressValue, progress)
        ).build()
    }

    /**
     * Post a notification and forget what the traffic line last rendered, because
     * whatever this one says is now what is on screen.
     */
    private fun postNotification(notification: Notification) {
        lastRenderedText = ""
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification)
    }

    private fun updateNotification(text: String, progress: Boolean, progressValue: Int) {
        postNotification(buildNotification(text, progress, progressValue))
    }

    /**
     * The three intents the notification carries, built once.
     *
     * They never vary: same activity, same two actions, same request codes. Every
     * build was handing a fresh one to the framework anyway, and each of those is
     * a call across to the system server. With a notification rebuilt once a
     * second that is three pointless Binder calls a second for the life of the
     * connection.
     */
    private val mainPendingIntent: PendingIntent by lazy {
        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        PendingIntent.getActivity(
            this,
            1,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private val disconnectPendingIntent: PendingIntent by lazy {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = ACTION_DISCONNECT
        }
        PendingIntent.getService(
            this,
            2,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private val stopVpnPendingIntent: PendingIntent by lazy {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = ACTION_STOP_VPN
        }
        PendingIntent.getService(
            this,
            3,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    /** Puts the VPN back on top of a core that is already bootstrapped. */
    private val startVpnPendingIntent: PendingIntent by lazy {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = ACTION_START_VPN
        }
        PendingIntent.getService(
            this,
            4,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }
}
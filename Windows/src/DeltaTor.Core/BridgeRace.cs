using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// One line the "foreground notification" (Windows: tray tooltip / toast)
/// should render, or null to remove it entirely.
/// </summary>
/// <param name="Title">Notification title ("DeltaTor", or the traffic line's).</param>
/// <param name="Body">Notification body text.</param>
/// <param name="Progress">True while a determinate progress bar is wanted.</param>
/// <param name="ProgressValue">0..100 while <paramref name="progress"/> is on.</param>
public sealed record EngineNotification(string Title, string Body, bool Progress, int ProgressValue);

/// <summary>
/// The engine controller — the Windows counterpart of Android's
/// TorVpnService, implementing the exact same state machine and constants:
///
///  - CONNECT / DISCONNECT / START_VPN / STOP_VPN as direct method calls.
///  - Stall watchdog: 60 s high-water mark over live runner percentages, then
///    a one-shot AutoRecovery (mode → auto) with the exact notice title/body.
///  - Liveness probe: SOCKS5 CONNECT to 1.1.1.1:80 through the local bridge,
///    6 s timeout, Ok / Live / Dead, 2-failure + grace rule (grace read from
///    the torrc template), 90 s rebuild cooldown, 5/20/45/90 s intervals.
///  - Transport recovery via RestartTransport + bridge repoint, escalating to
///    a full reconnect on failure.
///  - Stats cadence 1 s busy / 5 s idle, repaint only when the text changed.
///  - Exit-locator retry policy, STOPPING ≥ 3 s hold, teardown ordering
///    (tunnel → bridge → cores → wait ports free).
///
/// Not ported (plan decision 1 / §2.5 Windows): split tunnelling (the engine
/// always establishes the full tunnel; proxy-only mode stays the selective
/// escape hatch), Android TUN building (the Wintun adapter is created by the
/// hev-socks5-tunnel Windows backend behind <see cref="TunnelEngine"/>), the
/// wake lock (a no-op on desktop; the counted windows are kept as a hook) and
/// Android notifications (replaced by <see cref="NotificationChanged"/>).
/// </summary>
public static class BridgeRace
{
    // Android log tag kept verbatim so log lines match 1:1 across platforms.
    private const string TAG = "TorVpnService";

    private const int VpnMtu = 1280;
    private const string VpnAddress = "10.255.255.1";
    private const string VpnRoute = "0.0.0.0";
    private const string VpnDns = "8.8.8.8";

    /** How often the connected tunnel is asked whether Tor can still carry traffic. */
    private const long ProbeIntervalMs = 5_000L;

    /**
     * The same check while nothing is wrong, to keep the request itself rare.
     *
     * The first two steps of a healthy tunnel. After that the interval opens
     * up: a probe is a real request through Tor, not a local question, and a
     * connection nobody is using was paying for one every twenty seconds to
     * be told what it already knew.
     */
    private const long ProbeIntervalHealthyMs = 20_000L;
    private const long ProbeIntervalQuietMs = 45_000L;
    private const long ProbeIntervalIdleMs = 90_000L;

    /**
     * Consecutive failed probes before the transport is rebuilt. Two is one
     * full interval of grace: long enough that a single dropped request does
     * not throw away a working circuit, short enough that a real outage is
     * noticed while the user is still looking at the screen.
     */
    private const int ProbesBeforeRecovery = 2;

    /**
     * Failures after which the wait is abandoned and the rebuild happens
     * anyway. A transport that has answered nothing at all for this many
     * probes in a row is not merely busy building a circuit, so waiting out
     * the grace window would only delay an already obvious answer.
     */
    private const int ProbesToIgnoreGrace = 6;

    /**
     * Slack on top of the Tor-derived grace in ProbeGraceMs(). A circuit being
     * built is silent, and from the outside that is indistinguishable from a
     * tunnel that is gone, so the grace has to outlast one build plus the wait
     * a request makes for it. This is only the margin on top of that sum, not
     * the whole grace.
     */
    private const long ProbeGraceMarginMs = 5_000L;

    /**
     * A restore is a clean Tor that still has to build its first circuit, so
     * for a while after it the tunnel is honestly allowed to be quiet. The
     * app used to start rebuilding again the moment two probes ran into that
     * silence, which is a loop: each rebuild costs a full bootstrap and the
     * next rebuild is due before the tunnel ever carried anything.
     */
    private const long ProbeRecoveryCooldownMs = 90_000L;

    /** Upper bound on one liveness probe; a dead Tor would otherwise hang it. */
    private const int ProbeTimeoutMs = 6_000;

    /**
     * How often the traffic counters are read and republished while bytes are
     * actually moving.
     */
    private const long StatsIntervalBusyMs = 1_000L;

    /**
     * The same, for a tunnel nobody is using. A connected tunnel spends most
     * of its life idle, and idle is exactly when there is nothing new to draw:
     * the counters stand still and republishing them once a second only woke
     * the machine up to redraw numbers that had not changed.
     */
    private const long StatsIntervalIdleMs = 5_000L;

    /**
     * How long the UI stays in STOPPING after the cores are gone. Long enough
     * that the phase is actually seen instead of flashing past, and it also
     * covers the teardown itself when that is the slower of the two.
     */
    private const long StoppingMinMs = 3_000L;

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
    private const long StallRecoveryAfterMs = 60_000L;

    /// <summary>Title shared by every automatic-recovery notice.</summary>
    public const string RecoveryTitle = "DELTATOR CHANGED THE CONNECTION MODE";

    /// <summary>
    /// The line the tray tooltip / toast should show, or null to remove the
    /// notification. Raised from arbitrary worker threads; the UI marshals.
    /// </summary>
    public static event Action<EngineNotification?>? NotificationChanged;

    private static TorRunner? _activeRunner;

    private static CancellationTokenSource? _statsCts;
    private static CancellationTokenSource? _linkWatchCts;
    private static CancellationTokenSource? _exitLocatorCts;

    private static NetworkAvailabilityChangedEventHandler? _netHandler;

    // --- Stall watchdog state, reset once per connect attempt.
    //
    // The high-water mark rather than the latest reading is what makes this work:
    // bootstrap percentages are not monotonic per runner -- a runner that is
    // fetching microdescriptors can report 40, then 25 again -- so watching the
    // latest number would reset the timer on noise and a dead race could sit
    // there forever. Only a new maximum counts as progress.
    private static int _stallBest = -1;
    private static long _stallSince;
    private static bool _stallFired;

    private static readonly object RecoveriesLock = new();

    /**
     * Recoveries already spent in this connect, by kind.
     *
     * The loop that retries on a recovery is only provably finite because each
     * kind fires once: the only kind left switches the mode to auto, and the rule
     * that produces it cannot match again once the mode is auto. If that write
     * silently failed, this set is what stops the app from restarting the connect
     * over and over instead of getting anywhere.
     */
    private static readonly HashSet<NoticeKind> RecoveriesSpent = new();

    // --- Link liveness, written by the network-change handler and the watch loop.
    private static volatile bool _linkUp = true;
    private static volatile int _failedProbes;
    private static volatile int _quietProbes;
    private static long _deadSince;
    private static long _lastRecoveryAt;
    private static long _bytesAtLastProbe;

    /** 1 while a transport rebuild is in flight (AtomicBoolean on Android). */
    private static int _recovering;

    /// <summary>Id of the connect session whose lines this service emits.</summary>
    private static volatile int _currentSession;

    /**
     * What the connected notification last rendered, so an idle tunnel can leave
     * the notification alone instead of republishing the same two numbers.
     */
    private static string _lastRenderedText = "";

    // Wake-lock counters: the windows are still counted so a future
    // SetThreadExecutionState hook has the same shape, but desktops do not
    // suspend mid-connect and nothing is held today.
    private static readonly object CpuLock = new();
    private static int _cpuHolds;

    /** What one liveness probe found, as far as it could tell. */
    private enum ProbeResult { Ok, Live, Dead }

    /**
     * Unwinds a transport race that the stall watchdog has given up on.
     *
     * Thrown from the race's progress callback, which is the only place in the
     * poll loop that can see the stall, and caught by the attempt that started
     * the race. It is not an error: nothing failed, the app simply decided the
     * plan was wrong and is about to run a different one. Carrying the recovery
     * on the exception is what keeps that decision on the same stack as the
     * attempt it belongs to.
     */
    private sealed class RestartInAuto : Exception
    {
        public NoticeKind Recovery { get; }
        public RestartInAuto(NoticeKind recovery)
            : base($"connect restarted via {recovery}") => Recovery = recovery;
    }

    // --- Commands (Android onStartCommand actions) --------------------------

    /// <summary>Connect, and retry in auto if the user's own choice turns out to be blocked.</summary>
    public static void Connect()
    {
        if (AppState.State.Stopping)
        {
            AppLog.I(TAG, "Ignoring connect: a stop is still tearing the cores down");
            return;
        }
        if (AppState.VpnStarted)
        {
            AppLog.I(TAG, "Already running");
            return;
        }
        AppState.MarkStarted();
        _currentSession = AppLog.BeginSession("connect");
        lock (RecoveriesLock) RecoveriesSpent.Clear();
        AppState.Update(s => s with
        {
            Connecting = true,
            Connected = false,
            Reconnecting = false,
            TorRunning = false,
            Error = null,
            Transports = new Dictionary<string, int>(StringComparer.Ordinal),
            Transport = "",
            SocksEndpoint = ""
        });
        // Published because this is the connect the drawer will be describing,
        // and a recovery may have changed it since the drawer was last open.
        AppState.SetMode(Config.TransportMode);

        Notify("DeltaTor", "Connecting\u2026", progress: true, progressValue: 0);

        HoldCpu();

        _ = Task.Run(async () =>
        {
            try
            {
                await RunConnectFlowAsync();
            }
            catch (Exception e)
            {
                AppLog.E(TAG, "Connect failed", e);
                Fail($"Connect failed: {e.Message}");
            }
            finally
            {
                ReleaseCpu();
            }
        });
    }

    /**
     * Connect, and retry in auto if the user's own choice turns out to be blocked.
     *
     * A recovery returns from RunConnectAttemptAsync instead of being applied
     * here, so the retry is a plain second turn of the loop rather than a connect
     * re-entered from inside itself: one place owns the attempt, one owns the
     * decision to make another, and nothing has to unwind a nested launch to get
     * there.
     */
    private static async Task RunConnectFlowAsync()
    {
        while (true)
        {
            var recovery = await RunConnectAttemptAsync();
            if (recovery == null) return;
            ApplyRecovery(recovery);
        }
    }

    /**
     * One connect attempt against whatever the configuration says right now.
     *
     * Returns the NoticeKind of a recovery that has to be applied before trying
     * again, or null when the attempt finished -- connected, or failed for real.
     * Null covers the failure case because Fail has already reported it and there
     * is nothing left to retry.
     */
    private static async Task<NoticeKind?> RunConnectAttemptAsync()
    {
        var proxyPort = Config.ProxyPort;
        var proxyHost = "127.0.0.1";

        TorSocksBridge.DebugLogging = Config.DebugMode;
        TorSocksBridge.Router = DomainRouter.Disabled;

        // Step 1: Resolve the bridge lists for the selected mode and start its
        // runner(s). In auto mode vanilla / obfs4 / webtunnel race together, plus
        // a fourth "memory" runner on the bridges that provably worked last time.
        // Monitor each transport's bootstrap progress; the first to reach 100%
        // wins and the losing transports are stopped by the manager.
        var mode = ParallelTorManager.Modes.Contains(Config.TransportMode)
            ? Config.TransportMode
            : ParallelTorManager.TransportAuto;
        var autoNames = Config.AutoTransports
            .Where(t => ParallelTorManager.AutoSources.Contains(t))
            .ToList();
        if (autoNames.Count == 0) autoNames = ParallelTorManager.AutoSources.ToList();
        var modeLabel = mode == ParallelTorManager.TransportAuto
            ? string.Join(" / ", autoNames) +
              (BridgeMemory.CountAll() > 0 ? " / memory" : "")
            // A single transport also races its own memory twin whenever that pool
            // has proven bridges, so say so instead of surprising the user later.
            : mode + (BridgeMemory.Count(mode) > 0 ? " + memory" : "");
        AppLog.I(TAG, $"Transport mode: {modeLabel}");
        Notify("DeltaTor", $"Connecting via {modeLabel} \u2026", progress: true, progressValue: 0);

        // Watchdog armed for this attempt only; a later connect starts over.
        _stallBest = -1;
        _stallSince = Environment.TickCount64;
        _stallFired = false;

        TorRunner w;
        try
        {
            w = ParallelTorManager.Race(
                proxyPort,
                _currentSession,
                Config.TransportMode,
                Config.CustomBridges,
                Config.AutoTransports,
                Config.RunMemory,
                onProgress: snapshot =>
                {
                    var progress = snapshot.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Failed != null ? -1 : kv.Value.Progress(),
                        StringComparer.Ordinal);
                    AppState.Update(s => s with { Transports = progress });
                    var maxProg = Math.Max(0, progress.Count > 0 ? progress.Values.Max() : 0);
                    var detail = string.Join("  ", progress.Select(kv =>
                        $"{kv.Key}={(kv.Value < 0 ? "FAIL" : kv.Value + "%")}"));
                    Notify("DeltaTor", detail, progress: true, progressValue: maxProg);

                    // Failed runners report -1 and must not count as a high-water
                    // mark, or a race where everything died instantly would look
                    // like progress up to 0 and never trip.
                    var liveValues = progress.Values.Where(v => v >= 0).ToList();
                    var live = liveValues.Count > 0 ? liveValues.Max() : 0;
                    if (live > _stallBest)
                    {
                        _stallBest = live;
                        _stallSince = Environment.TickCount64;
                    }
                    if (!_stallFired && live < 100 &&
                        Environment.TickCount64 - _stallSince >= StallRecoveryAfterMs)
                    {
                        _stallFired = true;
                        if (!Config.AutoRecovery)
                        {
                            // The user switched this rule off: the mode they picked
                            // is theirs and gets to fail on its own terms. Nothing
                            // else changes, so the attempt simply carries on until
                            // Tor or the race decides it, and the reason lands in
                            // the log and on the screen.
                            AppLog.W(TAG,
                                $"no progress past {live}% for {StallRecoveryAfterMs / 1000}s " +
                                $"in {mode}, but auto recovery is off; leaving it alone");
                        }
                        else
                        {
                            // Only a mode the user chose is recovered from. Auto is
                            // already the plan the app would pick for itself, so a
                            // stall there has nothing to switch to: it fails, and the
                            // reason is on the screen and in the log.
                            var recovery = mode != ParallelTorManager.TransportAuto
                                ? NoticeKind.AutoRecovery
                                : (NoticeKind?)null;
                            if (recovery != null)
                            {
                                bool spent;
                                lock (RecoveriesLock) spent = RecoveriesSpent.Add(recovery.Value);
                                if (spent)
                                {
                                    AppLog.W(TAG,
                                        $"no progress past {live}% for {StallRecoveryAfterMs / 1000}s " +
                                        $"in {mode}; recovering via {recovery.Value}");
                                    // Tear the runners down here rather than waiting
                                    // for the race to notice: stopAll bumps the
                                    // generation, so the loop unwinds on its next
                                    // checkGeneration instead of polling a set of
                                    // processes that are already gone.
                                    ParallelTorManager.StopAll();
                                    throw new RestartInAuto(recovery.Value);
                                }
                            }
                        }
                    }
                });
        }
        catch (RestartInAuto e)
        {
            return e.Recovery;
        }
        catch (Exception e)
        {
            Fail(e.Message.Length > 0 ? e.Message : "All transports failed to bootstrap");
            return null;
        }

        _activeRunner = w;
        // The race is over, so the watchdog has served its purpose.
        AppState.Update(s => s with
        {
            Transports = new Dictionary<string, int>(StringComparer.Ordinal) { [w.Name] = 100 },
            Transport = w.Name
        });
        AppLog.I(TAG, $"Winner transport: {w.Name} (SOCKS5 {proxyHost}:{w.TorSocksPort})");

        // Step 2: Start the SOCKS5 bridge between the tunnel side and the winning
        // Tor instance. The bridge and Tor stay alive across VPN stop/start.
        // The losing runners were killed by race(); their processes are gone by
        // now, but wait for the kernel to release the ports anyway so the bind
        // below cannot lose a race against a dying socket.
        ParallelTorManager.AwaitPortFree(proxyHost, proxyPort);
        var bridgeError = TorSocksBridge.Start(w.TorSocksPort, proxyPort, "127.0.0.1", proxyHost);
        if (bridgeError != null)
        {
            Fail(bridgeError.Message.Length > 0 ? bridgeError.Message : "Failed to start bridge");
            return null;
        }
        AppState.Update(s => s with { TorRunning = true });

        // Step 3+4: TUN interface + tun2socks.
        await EstablishTunnelAsync(w, proxyHost, proxyPort);
        return null;
    }

    /**
     * Change the plan the next attempt will use, and tell the user it happened.
     *
     * The notice is posted before the retry starts, not after it succeeds: the
     * retry may itself stall, and a report that only appears on success would
     * leave the user with a connect that silently changed transports and no
     * explanation for it. The dialog survives the retry because AppState.Notice
     * is not cleared by a connect.
     */
    private static void ApplyRecovery(NoticeKind recovery)
    {
        var wasMode = Config.TransportMode;
        switch (recovery)
        {
            case NoticeKind.AutoRecovery:
                Config.TransportMode = ParallelTorManager.TransportAuto;
                // The drawer has to follow this: it holds its own copy of the
                // mode, and the app is about to connect with the new one.
                AppState.SetMode(Config.TransportMode);
                AppLog.I(TAG, $"Recovery: {wasMode} stalled, switching to auto");
                break;
            // Not a recovery the service can perform; posted by the UI instead.
            case NoticeKind.FirstRun:
                return;
        }

        // The old attempt's runners are already gone and the per-transport map
        // describes processes that no longer exist, so clear it rather than leave
        // the retry drawing progress bars for the dead race.
        AppState.Update(s => s with
        {
            Transports = new Dictionary<string, int>(StringComparer.Ordinal),
            Transport = "",
            Error = null,
            Connecting = true
        });
        // A new session id so the retry's transport log does not interleave with
        // the attempt the user was watching fail.
        _currentSession = AppLog.BeginSession("connect");

        AppState.PostNotice(recovery, RecoveryTitle,
            recovery == NoticeKind.AutoRecovery
                ? $"{wasMode} made no progress for a minute, which on this network " +
                  "usually means it is blocked.\n\n" +
                  "DeltaTor stopped that attempt, switched the connection mode to " +
                  "Auto and started again. Auto races every transport that works " +
                  "here, so it is more likely to find one.\n\n" +
                  "Auto is now your connection mode. If you would rather pick " +
                  "the transport yourself again, change it in Settings."
                : "");
        Notify("DeltaTor", "Restarting in auto \u2026", progress: true, progressValue: 0);
    }

    /// <summary>Establish the tunnel on top of the running Tor engine.</summary>
    private static async Task EstablishTunnelAsync(TorRunner w, string proxyHost, int proxyPort)
    {
        // Proxy mode never brings up a tunnel, so it must not announce one and
        // must not report the missing engine as a failure. Tor is bootstrapped
        // and listening at this point either way; all that differs is whether
        // the machine is pointed at it.
        if (Config.ProxyOnlyMode)
        {
            var endpoint = $"socks5://{proxyHost}:{proxyPort}";
            AppState.Update(s => s with
            {
                Connecting = false,
                Connected = false,
                Reconnecting = false,
                Error = null,
                Transports = new Dictionary<string, int>(StringComparer.Ordinal) { [w.Name] = 100 },
                ConnectedAtMillis = NowMs(),
                SocksEndpoint = endpoint
            });
            Notify("DeltaTor", $"Proxy ready \u00b7 {endpoint}", progress: false, progressValue: 0);
            _quietProbes = 0;
            _bytesAtLastProbe = 0;
            StartExitLocator(proxyHost, proxyPort);
            AppLog.I(TAG, $"Proxy mode ready. Winner: {w.Name}, SOCKS5 at {endpoint}");
            AppLog.EndSession($"proxy ready via {w.Name}");
            // No stats poller and no link watch: both read bytes off the tunnel,
            // which does not exist here, so they would spin on a null handle and
            // paint nothing. The exit lookup above still works, because it talks
            // to Tor over SOCKS rather than through the tunnel.
            return;
        }

        Notify("DeltaTor", "Establishing VPN \u2026", progress: true, progressValue: 0);

        await Task.Delay(200);

        var tunnelError = TunnelEngine.Start(proxyHost, proxyPort, enableUdpTunneling: true, VpnMtu, VpnAddress);
        if (tunnelError != null)
        {
            Fail(tunnelError.Message.Length > 0 ? tunnelError.Message : "Failed to start tunnel");
            return;
        }

        AppState.Update(s => s with
        {
            Connecting = false,
            Connected = true,
            Reconnecting = false,
            Error = null,
            Transports = new Dictionary<string, int>(StringComparer.Ordinal) { [w.Name] = 100 },
            ConnectedAtMillis = NowMs(),
            ExitCode = "",
            ExitName = "",
            ExitIp = "",
            SocksEndpoint = ""
        });
        Notify("DeltaTor", $"Connected via {w.Name} \u00b7 Tor Network", progress: false, progressValue: 0);
        _quietProbes = 0;
        _bytesAtLastProbe = 0;
        StartStatsPolling();
        StartLinkWatch();
        StartExitLocator(proxyHost, proxyPort);
        AppLog.I(TAG, $"DeltaTor connected. Winner: {w.Name}, SOCKS5 at {proxyHost}:{proxyPort}");
        AppLog.EndSession($"connected via {w.Name}");
    }

    /// <summary>Re-establish the tunnel on top of an already-running Tor engine.</summary>
    public static void StartVpn()
    {
        if (AppState.State.Stopping)
        {
            AppLog.I(TAG, "Ignoring start: a stop is still tearing the cores down");
            return;
        }
        if (AppState.State.Connected)
        {
            AppLog.I(TAG, "VPN already active");
            return;
        }
        // In proxy mode the tunnel is never what "stopped", so restarting it
        // cannot be what brings it back either. Say so instead of silently
        // returning, because the button is still on screen and pressing it and
        // watching nothing happen is the worst possible answer.
        if (Config.ProxyOnlyMode)
        {
            AppLog.I(TAG, "Ignoring start: proxy mode is on, there is no VPN to start");
            return;
        }
        var w = _activeRunner;
        if (w == null || !w.IsReady() || !TorSocksBridge.IsRunning())
        {
            AppLog.E(TAG, "Tor not running; full reconnect required");
            Fail("Tor is not running. Reconnect.");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                AppState.Update(s => s with { Connecting = true, Connected = false, Error = null });
                await EstablishTunnelAsync(w, "127.0.0.1", Config.ProxyPort);
            }
            catch (Exception e)
            {
                AppLog.E(TAG, "Start VPN failed", e);
                Fail($"Start VPN failed: {e.Message}");
            }
        });
    }

    /**
     * Turn the tunnel off without touching the Tor engine.
     *
     * The service, the SOCKS5 bridge and every Tor core stay exactly as they
     * are; only the tun2socks data path goes away, so the machine stops routing
     * through Tor while the engine stays bootstrapped and ready. StartVpn puts
     * the tunnel back on top of the same core without a re-bootstrap, which is
     * the whole point of keeping this separate from Disconnect.
     *
     * Because no process is killed here, there is nothing to wait for: the state
     * flips to torRunning straight away instead of holding the STOPPING word for
     * the three seconds a real core teardown needs.
     */
    public static void StopVpn()
    {
        if (AppState.State.Stopping)
        {
            AppLog.I(TAG, "Ignoring stop: a stop is already tearing the cores down");
            return;
        }
        // Stopping the tunnel tears the data path down. In proxy mode there is
        // no tunnel, and the listener the user was told to point their browser
        // at is the thing being kept, so honouring this would shut the proxy off
        // and leave them with a dead address and no error. Disconnect is what
        // stops proxy mode, and it goes through the normal path below.
        if (Config.ProxyOnlyMode)
        {
            AppLog.I(TAG, "Ignoring stop: proxy mode is on, use Disconnect to shut the proxy down");
            return;
        }
        var s = AppState.State;
        if (!s.Connected && !s.Connecting && !s.Reconnecting)
        {
            AppLog.I(TAG, "Nothing to stop: the VPN is not up");
            return;
        }
        AppLog.I(TAG, "Stopping the VPN only; Tor stays running");
        AppState.Update(st => st with
        {
            Stopping = true,
            Connecting = false,
            Connected = false,
            Reconnecting = false,
            Error = null,
            SocksEndpoint = ""
        });
        Notify("DeltaTor", "Turning the VPN off \u2026", progress: true, progressValue: 0);
        _ = Task.Run(() =>
        {
            TeardownVpn();
            // The engine was never touched, so torRunning stays true on purpose.
            AppState.Update(st => st with { Stopping = false, TorRunning = _activeRunner != null });
            AppLog.EndSession("VPN off \u00b7 Tor still running");
            AppLog.I(TAG, "VPN off: tor is still up, ready for a restart without re-bootstrapping");
            if (_activeRunner != null)
            {
                // No stats poller here: the tunnel that fed it is gone, so it
                // would only spin on a null stats handle and repaint nothing.
                Notify("Tor running \u00b7 VPN off", "", progress: false, progressValue: 0);
            }
            else
            {
                // No runner survived; drop to the fully stopped state.
                NotifyRemove();
            }
        });
    }

    // --- Link loss and recovery ------------------------------------------------

    /**
     * Watch the link while the tunnel is up.
     *
     * A dropped connection used to be invisible: the Tor process keeps printing
     * 100%, the interface keeps claiming to be connected, and the wait for Tor
     * to retry its guard connections on its own schedule was as long as Tor
     * decided it should be. This loop asks the tunnel every few seconds whether
     * Tor can still complete a request, and repairs it when the answer stays no.
     */
    private static void StartLinkWatch()
    {
        RegisterNetworkCallback();
        var ct = ReplaceCts(ref _linkWatchCts);
        _ = Task.Run(() => LinkWatchLoopAsync(ct.Token));
    }

    private static async Task LinkWatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var before = AppState.State;
                // A healthy tunnel is only checked now and then, since the probe
                // is a real (if tiny) request through Tor; a link that is down or
                // already failing is watched closely.
                var urgent = before.Reconnecting || !_linkUp || _failedProbes > 0;
                await Task.Delay((int)(urgent ? ProbeIntervalMs : HealthyInterval()), ct);

                var state = AppState.State;
                if (!state.Connected || Volatile.Read(ref _recovering) != 0) continue;

                if (!_linkUp)
                {
                    // Nothing can pass until the link is back; only say so.
                    _quietProbes = 0;
                    if (!state.Reconnecting) MarkReconnecting("network down");
                    continue;
                }

                // Traffic is its own evidence that the tunnel is working, and it
                // also means a dead link would have been noticed by the user.
                var bytes = state.TxBytes + state.RxBytes;
                var carried = bytes != _bytesAtLastProbe;
                _bytesAtLastProbe = bytes;

                switch (ProbeTunnelUsable())
                {
                    case ProbeResult.Ok:
                        _deadSince = 0;
                        _failedProbes = 0;
                        _lastRecoveryAt = 0;
                        _quietProbes = carried ? 0 : _quietProbes + 1;
                        if (state.Reconnecting)
                        {
                            AppLog.I(TAG, "Tunnel usable again");
                            ClearReconnecting();
                        }
                        continue;
                    // Tor is talking and said no. A busy exit or a destination
                    // that refuses port 80 is not a dead transport, and killing
                    // the runner over it is how a working connection ended up
                    // rebuilding itself every few seconds.
                    case ProbeResult.Live:
                        _failedProbes = 0;
                        _quietProbes = 0;
                        continue;
                    default:
                        _quietProbes = 0;
                        break;
                }

                var since = NowMs();
                if (_deadSince == 0) _deadSince = since;
                _failedProbes++;
                var deadForMs = since - _deadSince;
                AppLog.W(TAG, $"Tunnel silent for {deadForMs / 1000}s ({_failedProbes} probe(s))");

                // One answer can be missing because a circuit is mid-build, and
                // from out here that is indistinguishable from a dead tunnel, so
                // the wall clock decides, not the count. Two failures are only
                // the fast path for a tunnel that really has nothing.
                var mustRebuild = _failedProbes >= ProbesBeforeRecovery &&
                                  (deadForMs >= ProbeGraceMs() || _failedProbes >= ProbesToIgnoreGrace);

                if (!mustRebuild) continue;

                _failedProbes = 0;
                _deadSince = 0;
                if (since - _lastRecoveryAt < ProbeRecoveryCooldownMs)
                {
                    AppLog.I(TAG,
                        $"Not rebuilding {_activeRunner?.Name} again so soon, " +
                        $"{(ProbeRecoveryCooldownMs - (since - _lastRecoveryAt)) / 1000}s left of the cooldown");
                    continue;
                }
                _lastRecoveryAt = since;
                await RecoverTransportAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Job cancelled by a teardown; the loop simply ends.
        }
    }

    /**
     * How long to wait before the next probe of a tunnel that has been answering.
     *
     * A healthy link gets checked often at first and then less often, and any
     * of traffic, a network change or a less than perfect answer puts it back
     * at the front. The point is that the backstop stays a backstop: the cases
     * where a link dies without the system telling us are exactly the cases
     * where the interval is short, because something just happened.
     */
    private static long HealthyInterval() =>
        _quietProbes < 2 ? ProbeIntervalHealthyMs :
        _quietProbes < 5 ? ProbeIntervalQuietMs :
        ProbeIntervalIdleMs;

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
     *    is a real reason to rebuild, and even this one only after ProbeGraceMs
     *    has passed, because a circuit that is being built right now looks
     *    exactly the same from out here.
     *
     * The ok answer is the one that is not written down. A probe every twenty
     * seconds or slower saying "still fine" is noise in a log the user reads to
     * find out what went wrong, and it is the only message this loop would ever
     * produce on a connection that never goes wrong.
     */
    private static ProbeResult ProbeTunnelUsable()
    {
        var stage = "connect";
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            stage = "bridge";
            if (!socket.ConnectAsync("127.0.0.1", Config.ProxyPort).Wait(ProbeTimeoutMs))
            {
                return LogProbe("connect timed out", ProbeResult.Dead);
            }
            socket.ReceiveTimeout = ProbeTimeoutMs;
            socket.SendTimeout = ProbeTimeoutMs;
            using var stream = new NetworkStream(socket, ownsSocket: false);

            stream.Write(new byte[] { 0x05, 0x01, 0x00 }); // greeting, no auth
            var version = stream.ReadByte();
            var method = stream.ReadByte();
            if (version != 0x05 || method != 0x00)
            {
                return LogProbe($"bridge rejected greeting: {version}/{method}", ProbeResult.Dead);
            }

            stage = "tor";
            // CONNECT 1.1.1.1:80, then close without sending a byte.
            stream.Write(new byte[] { 0x05, 0x01, 0x00, 0x01, 1, 1, 1, 1, 0x00, 0x50 });
            var reply = stream.ReadByte();
            if (reply == 0x00) return ProbeResult.Ok;
            if (reply < 0) return LogProbe("Tor closed without answering", ProbeResult.Dead);
            return LogProbe(
                $"Tor answered 0x{reply:x2}, the destination refused it",
                ProbeResult.Live);
        }
        catch (Exception e)
        {
            // No SOCKS5 answer at all. Named, because the difference between
            // "the bridge would not take the connection" and "Tor never
            // answered" is the difference between fixing the app and fixing Tor.
            return LogProbe($"no answer at {stage}: {e.Message}", ProbeResult.Dead);
        }
    }

    private static ProbeResult LogProbe(string detail, ProbeResult result)
    {
        AppLog.D(TAG, $"probe ({detail})");
        return result;
    }

    /**
     * Grace before a rebuild follows the Tor that has to build the circuit the
     * tunnel is waiting for: CircuitBuildTimeout plus the SocksTimeout a request
     * spends waiting for one. A shorter grace calls a rebuild normal, and a
     * normal rebuild is a full bootstrap that takes longer than the grace, which
     * is the loop the cooldown below exists to break.
     *
     * Both terms are read from the template so editing it cannot silently
     * desync this from what Tor will actually do.
     */
    private static long ProbeGraceMs() =>
        (TorrcSettings.IntValue("CircuitBuildTimeout", 40) +
         TorrcSettings.IntValue("SocksTimeout", 30)) * 1000L + ProbeGraceMarginMs;

    /**
     * Rebuild the transport that was carrying traffic, keeping the tunnel up.
     *
     * The replacement runs on the same port with the same bridge lines, so the
     * app's SOCKS bridge only needs a repoint and every app connection survives
     * the swap; nothing on the tunnel side is torn down. If even that cannot
     * bootstrap, the ordinary connect flow takes over, which is slower but
     * re-races every transport from scratch.
     */
    private static async Task RecoverTransportAsync()
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        var startedAt = NowMs();
        HoldCpu();
        try
        {
            var previous = _activeRunner;
            if (previous == null)
            {
                EscalateToFullReconnect("no active transport");
                return;
            }
            MarkReconnecting($"rebuilding {previous.Name}");

            TorRunner winner;
            try
            {
                winner = await Task.Run(() => ParallelTorManager.RestartTransport(
                    Config.ProxyPort,
                    _currentSession,
                    previous.Name,
                    snapshot => AppState.Update(s => s with
                    {
                        Transports = snapshot.ToDictionary(
                            kv => kv.Key,
                            kv => kv.Value.Failed != null ? -1 : kv.Value.Progress(),
                            StringComparer.Ordinal)
                    })));
            }
            catch (Exception e)
            {
                AppLog.E(TAG, $"Transport rebuild failed: {e.Message}");
                EscalateToFullReconnect(e.Message.Length > 0 ? e.Message : "transport rebuild failed");
                return;
            }

            TorSocksBridge.Repoint(winner.TorSocksPort);
            _activeRunner = winner;
            AppState.Update(s => s with
            {
                Transports = new Dictionary<string, int>(StringComparer.Ordinal) { [winner.Name] = 100 },
                Transport = winner.Name,
                Reconnecting = false
            });
            var seconds = (NowMs() - startedAt) / 1000;
            AppLog.I(TAG, $"Recovered via {winner.Name} in {seconds}s (SOCKS5 {winner.TorSocksPort})");
            Notify("DeltaTor", $"Reconnected via {winner.Name} \u00b7 {seconds}s", progress: false, progressValue: 0);
            // A fresh transport has just proved itself, so the careful cadence
            // starts again rather than carrying over whatever it ended on.
            _quietProbes = 0;
            _bytesAtLastProbe = AppState.State.TxBytes + AppState.State.RxBytes;
            // The new Tor has its own exit circuits, so the reported country is
            // stale until it is looked up again.
            StartExitLocator("127.0.0.1", Config.ProxyPort);
        }
        catch (Exception e)
        {
            AppLog.E(TAG, "Recovery failed", e);
            EscalateToFullReconnect(e.Message.Length > 0 ? e.Message : "recovery failed");
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
            ReleaseCpu();
        }
    }

    /**
     * Hand over to the normal connect flow. The teardown runs in a fresh task
     * so nothing inside it can abort the handover itself.
     */
    private static void EscalateToFullReconnect(string reason)
    {
        AppLog.W(TAG, $"Falling back to a full reconnect ({reason})");
        MarkReconnecting("reconnecting");
        _ = Task.Run(() =>
        {
            Teardown();
            Connect();
        });
    }

    private static void MarkReconnecting(string reason)
    {
        // A link event can arrive just after the user hit stop. Reconnecting now
        // would start cores again in the middle of the teardown, so the stop
        // wins and the button stays locked.
        if (AppState.State.Stopping)
        {
            AppLog.W(TAG, $"Ignoring reconnect ({reason}): a stop is tearing the cores down");
            return;
        }
        AppLog.W(TAG, $"Reconnecting: {reason}");
        AppState.Update(s => s with { Reconnecting = true });
        Notify("DeltaTor", "Reconnecting\u2026", progress: false, progressValue: 0);
    }

    private static void ClearReconnecting() =>
        AppState.Update(s => s with { Reconnecting = false });

    // --- Network availability (the Android ConnectivityManager callback) ------

    private static void RegisterNetworkCallback()
    {
        if (_netHandler != null) return;
        try
        {
            _linkUp = NetworkInterface.GetIsNetworkAvailable();
            _netHandler = OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAvailabilityChanged += _netHandler;
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"Network callback unavailable: {e.Message}");
        }
    }

    private static void UnregisterNetworkCallback()
    {
        var handler = _netHandler;
        if (handler == null) return;
        _netHandler = null;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= handler;
        }
        catch (Exception e)
        {
            AppLog.D(TAG, $"unregisterNetworkCallback: {e.Message}");
        }
    }

    private static void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            if (_linkUp) return;
            _linkUp = true;
            _failedProbes = 0;
            _quietProbes = 0;
            AppLog.I(TAG, "Network available again");
        }
        else
        {
            // Losing one network is routine when the system moves between
            // interfaces, so only a real absence of any counts.
            _linkUp = false;
            AppLog.W(TAG, "Network lost");
            if (AppState.State.Connected) MarkReconnecting("network down");
        }
    }

    // --- Stats polling ---------------------------------------------------------

    /**
     * Publish the traffic counters, and only when there is something new to say.
     *
     * The counters are read every second while bytes are moving, because that
     * is when they are the point. Once the tunnel goes quiet they are read
     * every few seconds and the notification is only redrawn when its text
     * actually differs from what is already on screen.
     *
     * That check is the whole point. Re-posting a notification that says the
     * same thing is not free even when nothing changes, and a tunnel that
     * nobody is using is the state it spends most of its life in.
     */
    private static void StartStatsPolling()
    {
        var ct = ReplaceCts(ref _statsCts);
        _lastRenderedText = "";
        _ = Task.Run(() => StatsLoopAsync(ct.Token));
    }

    private static async Task StatsLoopAsync(CancellationToken ct)
    {
        long lastTx = 0;
        long lastRx = 0;
        long lastTime = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stats = TunnelEngine.GetStats();
                if (stats == null)
                {
                    await Task.Delay((int)StatsIntervalIdleMs, ct);
                    continue;
                }

                var now = NowMs();
                var dtSeconds = Math.Max(0.001f, Math.Max(now - lastTime, 1000) / 1000f);
                var upSpeed = lastTime > 0 ? (float)(stats.TxBytes - lastTx) / dtSeconds : 0f;
                var downSpeed = lastTime > 0 ? (float)(stats.RxBytes - lastRx) / dtSeconds : 0f;
                var moving = stats.TxBytes != lastTx || stats.RxBytes != lastRx;
                lastTx = stats.TxBytes;
                lastRx = stats.RxBytes;
                lastTime = now;

                AppState.Update(s => s with
                {
                    TxBytes = stats.TxBytes,
                    RxBytes = stats.RxBytes,
                    TxSpeed = upSpeed,
                    RxSpeed = downSpeed
                });

                // Reconnecting is what the user is watching, so it keeps the fast
                // cadence; a still tunnel has nothing to hurry.
                var busy = moving || AppState.State.Reconnecting;
                var text = TrafficText(upSpeed, downSpeed, stats.TxBytes, stats.RxBytes);
                if (busy || text != _lastRenderedText)
                {
                    NotifyTraffic(text);
                    _lastRenderedText = text;
                }
                await Task.Delay((int)(busy ? StatsIntervalBusyMs : StatsIntervalIdleMs), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Job cancelled by a teardown; the loop simply ends.
        }
    }

    /// <summary>The connected notification's title and body, as one string to compare.</summary>
    private static string TrafficText(float upSpeed, float downSpeed, long txBytes, long rxBytes)
    {
        return AppState.State.Reconnecting
            ? "DeltaTor \u2014 Reconnecting|Restoring the tunnel\u2026"
            : "DeltaTor \u2014 Connected|\u2191 " + FormatBytes(upSpeed) + "/s  \u2193 " + FormatBytes(downSpeed) + "/s\n" +
              "Total: \u2191 " + FormatBytes(txBytes) + "  \u2193 " + FormatBytes(rxBytes);
    }

    private static void NotifyTraffic(string text)
    {
        var parts = text.Split('|');
        Notify(parts[0], parts[1], progress: false, progressValue: 0);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var v = (float)bytes;
        var idx = 0;
        while (v >= 1024 && idx < units.Length - 1)
        {
            v /= 1024f;
            idx++;
        }
        return idx == 0
            ? $"{(int)v} {units[idx]}"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", v, units[idx]);
    }

    private static string FormatBytes(float bytes) => FormatBytes((long)bytes);

    // --- Exit locator ----------------------------------------------------------

    private static void StartExitLocator(string proxyHost, int proxyPort)
    {
        // Every recovery starts the locator again, so the previous run is
        // replaced rather than left to finish: otherwise N recoveries mean N
        // loops of up to six 8s probes competing for the circuit that was just
        // rebuilt.
        var ct = ReplaceCts(ref _exitLocatorCts);
        _ = Task.Run(() => ExitLocatorLoopAsync(proxyHost, proxyPort, ct.Token));
    }

    private static async Task ExitLocatorLoopAsync(string proxyHost, int proxyPort, CancellationToken ct)
    {
        try
        {
            var selected = ExitNodes.CurrentCodes()
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z'))
                .ToHashSet(StringComparer.Ordinal);
            if (selected.Count > 0)
            {
                // The very first circuit of the session is the fast,
                // unrestricted one; the post-bootstrap SETCONF + NEWNYM needs a
                // moment to steer the next circuits into the selected countries
                // before the reported location can match the choice.
                AppLog.I("ExitNode", $"waiting for live exit switch before locating (selected: {string.Join(",", selected)})");
                await Task.Delay(5_000, ct);
            }
            var tries = selected.Count == 0 ? 1 : 6;
            for (var attempt = 1; attempt <= tries; attempt++)
            {
                var info = ExitLocator.Locate(proxyHost, proxyPort, timeoutMs: 8_000);
                if (info == null)
                {
                    AppLog.W("ExitNode", $"location probe attempt {attempt}/{tries} failed (no traffic yet?)");
                }
                else
                {
                    AppState.Update(s => s with
                    {
                        ExitIp = info.Ip,
                        ExitCode = info.CountryCode,
                        ExitName = info.CountryName.Length > 0 ? info.CountryName : info.City
                    });
                    var match = selected.Contains(info.CountryCode.ToUpperInvariant());
                    AppLog.I("ExitNode",
                        $"located {info.Ip} \u00b7 {info.Label()}" +
                        (selected.Count == 0
                            ? ""
                            : $" \u00b7 {(match ? "MATCHES " + string.Join(",", selected) : "NOT yet " + string.Join(",", selected))}") +
                        (info.Asn.Length > 0 ? $" \u00b7 {info.Asn}" : ""));
                    if (selected.Count == 0 || match) return;
                }
                if (attempt < tries) await Task.Delay(8_000, ct);
            }
            if (selected.Count > 0)
            {
                AppLog.W("ExitNode", $"did not observe a {string.Join(",", selected)} exit after {tries} tries; connection still up");
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer locator run; this one is done.
        }
        catch (Exception e)
        {
            AppLog.W("ExitNode", $"exit locator failed: {e.Message}");
        }
    }

    // --- Failure / disconnect / teardown ---------------------------------------

    private static void Fail(string message)
    {
        // A connect that was already in flight when the user hit stop can fail
        // on its way out. Reporting that now would clear the stop flag while the
        // teardown is still running, which unlocks the button and lets a start
        // race the cores for their ports, and it would run a second teardown
        // concurrently with the first. The stop owns the teardown; the failure
        // is only worth recording if nothing was stopping.
        if (AppState.State.Stopping)
        {
            AppLog.I(TAG, $"Ignoring '{message}': a stop is already tearing the cores down");
            return;
        }
        AppLog.E(TAG, message);
        AppLog.EndSession($"failed \u00b7 {message}");
        AppState.Update(s => s with
        {
            Connecting = false,
            Connected = false,
            Reconnecting = false,
            TorRunning = false,
            Stopping = false,
            Error = message,
            SocksEndpoint = ""
        });
        NotifyRemove();
        AppState.MarkStopped();
        Teardown();
    }

    public static void Disconnect()
    {
        if (AppState.State.Stopping)
        {
            AppLog.I(TAG, "Ignoring disconnect: a stop is already tearing the cores down");
            return;
        }
        var s = AppState.State;
        if (!s.Connected && !s.Connecting && !s.Reconnecting && !s.TorRunning && s.SocksEndpoint.Length == 0)
        {
            AppLog.I(TAG, "Nothing to disconnect");
            return;
        }
        var startedAt = Environment.TickCount64;
        AppLog.I(TAG, "Disconnecting: tunnel, bridge and every Tor core");
        AppState.Update(st => st with
        {
            Stopping = true,
            Connecting = false,
            Connected = false,
            Reconnecting = false,
            Error = null,
            SocksEndpoint = ""
        });
        Notify("DeltaTor", "Stopping", progress: true, progressValue: 0);
        _ = Task.Run(async () =>
        {
            Teardown();
            var left = StoppingMinMs - (Environment.TickCount64 - startedAt);
            if (left > 0)
            {
                AppLog.I(TAG, $"cores are gone, holding STOPPING for another {left}ms");
                await Task.Delay((int)left);
            }
            AppLog.EndSession("disconnected by user");
            AppLog.I(TAG, "disconnected: no Tor core is left running");
            AppState.Update(st => st with { Stopping = false, TorRunning = false });
            NotifyRemove();
        });
    }

    /**
     * Take down the tun2socks data path, and nothing else.
     *
     * This is the half of Teardown that StopVpn uses: the tunnel is closed so
     * the machine stops routing through Tor, but the SOCKS5 bridge and every
     * Tor core survive, still bound to their ports and still bootstrapped.
     */
    private static void TeardownVpn()
    {
        CancelCts(ref _statsCts);
        CancelCts(ref _linkWatchCts);
        CancelCts(ref _exitLocatorCts);
        _quietProbes = 0;
        _bytesAtLastProbe = 0;
        UnregisterNetworkCallback();
        try { TunnelEngine.Stop(); } catch { /* already down */ }
        if (AppState.VpnStarted)
        {
            AppState.MarkStopped();
        }
    }

    private static void Teardown()
    {
        TeardownVpn();
        try { TorSocksBridge.Stop(); } catch { /* already down */ }
        // Blocks until every Tor/lyrebird process is really gone and the ports
        // are released, so a connect right after a stop cannot hit EADDRINUSE.
        try { ParallelTorManager.StopAllAndWait(Config.ProxyPort); } catch { /* already down */ }
        _activeRunner = null;
        DropCpu();
        if (AppState.VpnStarted)
        {
            AppState.MarkStopped();
        }
    }

    /// <summary>
    /// App is going away: teardown blocks until every Tor/lyrebird process has
    /// exited and its ports are released, so it must never run on the caller's
    /// (UI) thread. The Android onDestroy equivalent.
    /// </summary>
    public static void Shutdown()
    {
        _ = Task.Run(() =>
        {
            try { Teardown(); }
            catch (Exception e) { AppLog.W(TAG, $"Shutdown teardown failed: {e.Message}"); }
        });
    }

    // --- Small helpers ---------------------------------------------------------

    private static void Notify(string title, string body, bool progress, int progressValue)
    {
        // Whatever this one says is now what is on screen, so forget what the
        // traffic line last rendered.
        _lastRenderedText = "";
        NotificationChanged?.Invoke(new EngineNotification(title, body, progress, progressValue));
    }

    private static void NotifyRemove()
    {
        _lastRenderedText = "";
        NotificationChanged?.Invoke(null);
    }

    private static CancellationTokenSource ReplaceCts(ref CancellationTokenSource? field)
    {
        var next = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref field, next);
        if (prev != null)
        {
            try { prev.Cancel(); } catch { /* already cancelled */ }
            prev.Dispose();
        }
        return next;
    }

    private static void CancelCts(ref CancellationTokenSource? field)
    {
        var prev = Interlocked.Exchange(ref field, null);
        if (prev == null) return;
        try { prev.Cancel(); } catch { /* already cancelled */ }
        prev.Dispose();
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /** Outstanding HoldCpu calls, so nested windows release only when both end. */
    private static void HoldCpu()
    {
        lock (CpuLock) _cpuHolds++;
    }

    /** Give the CPU back once every window that asked for it has finished. */
    private static void ReleaseCpu()
    {
        lock (CpuLock)
        {
            if (_cpuHolds > 0) _cpuHolds--;
        }
    }

    /** Release the lock whatever asked for it. The shutdown path. */
    private static void DropCpu()
    {
        lock (CpuLock) _cpuHolds = 0;
    }
}

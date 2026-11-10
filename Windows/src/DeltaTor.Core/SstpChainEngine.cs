using DeltaTor.Core.Sstp;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// The Proxy Chain: Tor is still alive underneath its SOCKS bridge, and on top
/// of that a tunneled connection lands on a VPN Gate server over SSTP, so the
/// machine's traffic leaves the far end of the chain:
///
///     apps → [chain TUN] → SSTP/TLS → SOCKS5 → Tor → VPN Gate → internet
///
/// The tunnel itself is the ported kittoku core (<see cref="Controller"/> with
/// a fresh chain adapter), DNS comes from IPCP or a public fallback, and Tor's
/// own outbound links are pinned to the physical route so they never follow
/// the chain's new default route back into the tunnel they sit under.
///
/// Failure policy, in order: reconnect the same server
/// <see cref="RetriesPerHost"/> times, then try the next best pinged server,
/// then restore the plain Tor VPN — done by <c>BridgeRace.OnChainExhausted</c>.
/// </summary>
public sealed class SstpChainEngine
{
    private const string Tag = "Chain";

    /// <summary>VPN Gate's Virtual Hub "VPN" gives these credentials to everybody.</summary>
    private const string VpnGateUser = "vpn";
    private const string VpnGatePass = "vpn";

    /// <summary>The same server gets three consecutive chances before the next one is tried.</summary>
    private const int RetriesPerHost = 3;

    /// <summary>Space between the attempts of one server: a Tor circuit change breathes.</summary>
    private const long RetryDelayMs = 4_000L;

    /// <summary>Next-best servers probed before the chain gives up and hands back to Tor.</summary>
    private const int FallbackServers = 2;

    private readonly Func<TorRunner?> _runner;
    private readonly Action<string, int> _onEstablished;
    private readonly Action<string> _onExhausted;
    private readonly Action _onStoppedByUser;
    private readonly Action _onStateChanged;

    private readonly object _sync = new();

    private Task? _job;
    private CancellationTokenSource? _jobCts;
    private Controller? _controller;

    private bool _running;
    private bool _deliberate;

    /// <summary>
    /// Whose exit the policy loop owes an answer to: the default is a restore
    /// of the plain Tor VPN, switching server restores nothing, and a stop the
    /// service initiated (STOP VPN) leaves the cores where they are silently.
    /// </summary>
    private bool _restoreOnEnd = true;

    private (string Host, int Port)? _pendingSwitch;

    public SstpChainEngine(
        Func<TorRunner?> runner,
        Action<string, int> onEstablished,
        Action<string> onExhausted,
        Action onStoppedByUser,
        Action onStateChanged)
    {
        _runner = runner;
        _onEstablished = onEstablished;
        _onExhausted = onExhausted;
        _onStoppedByUser = onStoppedByUser;
        _onStateChanged = onStateChanged;
    }

    /// <summary>True while the chain owns the VPN interface (or is trying to).</summary>
    public bool IsActive
    {
        get { lock (_sync) return _running; }
    }

    public void Start(string host, int port)
    {
        lock (_sync)
        {
            if (_running)
            {
                AppLog.I(Tag, "chain start ignored: already running");
                return;
            }

            _running = true;
            _deliberate = false;
            _restoreOnEnd = true;
            _pendingSwitch = null;
        }

        SetChain(s => s with
        {
            Status = ChainStatus.Connecting,
            Host = host,
            Port = port,
            Message = "",
            UrlMs = -1,
            Tested = -1,
            TestTotal = -1
        });

        _jobCts = new CancellationTokenSource();
        var token = _jobCts.Token;
        _job = Task.Run(() => RunPolicyGuardedAsync(host, port, token), token);
    }

    public void SwitchTo(string host, int port)
    {
        if (!IsActive)
        {
            Start(host, port);
            return;
        }

        lock (_sync)
        {
            _pendingSwitch = (host, port);
            _restoreOnEnd = false;
            _deliberate = true;
        }

        SetChain(s => s with
        {
            Status = ChainStatus.Stopping,
            Host = host,
            Port = port,
            Message = "switching..."
        });
        _controller?.Disconnect();
    }

    /// <summary>
    /// The service itself is taking the chain down (STOP VPN): no restore is
    /// asked for, because whoever calls knows what the next state should be.
    /// </summary>
    public void StopWithoutRestore()
    {
        if (!IsActive) return;

        lock (_sync)
        {
            _restoreOnEnd = false;
            _deliberate = true;
        }

        SetChain(s => s with { Status = ChainStatus.Stopping });
        _controller?.Disconnect();
    }

    /// <summary>The user pulled the plug: disconnect properly (SSTP Call Disconnect).</summary>
    public void Stop()
    {
        if (!IsActive) return;

        lock (_sync)
        {
            _restoreOnEnd = true;
            _deliberate = true;
        }

        SetChain(s => s with { Status = ChainStatus.Stopping });
        _controller?.Disconnect();
    }

    /// <summary>The whole service is going down; no polite bye message is needed.</summary>
    public void Shutdown()
    {
        lock (_sync)
        {
            _deliberate = true;
        }

        try { _controller?.Kill(null); } catch { /* already gone */ }
        try { _jobCts?.Cancel(); } catch { /* already cancelled */ }
        lock (_sync)
        {
            _running = false;
        }

        AppState.UpdateChain(_ => new ChainState());
    }

    private async Task RunPolicyGuardedAsync(string host, int port, CancellationToken token)
    {
        try
        {
            await RunPolicyAsync(host, port, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown cancelled the loop; nothing to report
        }
        catch (Exception e)
        {
            AppLog.E(Tag, "chain policy crashed", e);
            if (!IsDeliberate)
            {
                _onExhausted(e.Message.Length > 0 ? e.Message : "internal error");
            }
        }
        finally
        {
            bool restore;
            (string Host, int Port)? next;
            lock (_sync)
            {
                restore = _deliberate && _restoreOnEnd && _pendingSwitch is null;
                next = _pendingSwitch;
                _pendingSwitch = null;
                _restoreOnEnd = true;
                _deliberate = false;
                _running = false;
            }

            if (restore)
            {
                _onStoppedByUser();
            }
            else if (next is not null)
            {
                // The old loop has fully ended; the new one starts from here
                // without ever touching the Tor VPN underneath.
                Start(next.Value.Host, next.Value.Port);
            }
            else
            {
                AppState.UpdateChain(_ => new ChainState());
            }
        }
    }

    private bool IsDeliberate
    {
        get { lock (_sync) return _deliberate; }
    }

    private async Task RunPolicyAsync(string host, int port, CancellationToken token)
    {
        var runner = _runner();
        var physical = ChainNetwork.PrepareHostRoutes(runner?.ActiveBridgeLines() ?? Array.Empty<string>());
        if (physical is null)
        {
            AppLog.W(Tag, "no physical IPv4 path; the chain cannot open without it");
            _onExhausted("no physical network path");
            return;
        }

        // Tor itself must keep leaving through the interface it bootstrapped
        // on; lyrebird's bridge endpoints are pinned by the host routes above.
        if (runner is not null && !runner.SetOutboundBindAddress(physical.Address))
        {
            AppLog.W(Tag, "could not pin Tor's outbound address; loop prevention is weaker");
        }

        try
        {
            await RunAttemptsAsync(host, port, token).ConfigureAwait(false);
        }
        finally
        {
            runner?.SetOutboundBindAddress(null);
            ChainNetwork.ReleaseHostRoutes();
            ChainNetwork.FlushDns();
        }
    }

    private async Task RunAttemptsAsync(string host, int port, CancellationToken token)
    {
        var candidates = BuildCandidates(host, port);
        var reason = "no attempt succeeded";

        foreach (var candidate in candidates)
        {
            if (IsDeliberate || token.IsCancellationRequested) return;

            for (var attempt = 1; attempt <= RetriesPerHost; attempt++)
            {
                if (IsDeliberate || token.IsCancellationRequested) return;

                var message = attempt == 1 ? "" : $"attempt {attempt}/{RetriesPerHost}";
                SetChain(s => s with
                {
                    Status = ChainStatus.Connecting,
                    Host = candidate.Host,
                    Port = candidate.Port,
                    Message = message
                });
                AppLog.I(Tag, $"chain attempt {candidate.Host}:{candidate.Port} {message}");

                var outcome = await RunSessionAsync(candidate.Host, candidate.Port, token)
                    .ConfigureAwait(false);

                if (outcome.Negotiated && IsDeliberate)
                {
                    // A stop arrived while the tunnel was already up.
                    return;
                }

                if (outcome.Negotiated)
                {
                    // The tunnel lived, then dropped: count as one used attempt.
                    reason = outcome.Log.Length > 0 ? outcome.Log : "tunnel dropped";
                    AppLog.I(Tag, $"chain drop: {reason}");
                }
                else
                {
                    reason = outcome.Log;
                    AppLog.W(Tag, $"chain fail: {reason}");
                }

                if (IsDeliberate || token.IsCancellationRequested) return;
                await Task.Delay((int)RetryDelayMs, token).ConfigureAwait(false);
            }

            SetChain(s => s with { Message = "next server..." });
        }

        if (IsDeliberate || token.IsCancellationRequested) return;
        _onExhausted(reason);
    }

    /// <summary>
    /// The preferred server first, then up to <see cref="FallbackServers"/>
    /// servers ranked by their last measured delay.
    /// </summary>
    private static List<(string Host, int Port)> BuildCandidates(string host, int port)
    {
        var list = new List<(string Host, int Port)> { (host, port) };
        if (Config.ChainAutoFallback)
        {
            foreach (var server in ProxyChainManager.BestServers($"{host}:{port}", FallbackServers))
            {
                list.Add((server.Host, server.Port));
            }
        }

        return list;
    }

    private sealed record Outcome(bool Negotiated, string Log);

    /// <summary>
    /// One full session lifecycle. Returns once the negotiation either
    /// succeeded and the tunnel later ended, or failed never having carried a
    /// packet.
    /// </summary>
    private async Task<Outcome> RunSessionAsync(string host, int port, CancellationToken token)
    {
        var negotiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastLog = "";

        var builder = SstpTun.Factory?.Invoke();
        if (builder is null)
        {
            return new Outcome(false, "chain tunnel adapter is not available");
        }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var parameters = new SstpParams
        {
            Mode = SstpMode.Run,
            Hostname = host,
            Port = port,
            ProxyHostname = "127.0.0.1",
            ProxyPort = Config.ProxyPort,
            Username = VpnGateUser,
            Password = VpnGatePass,
            DoVerifyCert = Config.ChainVerifyTlsCert
        };

        var bridge = new SharedBridge(
            parameters,
            sessionCts.Token,
            builder,
            protect: _ => true,
            log: line =>
            {
                lastLog = line;
                AppLog.I(Tag, $"[{host}:{port}] {line}");
            },
            onSessionEnded: () => ended.TrySetResult(lastLog),
            onNegotiated: () => negotiated.TrySetResult());

        var controller = new Controller(bridge);
        controller.LaunchJobMain();
        _controller = controller;

        try
        {
            // Whichever comes first: a working tunnel or the session's death.
            var first = await Task.WhenAny(negotiated.Task, ended.Task)
                .WaitAsync(token)
                .ConfigureAwait(false);

            if (first != negotiated.Task)
            {
                var log = ended.Task.IsCompletedSuccessfully ? ended.Task.Result : "";
                return new Outcome(false, log.Length > 0 ? log : $"no answer from {host}:{port}");
            }

            SetChain(s => s with
            {
                Status = ChainStatus.Connected,
                Host = host,
                Port = port,
                Message = ""
            });
            _onEstablished(host, port);

            // Hold until the tunnel drops for any reason (echo timeout, abort,
            // network loss), then report back to the retry policy.
            var dropLog = await ended.Task.WaitAsync(token).ConfigureAwait(false);
            return new Outcome(true, dropLog);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(false, "cancelled");
        }
        finally
        {
            _controller = null;
            try { sessionCts.Cancel(); } catch { /* already gone */ }
        }
    }

    private void SetChain(Func<ChainState, ChainState> block)
    {
        AppState.UpdateChain(block);
        _onStateChanged();
    }
}
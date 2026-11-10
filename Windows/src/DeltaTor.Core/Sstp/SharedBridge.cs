using System.Net.Sockets;
using System.Threading.Channels;

namespace DeltaTor.Core.Sstp;

/// <summary>Channel helpers bridging Kotlin's coroutine channels to
/// <see cref="System.Threading.Channels"/>.</summary>
internal static class SstpChannels
{
    /// <summary>withTimeoutOrNull(ms) { receive() } — null on timeout.</summary>
    public static async Task<T?> ReceiveWithTimeoutAsync<T>(
        this ChannelReader<T> reader, int timeoutMs, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
    }

    /// <summary>withTimeoutOrNull(ms) { receive() } for a long timeout.</summary>
    public static Task<T?> ReceiveWithTimeoutAsync<T>(
        this ChannelReader<T> reader, long timeoutMs, CancellationToken token) =>
        ReceiveWithTimeoutAsync(reader, (int)timeoutMs, token);
}

/// <summary>
/// The Windows counterpart of <c>kittoku.osc.SharedBridge</c>: the shared state
/// every SSTP/PPP client of one session reads and writes, plus the pieces the
/// original borrowed from the Android service (the VpnService.Builder, the
/// protect() call, the log sink and the session callbacks).
/// </summary>
internal sealed class SharedBridge
{
    public SharedBridge(
        SstpParams parameters,
        CancellationToken token,
        ISstpTun? builder,
        Func<Socket?, bool> protect,
        Action<string> log,
        Action onSessionEnded,
        Action onNegotiated)
    {
        Params = parameters;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = _cts.Token;
        Builder = builder;
        Protect = protect;
        Log = log;
        OnSessionEnded = onSessionEnded;
        OnNegotiated = onNegotiated;

        ControlMailbox = Channel.CreateUnbounded<ControlMessage>();
        CurrentMru = parameters.Mru;
    }

    private readonly CancellationTokenSource _cts;

    /// <summary>Stop every job this bridge launched (Controller.kill).</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }
    }

    public SstpParams Params { get; }

    public CancellationToken Token { get; }

    /// <summary>VpnService.Builder of the owning service, null in <see cref="SstpMode.Probe"/>.</summary>
    public ISstpTun? Builder { get; }

    /// <summary>Exempts a socket from the owning tunnel.</summary>
    public Func<Socket?, bool> Protect { get; }

    /// <summary>Line for every notable event and error of the session.</summary>
    public Action<string> Log { get; }

    /// <summary>Called once when the session is torn down by whatever reason.</summary>
    public Action OnSessionEnded { get; }

    /// <summary>Called once when the negotiation reached its goal: a live tunnel
    /// carrying packets in <see cref="SstpMode.Run"/>, an answered IPCP in
    /// <see cref="SstpMode.Probe"/>.</summary>
    public Action OnNegotiated { get; }

    /// <summary>Exception hook installed by the Controller (CoroutineExceptionHandler).</summary>
    public Action<Exception>? ExceptionHandler { get; set; }

    public Channel<ControlMessage> ControlMailbox { get; }

    public SslTerminal? Ssl { get; set; }
    public IpTerminal? Ip { get; set; }

    public string HomeUsername => Params.Username;
    public string HomePassword => Params.Password;
    public int PppMru => Params.Mru;
    public int PppMtu => Params.Mtu;
    public IReadOnlySet<string> PppAuthProtocols => Params.AuthProtocols;
    public bool PppIPv4Enabled => Params.PppIPv4Enabled;
    public bool PppIPv6Enabled => Params.PppIPv6Enabled;

    public byte[]? Hlak { get; set; }
    public byte[] Nonce { get; } = new byte[32];
    public string Guid { get; } = System.Guid.NewGuid().ToString();
    public byte HashProtocol { get; set; }

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private int _frameId = -1;

    public int CurrentMru { get; set; }
    public string CurrentAuth { get; set; } = "";
    public byte[] CurrentIPv4 { get; } = new byte[4];
    public byte[] CurrentIPv6 { get; } = new byte[8];
    public byte[] CurrentProposedDns { get; } = new byte[4];

    public bool IsEnabled(string authProtocol) => PppAuthProtocols.Contains(authProtocol);

    public void SendControlMessage(ControlMessage message) => ControlMailbox.Writer.TryWrite(message);

    public void AttachSslTerminal() => Ssl = new SslTerminal(this);

    public void AttachIpTerminal() => Ip = new IpTerminal(this);

    public async Task<byte> AllocateNewFrameIdAsync()
    {
        await _mutex.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            _frameId += 1;
            return (byte)_frameId;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>bridge.scope.launch(bridge.handler): run a detached session task
    /// that reports unexpected failures to the Controller's handler.</summary>
    public void Launch(Func<Task> body)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // scope cancelled: expected during teardown
            }
            catch (ChannelClosedException)
            {
                // mailbox closed by Cancel(): also an orderly teardown
            }
            catch (Exception ex)
            {
                ExceptionHandler?.Invoke(ex);
            }
        }, Token);
    }
}

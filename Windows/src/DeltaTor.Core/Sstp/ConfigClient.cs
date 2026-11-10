using System.Threading.Channels;

namespace DeltaTor.Core.Sstp;

/// <summary>
/// Windows counterpart of <c>kittoku.osc.client.ppp.ConfigClient</c>: the shared
/// LCP/IPCP/IPv6CP negotiation loop (client CONFIGURE-REQUEST retries against the
/// server's own requests), parameterised by the per-protocol hooks.
/// </summary>
internal abstract class ConfigClient<T> where T : PppFrame
{
    private const int PppRequestInterval = 3_000;
    private const int PppRequestCount = 10;
    public const int PppNegotiationTimeout = Configuration.PppNegotiationTimeout;

    private readonly Where _where;
    protected readonly SharedBridge Bridge;

    public Channel<T> Mailbox { get; } = Channel.CreateUnbounded<T>();

    private byte _requestId;
    private int _requestCount = PppRequestCount;

    private bool _isClientReady;
    private bool _isServerReady;

    private bool IsOpen => _isClientReady && _isServerReady;

    protected ConfigClient(Where where, SharedBridge bridge)
    {
        _where = where;
        Bridge = bridge;
    }

    protected abstract T? TryCreateServerReject(T request);

    protected abstract T? TryCreateServerNak(T request);

    protected abstract T CreateServerAck(T request);

    protected abstract T CreateClientRequest();

    protected abstract Task TryAcceptClientReject(T reject);

    protected abstract Task TryAcceptClientNak(T nak);

    private async Task ConsumeRequestCounterAsync()
    {
        _requestCount--;

        if (_requestCount < 0)
        {
            Bridge.SendControlMessage(new ControlMessage(_where, Result.ERR_COUNT_EXHAUSTED));
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task SendClientRequestAsync()
    {
        await ConsumeRequestCounterAsync().ConfigureAwait(false);
        _requestId = await Bridge.AllocateNewFrameIdAsync().ConfigureAwait(false);

        var request = CreateClientRequest();
        request.Id = _requestId;
        await Bridge.Ssl!.SendAsync(request.ToByteBuffer()).ConfigureAwait(false);
    }

    public void LaunchJobNegotiation()
    {
        Bridge.Launch(async () =>
        {
            await SendClientRequestAsync().ConfigureAwait(false);

            while (!Bridge.Token.IsCancellationRequested)
            {
                var tried = await Mailbox.Reader
                    .ReceiveWithTimeoutAsync(PppRequestInterval, Bridge.Token).ConfigureAwait(false);

                T received;
                if (tried is null)
                {
                    _isClientReady = false;

                    await SendClientRequestAsync().ConfigureAwait(false);

                    continue;
                }

                received = tried;

                if (received.Code == Ppp.LcpCodeConfigureRequest)
                {
                    _isServerReady = false;

                    var reject = TryCreateServerReject(received);
                    if (reject is not null)
                    {
                        await Bridge.Ssl!.SendAsync(reject.ToByteBuffer()).ConfigureAwait(false);
                        continue;
                    }

                    var nak = TryCreateServerNak(received);
                    if (nak is not null)
                    {
                        await Bridge.Ssl!.SendAsync(nak.ToByteBuffer()).ConfigureAwait(false);
                        continue;
                    }

                    var ack = CreateServerAck(received);
                    await Bridge.Ssl!.SendAsync(ack.ToByteBuffer()).ConfigureAwait(false);
                    _isServerReady = true;
                }
                else
                {
                    if (_isClientReady)
                    {
                        _isClientReady = false;

                        await SendClientRequestAsync().ConfigureAwait(false);

                        continue;
                    }

                    if (received.Id != _requestId)
                    {
                        continue;
                    }

                    switch (received.Code)
                    {
                        case Ppp.LcpCodeConfigureAck:
                            _isClientReady = true;
                            break;

                        case Ppp.LcpCodeConfigureNak:
                            await TryAcceptClientNak(received).ConfigureAwait(false);
                            await SendClientRequestAsync().ConfigureAwait(false);
                            break;

                        case Ppp.LcpCodeConfigureReject:
                            await TryAcceptClientReject(received).ConfigureAwait(false);
                            await SendClientRequestAsync().ConfigureAwait(false);
                            break;
                    }
                }

                if (IsOpen)
                {
                    _requestCount = PppRequestCount;
                    Bridge.SendControlMessage(new ControlMessage(_where, Result.PROCEEDED));

                    break;
                }
            }
        });
    }

    public void Cancel() => Mailbox.Writer.TryComplete();
}

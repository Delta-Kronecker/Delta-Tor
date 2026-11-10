using System.Text;
using System.Threading.Channels;

namespace DeltaTor.Core.Sstp;

/// <summary>kittoku.osc.client.SstpClient — the SSTP control channel.</summary>
internal sealed class SstpClient
{
    private const int SstpRequestInterval = 60_000;
    private const int SstpRequestCount = 3;
    public const int SstpRequestTimeout = SstpRequestInterval * SstpRequestCount;

    private readonly SharedBridge _bridge;

    public Channel<ControlPacket> Mailbox { get; } = Channel.CreateUnbounded<ControlPacket>();

    public SstpClient(SharedBridge bridge) => _bridge = bridge;

    private sealed class HashSetting
    {
        public HashSetting(byte hashProtocol)
        {
            switch (hashProtocol)
            {
                case SstpPackets.CertHashProtocolSha1:
                    CmacSize = 0x1400;
                    DigestProtocol = "SHA-1";
                    MacProtocol = "HmacSHA1";
                    break;

                case SstpPackets.CertHashProtocolSha256:
                    CmacSize = 0x2000;
                    DigestProtocol = "SHA-256";
                    MacProtocol = "HmacSHA256";
                    break;

                default:
                    throw new NotSupportedException(hashProtocol.ToString());
            }
        }

        public short CmacSize { get; }
        public string DigestProtocol { get; }
        public string MacProtocol { get; }
    }

    public void LaunchJobControl()
    {
        _bridge.Launch(async () =>
        {
            while (!_bridge.Token.IsCancellationRequested)
            {
                var received = await Mailbox.Reader.ReadAsync(_bridge.Token).ConfigureAwait(false);

                switch (received)
                {
                    case SstpEchoRequest:
                        await _bridge.Ssl!.SendAsync(new SstpEchoResponse().ToByteBuffer()).ConfigureAwait(false);
                        break;

                    case SstpEchoResponse:
                        break;

                    case SstpCallDisconnect:
                        _bridge.SendControlMessage(
                            new ControlMessage(Where.SSTP_CONTROL, Result.ERR_DISCONNECT_REQUESTED));
                        break;

                    case SstpCallAbort:
                        _bridge.SendControlMessage(
                            new ControlMessage(Where.SSTP_CONTROL, Result.ERR_ABORT_REQUESTED));
                        break;

                    default:
                        _bridge.SendControlMessage(
                            new ControlMessage(Where.SSTP_CONTROL, Result.ERR_UNEXPECTED_MESSAGE));
                        break;
                }
            }
        });
    }

    public void LaunchJobRequest()
    {
        _bridge.Launch(async () =>
        {
            var request = new SstpCallConnectRequest();
            var requestCount = SstpRequestCount;

            ControlPacket received;
            while (true)
            {
                requestCount--;
                if (requestCount < 0)
                {
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.ERR_COUNT_EXHAUSTED));

                    return;
                }

                await _bridge.Ssl!.SendAsync(request.ToByteBuffer()).ConfigureAwait(false);

                var tried = await Mailbox.Reader
                    .ReceiveWithTimeoutAsync(SstpRequestInterval, _bridge.Token).ConfigureAwait(false);

                if (tried is null)
                {
                    continue;
                }

                received = tried;
                break;
            }

            switch (received)
            {
                case SstpCallConnectAck ack:
                    _bridge.HashProtocol = (ack.Request.Bitmask) switch
                    {
                        >= 2 and <= 3 => SstpPackets.CertHashProtocolSha256,
                        1 => SstpPackets.CertHashProtocolSha1,
                        _ => 0
                    };

                    if (_bridge.HashProtocol == 0)
                    {
                        _bridge.SendControlMessage(new ControlMessage(Where.SSTP_HASH, Result.ERR_UNKNOWN_TYPE));

                        return;
                    }

                    ack.Request.Nonce.CopyTo(_bridge.Nonce, 0);

                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.PROCEEDED));
                    break;

                case SstpCallConnectNak:
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.ERR_NEGATIVE_ACKNOWLEDGED));
                    break;

                case SstpCallDisconnect:
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.ERR_DISCONNECT_REQUESTED));
                    break;

                case SstpCallAbort:
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.ERR_ABORT_REQUESTED));
                    break;

                default:
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_REQUEST, Result.ERR_UNEXPECTED_MESSAGE));
                    break;
            }
        });
    }

    public async Task SendCallConnectedAsync()
    {
        var call = new SstpCallConnected();

        var cmkInputBuffer = ByteBuffer.Allocate(32);
        var cmacInputBuffer = ByteBuffer.Allocate(call.Length);
        var hashSetting = new HashSetting(_bridge.HashProtocol);

        _bridge.Nonce.CopyTo(call.Binding.Nonce, 0);
        Crypto.Digest(hashSetting.DigestProtocol, _bridge.Ssl!.GetServerCertificate())
            .CopyTo(call.Binding.CertHash, 0);

        call.Binding.HashProtocol = _bridge.HashProtocol;
        call.Write(cmacInputBuffer);

        var hlak = _bridge.CurrentAuth switch
        {
            AuthProtocol.Pap => new byte[32],
            AuthProtocol.MsChapV2 or AuthProtocol.EapMsChapV2 => _bridge.Hlak!,
            _ => throw new NotSupportedException(_bridge.CurrentAuth)
        };

        var cmkSeed = Encoding.ASCII.GetBytes("SSTP inner method derived CMK");
        cmkInputBuffer.Put(cmkSeed);
        cmkInputBuffer.PutShort(hashSetting.CmacSize);
        cmkInputBuffer.Put(1);

        var cmk = Crypto.Hmac(hlak, hashSetting.MacProtocol, cmkInputBuffer.Array);
        var cmac = Crypto.Hmac(cmk, hashSetting.MacProtocol, cmacInputBuffer.Array);
        cmac.CopyTo(call.Binding.CompoundMac, 0);

        await _bridge.Ssl!.SendAsync(call.ToByteBuffer()).ConfigureAwait(false);
    }

    public async Task SendLastPacketAsync(short type)
    {
        ControlPacket packet = type switch
        {
            SstpPackets.MessageCallDisconnect => new SstpCallDisconnect(),
            SstpPackets.MessageCallDisconnectAck => new SstpCallDisconnectAck(),
            SstpPackets.MessageCallAbort => new SstpCallAbort(),
            _ => throw new NotSupportedException(type.ToString())
        };

        try
        {
            // maybe the socket is no longer available
            await _bridge.Ssl!.SendAsync(packet.ToByteBuffer()).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    public void Cancel() => Mailbox.Writer.TryComplete();
}

/// <summary>kittoku.osc.client.ppp.PPPClient — the LCP housekeeping channel.</summary>
internal sealed class PppClient
{
    private readonly SharedBridge _bridge;

    public Channel<PppFrame> Mailbox { get; } = Channel.CreateUnbounded<PppFrame>();

    public PppClient(SharedBridge bridge) => _bridge = bridge;

    public void LaunchJobControl()
    {
        _bridge.Launch(async () =>
        {
            while (!_bridge.Token.IsCancellationRequested)
            {
                var received = await Mailbox.Reader.ReadAsync(_bridge.Token).ConfigureAwait(false);

                switch (received)
                {
                    case LcpEchoRequest echo:
                        var reply = new LcpEchoReply
                        {
                            Id = echo.Id,
                            Holder = Encoding.ASCII.GetBytes("Abura Mashi Mashi")
                        };
                        await _bridge.Ssl!.SendAsync(reply.ToByteBuffer()).ConfigureAwait(false);
                        break;

                    case LcpEchoReply:
                    case LcpDiscardRequest:
                        break;

                    case LcpTerminalRequest terminal:
                        var ack = new LcpTerminalAck { Id = terminal.Id };
                        await _bridge.Ssl!.SendAsync(ack.ToByteBuffer()).ConfigureAwait(false);

                        _bridge.SendControlMessage(new ControlMessage(Where.PPP, Result.ERR_TERMINATE_REQUESTED));
                        break;

                    case LcpProtocolReject:
                        _bridge.SendControlMessage(new ControlMessage(Where.PPP, Result.ERR_PROTOCOL_REJECTED));
                        break;

                    case LcpCodeReject:
                        _bridge.SendControlMessage(new ControlMessage(Where.PPP, Result.ERR_CODE_REJECTED));
                        break;

                    default:
                        _bridge.SendControlMessage(new ControlMessage(Where.PPP, Result.ERR_UNEXPECTED_MESSAGE));
                        break;
                }
            }
        });
    }

    public void Cancel() => Mailbox.Writer.TryComplete();
}

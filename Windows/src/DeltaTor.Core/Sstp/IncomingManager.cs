using System.Text;
using System.Threading.Channels;

namespace DeltaTor.Core.Sstp;

/// <summary>kittoku.osc.io.incoming.EchoTimer.</summary>
internal sealed class EchoTimer
{
    private readonly long _interval;
    private readonly Func<Task> _echoFunction;

    private long _lastTicked;
    private long _deadline;
    private bool _isEchoWaited;

    public EchoTimer(long interval, Func<Task> echoFunction)
    {
        _interval = interval;
        _echoFunction = echoFunction;
    }

    private bool IsOutOfTime => NowMs() - _lastTicked > _interval;

    private bool IsDead => NowMs() > _deadline;

    public async Task<bool> CheckAliveAsync()
    {
        if (IsOutOfTime)
        {
            if (_isEchoWaited)
            {
                if (IsDead)
                {
                    return false;
                }
            }
            else
            {
                await _echoFunction().ConfigureAwait(false);
                _isEchoWaited = true;
                _deadline = NowMs() + _interval;
            }
        }

        return true;
    }

    public void Tick()
    {
        _lastTicked = NowMs();
        _isEchoWaited = false;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// kittoku.osc.io.incoming.IncomingManager + process.kt: demultiplexes the SSTP
/// stream coming off the TLS socket into the per-protocol mailboxes, and writes
/// IP packets out to the chain adapter.
/// </summary>
internal sealed class IncomingManager
{
    private const long SstpEchoInterval = 20_000;
    private const long PppEchoInterval = 20_000;

    private readonly SharedBridge _bridge;
    private readonly int _bufferSize;

    private Channel<LcpConfigureFrame>? _lcpMailbox;
    private Channel<PapFrame>? _papMailbox;
    private Channel<ChapFrame>? _chapMailbox;
    private Channel<EapFrame>? _eapMailbox;
    private Channel<IpcpConfigureFrame>? _ipcpMailbox;
    private Channel<Ipv6cpConfigureFrame>? _ipv6cpMailbox;
    private Channel<PppFrame>? _pppMailbox;
    private Channel<ControlPacket>? _sstpMailbox;

    private readonly EchoTimer _sstpTimer;
    private readonly EchoTimer _pppTimer;

    public IncomingManager(SharedBridge bridge)
    {
        _bridge = bridge;
        _bufferSize = bridge.Ssl!.ApplicationBuffer + Configuration.MaxMru + 8;

        _sstpTimer = new EchoTimer(SstpEchoInterval, async () =>
            await bridge.Ssl!.SendAsync(new SstpEchoRequest().ToByteBuffer()).ConfigureAwait(false));

        _pppTimer = new EchoTimer(PppEchoInterval, async () =>
        {
            var request = new LcpEchoRequest
            {
                Id = await bridge.AllocateNewFrameIdAsync().ConfigureAwait(false),
                Holder = Encoding.ASCII.GetBytes("Abura Mashi Mashi")
            };
            await bridge.Ssl!.SendAsync(request.ToByteBuffer()).ConfigureAwait(false);
        });
    }

    public void RegisterMailbox(object client)
    {
        switch (client)
        {
            case LcpClient c: _lcpMailbox = c.Mailbox; break;
            case PapClient c: _papMailbox = c.Mailbox; break;
            case ChapClient c: _chapMailbox = c.Mailbox; break;
            case EapClient c: _eapMailbox = c.Mailbox; break;
            case IpcpClient c: _ipcpMailbox = c.Mailbox; break;
            case Ipv6cpClient c: _ipv6cpMailbox = c.Mailbox; break;
            case PppClient c: _pppMailbox = c.Mailbox; break;
            case SstpClient c: _sstpMailbox = c.Mailbox; break;
            default: throw new NotSupportedException(client?.ToString() ?? "");
        }
    }

    public void UnregisterMailbox(object client)
    {
        switch (client)
        {
            case LcpClient: _lcpMailbox = null; break;
            case PapClient: _papMailbox = null; break;
            case ChapClient: _chapMailbox = null; break;
            case EapClient: _eapMailbox = null; break;
            case IpcpClient: _ipcpMailbox = null; break;
            case Ipv6cpClient: _ipv6cpMailbox = null; break;
            case PppClient: _pppMailbox = null; break;
            case SstpClient: _sstpMailbox = null; break;
            default: throw new NotSupportedException(client?.ToString() ?? "");
        }
    }

    public void LaunchJobMain()
    {
        _bridge.Launch(async () =>
        {
            var buffer = ByteBuffer.Allocate(_bufferSize);
            buffer.Limit = 0;

            _sstpTimer.Tick();
            _pppTimer.Tick();

            while (!_bridge.Token.IsCancellationRequested)
            {
                if (!await _sstpTimer.CheckAliveAsync().ConfigureAwait(false))
                {
                    _bridge.SendControlMessage(new ControlMessage(Where.SSTP_CONTROL, Result.ERR_TIMEOUT));
                    return;
                }

                if (!await _pppTimer.CheckAliveAsync().ConfigureAwait(false))
                {
                    _bridge.SendControlMessage(new ControlMessage(Where.PPP, Result.ERR_TIMEOUT));
                    return;
                }

                var size = GetPacketSize(buffer);
                if (size >= 4 && size <= _bufferSize)
                {
                    // complete packet already buffered
                }
                else if (size == -1)
                {
                    _bridge.Ssl!.Receive(buffer);
                    continue;
                }
                else
                {
                    _bridge.SendControlMessage(new ControlMessage(Where.INCOMING, Result.ERR_INVALID_PACKET_SIZE));
                    return;
                }

                if (size > buffer.Remaining)
                {
                    _bridge.Ssl!.Receive(buffer);
                    continue;
                }

                _sstpTimer.Tick();

                switch (buffer.ProbeShort(0))
                {
                    case SstpPackets.PacketTypeData:
                        if (buffer.ProbeShort(4) != Ppp.HdlcHeader)
                        {
                            _bridge.SendControlMessage(new ControlMessage(Where.SSTP_DATA, Result.ERR_UNKNOWN_TYPE));
                            return;
                        }

                        _pppTimer.Tick();

                        var protocol = buffer.ProbeShort(6);

                        // DATA
                        if (protocol == Ppp.ProtocolIp)
                        {
                            ProcessIpPacket(_bridge.PppIPv4Enabled, size, buffer);
                            continue;
                        }

                        if (protocol == Ppp.ProtocolIpv6)
                        {
                            ProcessIpPacket(_bridge.PppIPv6Enabled, size, buffer);
                            continue;
                        }

                        // CONTROL
                        var code = buffer.ProbeByte(8);
                        var isGo = protocol switch
                        {
                            Ppp.ProtocolLcp => await ProcessLcpFrameAsync(code, buffer).ConfigureAwait(false),
                            Ppp.ProtocolPap => await ProcessPapFrameAsync(code, buffer).ConfigureAwait(false),
                            Ppp.ProtocolChap => await ProcessChapFrameAsync(code, buffer).ConfigureAwait(false),
                            Ppp.ProtocolEap => await ProcessEapFrameAsync(code, buffer).ConfigureAwait(false),
                            Ppp.ProtocolIpcp => await ProcessIpcpFrameAsync(code, buffer).ConfigureAwait(false),
                            Ppp.ProtocolIpv6cp => await ProcessIpv6cpFrameAsync(code, buffer).ConfigureAwait(false),
                            _ => await ProcessUnknownProtocolAsync(protocol, size, buffer).ConfigureAwait(false)
                        };

                        if (!isGo)
                        {
                            return;
                        }

                        break;

                    case SstpPackets.PacketTypeControl:
                        if (!await ProcessControlPacketAsync(buffer.ProbeShort(4), buffer).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;

                    default:
                        _bridge.SendControlMessage(new ControlMessage(Where.INCOMING, Result.ERR_UNKNOWN_TYPE));
                        return;
                }
            }
        });
    }

    private static int GetPacketSize(ByteBuffer buffer) =>
        buffer.Remaining < 4 ? -1 : buffer.ProbeShort(2).ToIntAsUShort();

    private async Task<bool> TryReadDataUnitAsync(DataUnit unit, ByteBuffer buffer)
    {
        try
        {
            unit.Read(buffer);
        }
        catch (ParsingDataUnitException)
        {
            // need to save packet log
            _bridge.SendControlMessage(new ControlMessage(Where.INCOMING, Result.ERR_PARSING_FAILED));
            return true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private async Task<bool> ProcessControlPacketAsync(short type, ByteBuffer buffer)
    {
        ControlPacket? packet = type switch
        {
            SstpPackets.MessageCallConnectRequest => new SstpCallConnectRequest(),
            SstpPackets.MessageCallConnectAck => new SstpCallConnectAck(),
            SstpPackets.MessageCallConnectNak => new SstpCallConnectNak(),
            SstpPackets.MessageCallConnected => new SstpCallConnected(),
            SstpPackets.MessageCallAbort => new SstpCallAbort(),
            SstpPackets.MessageCallDisconnect => new SstpCallDisconnect(),
            SstpPackets.MessageCallDisconnectAck => new SstpCallDisconnectAck(),
            SstpPackets.MessageEchoRequest => new SstpEchoRequest(),
            SstpPackets.MessageEchoResponse => new SstpEchoResponse(),
            _ => null
        };

        if (packet is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.SSTP_CONTROL, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(packet, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _sstpMailbox?.Writer.TryWrite(packet);
        return true;
    }

    private async Task<bool> ProcessLcpFrameAsync(byte code, ByteBuffer buffer)
    {
        if (code is >= 1 and <= 4)
        {
            LcpConfigureFrame frame = code switch
            {
                Ppp.LcpCodeConfigureRequest => new LcpConfigureRequest(),
                Ppp.LcpCodeConfigureAck => new LcpConfigureAck(),
                Ppp.LcpCodeConfigureNak => new LcpConfigureNak(),
                Ppp.LcpCodeConfigureReject => new LcpConfigureReject(),
                _ => throw new NotSupportedException(code.ToString())
            };

            if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
            {
                return false;
            }

            _lcpMailbox?.Writer.TryWrite(frame);
            return true;
        }

        if (code is >= 5 and <= 11)
        {
            PppFrame frame = code switch
            {
                Ppp.LcpCodeTerminateRequest => new LcpTerminalRequest(),
                Ppp.LcpCodeTerminateAck => new LcpTerminalAck(),
                Ppp.LcpCodeCodeReject => new LcpCodeReject(),
                Ppp.LcpCodeProtocolReject => new LcpProtocolReject(),
                Ppp.LcpCodeEchoRequest => new LcpEchoRequest(),
                Ppp.LcpCodeEchoReply => new LcpEchoReply(),
                Ppp.LcpCodeDiscardRequest => new LcpDiscardRequest(),
                _ => throw new NotSupportedException(code.ToString())
            };

            if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
            {
                return false;
            }

            _pppMailbox?.Writer.TryWrite(frame);
            return true;
        }

        _bridge.SendControlMessage(new ControlMessage(Where.LCP, Result.ERR_UNKNOWN_TYPE));

        return false;
    }

    private async Task<bool> ProcessPapFrameAsync(byte code, ByteBuffer buffer)
    {
        PapFrame? frame = code switch
        {
            AuthCodes.PapAuthenticateRequest => new PapAuthenticateRequest(),
            AuthCodes.PapAuthenticateAck => new PapAuthenticateAck(),
            AuthCodes.PapAuthenticateNak => new PapAuthenticateNak(),
            _ => null
        };

        if (frame is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.PAP, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _papMailbox?.Writer.TryWrite(frame);
        return true;
    }

    private async Task<bool> ProcessChapFrameAsync(byte code, ByteBuffer buffer)
    {
        ChapFrame? frame = code switch
        {
            AuthCodes.ChapChallenge => new ChapChallenge(),
            AuthCodes.ChapResponse => new ChapResponse(),
            AuthCodes.ChapSuccess => new ChapSuccess(),
            AuthCodes.ChapFailure => new ChapFailure(),
            _ => null
        };

        if (frame is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.CHAP, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _chapMailbox?.Writer.TryWrite(frame);
        return true;
    }

    private async Task<bool> ProcessEapFrameAsync(byte code, ByteBuffer buffer)
    {
        EapFrame? frame = code switch
        {
            AuthCodes.EapRequest => new EapRequest(),
            AuthCodes.EapResponse => new EapResponse(),
            AuthCodes.EapSuccess => new EapSuccess(),
            AuthCodes.EapFailure => new EapFailure(),
            _ => null
        };

        if (frame is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.EAP, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _eapMailbox?.Writer.TryWrite(frame);
        return true;
    }

    private async Task<bool> ProcessIpcpFrameAsync(byte code, ByteBuffer buffer)
    {
        IpcpConfigureFrame? frame = code switch
        {
            Ppp.LcpCodeConfigureRequest => new IpcpConfigureRequest(),
            Ppp.LcpCodeConfigureAck => new IpcpConfigureAck(),
            Ppp.LcpCodeConfigureNak => new IpcpConfigureNak(),
            Ppp.LcpCodeConfigureReject => new IpcpConfigureReject(),
            _ => null
        };

        if (frame is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.IPCP, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _ipcpMailbox?.Writer.TryWrite(frame);
        return true;
    }

    private async Task<bool> ProcessIpv6cpFrameAsync(byte code, ByteBuffer buffer)
    {
        Ipv6cpConfigureFrame? frame = code switch
        {
            Ppp.LcpCodeConfigureRequest => new Ipv6cpConfigureRequest(),
            Ppp.LcpCodeConfigureAck => new Ipv6cpConfigureAck(),
            Ppp.LcpCodeConfigureNak => new Ipv6cpConfigureNak(),
            Ppp.LcpCodeConfigureReject => new Ipv6cpConfigureReject(),
            _ => null
        };

        if (frame is null)
        {
            _bridge.SendControlMessage(new ControlMessage(Where.IPV6CP, Result.ERR_UNKNOWN_TYPE));
            return false;
        }

        if (await TryReadDataUnitAsync(frame, buffer).ConfigureAwait(false))
        {
            return false;
        }

        _ipv6cpMailbox?.Writer.TryWrite(frame);
        return true;
    }

    private async Task<bool> ProcessUnknownProtocolAsync(short protocol, int packetSize, ByteBuffer buffer)
    {
        var reject = new LcpProtocolReject
        {
            RejectedProtocol = protocol,
            Id = await _bridge.AllocateNewFrameIdAsync().ConfigureAwait(false)
        };

        var infoStart = buffer.Position + 8;
        var infoStop = buffer.Position + packetSize;
        reject.Holder = buffer.Array[infoStart..infoStop];

        await _bridge.Ssl!.SendAsync(reject.ToByteBuffer()).ConfigureAwait(false);

        buffer.Move(packetSize);

        return true;
    }

    private void ProcessIpPacket(bool isEnabledProtocol, int packetSize, ByteBuffer buffer)
    {
        if (isEnabledProtocol)
        {
            var start = buffer.Position + 8;
            var ipPacketSize = packetSize - 8;

            _bridge.Ip!.WritePacket(start, ipPacketSize, buffer);
        }

        buffer.Move(packetSize);
    }

    public void Cancel()
    {
        // jobMain is bound to the bridge token; Controller.kill cancels it.
    }
}

/// <summary>kittoku.osc.io.OutgoingManager: pulls IP packets off the chain
/// adapter and frames them onto the SSTP data stream, coalescing whatever is
/// available into one TLS record.</summary>
internal sealed class OutgoingManager
{
    private const int PrefixSize = 8;

    private const int Ipv4VersionHeader = unchecked((int)0x40000000);
    private const int Ipv6VersionHeader = unchecked((int)0x60000000);
    private const int IpVersionMask = unchecked((int)0xF0000000);

    private readonly SharedBridge _bridge;
    private readonly ByteBuffer _mainBuffer;
    private readonly Channel<ByteBuffer> _channel;

    public OutgoingManager(SharedBridge bridge)
    {
        _bridge = bridge;
        _mainBuffer = ByteBuffer.Allocate(bridge.Ssl!.ApplicationBuffer);
        _channel = Channel.CreateBounded<ByteBuffer>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public void LaunchJobMain()
    {
        _bridge.Launch(async () =>
        {
            LaunchJobRetrieve();

            var minCapacity = PrefixSize + _bridge.PppMtu;

            while (!_bridge.Token.IsCancellationRequested)
            {
                _mainBuffer.Clear();

                if (!Load(await _channel.Reader.ReadAsync(_bridge.Token).ConfigureAwait(false)))
                {
                    continue;
                }

                while (!_bridge.Token.IsCancellationRequested)
                {
                    if (_channel.Reader.TryRead(out var extra))
                    {
                        Load(extra);
                    }
                    else
                    {
                        break;
                    }

                    if (_mainBuffer.Remaining < minCapacity)
                    {
                        break;
                    }
                }

                _mainBuffer.Flip();
                await _bridge.Ssl!.SendAsync(_mainBuffer).ConfigureAwait(false);
            }
        });
    }

    private void LaunchJobRetrieve()
    {
        _bridge.Launch(async () =>
        {
            var bufferAlpha = ByteBuffer.Allocate(_bridge.PppMtu);
            var bufferBeta = ByteBuffer.Allocate(_bridge.PppMtu);
            var isBlockingAlpha = true;

            while (!_bridge.Token.IsCancellationRequested)
            {
                if (isBlockingAlpha)
                {
                    _bridge.Ip!.ReadPacket(bufferAlpha);
                    await _channel.Writer.WriteAsync(bufferAlpha, _bridge.Token).ConfigureAwait(false);
                    isBlockingAlpha = false;
                }
                else
                {
                    _bridge.Ip!.ReadPacket(bufferBeta);
                    await _channel.Writer.WriteAsync(bufferBeta, _bridge.Token).ConfigureAwait(false);
                    isBlockingAlpha = true;
                }
            }
        });
    }

    private bool Load(ByteBuffer packet) // true if data protocol is enabled
    {
        var header = packet.GetInt(0);
        short protocol;
        switch (header & IpVersionMask)
        {
            case Ipv4VersionHeader:
                if (!_bridge.PppIPv4Enabled)
                {
                    return false;
                }

                protocol = Ppp.ProtocolIp;
                break;

            case Ipv6VersionHeader:
                if (!_bridge.PppIPv6Enabled)
                {
                    return false;
                }

                protocol = Ppp.ProtocolIpv6;
                break;

            default:
                _bridge.SendControlMessage(new ControlMessage(Where.OUTGOING, Result.ERR_UNKNOWN_TYPE));
                return false;
        }

        _mainBuffer.PutShort(SstpPackets.PacketTypeData);
        _mainBuffer.PutShort((short)(packet.Remaining + PrefixSize));
        _mainBuffer.PutShort(Ppp.HdlcHeader);
        _mainBuffer.PutShort(protocol);
        _mainBuffer.Put(packet);

        return true;
    }

    public void Cancel() => _channel.Writer.TryComplete();
}

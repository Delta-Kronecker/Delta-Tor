namespace DeltaTor.Core.Sstp;

/// <summary>
/// The Windows counterpart of <c>kittoku.osc.control.Controller</c>: drives one
/// SSTP session through SSL → SSTP request → LCP → authentication → (IPCP) →
/// either a live tunnel (<see cref="SstpMode.Run"/>) or a completed probe
/// (<see cref="SstpMode.Probe"/>), and tears everything down on the first failure.
/// </summary>
internal sealed class Controller
{
    private readonly SharedBridge _bridge;

    private SstpClient? _sstpClient;
    private PppClient? _pppClient;
    private IncomingManager? _incomingManager;
    private OutgoingManager? _outgoingManager;

    private LcpClient? _lcpClient;
    private PapClient? _papClient;
    private ChapClient? _chapClient;
    private EapClient? _eapClient;
    private IpcpClient? _ipcpClient;
    private Ipv6cpClient? _ipv6cpClient;

    private int _killed;

    public Controller(SharedBridge bridge) => _bridge = bridge;

    private bool IsProbe => _bridge.Params.Mode == SstpMode.Probe;

    public void LaunchJobMain()
    {
        AttachHandler();
        _bridge.Launch(RunMainAsync);
    }

    private void AttachHandler()
    {
        _bridge.ExceptionHandler = ex => Kill(async () =>
        {
            var header = "OSC: ERR_UNEXPECTED";
            _bridge.Log($"{header}\n{ex}");
            await Task.CompletedTask.ConfigureAwait(false);
        });
    }

    private async Task RunMainAsync()
    {
        _bridge.AttachSslTerminal();
        if (!IsProbe)
        {
            _bridge.AttachIpTerminal();
        }

        _bridge.Ssl!.Initialize();
        if (!await ExpectProceededAsync(Where.SSL, SslTerminal.SslRequestInterval).ConfigureAwait(false))
        {
            return;
        }

        _incomingManager = new IncomingManager(_bridge);
        _incomingManager.LaunchJobMain();

        _sstpClient = new SstpClient(_bridge);
        _incomingManager.RegisterMailbox(_sstpClient);
        _sstpClient.LaunchJobRequest();

        if (!await ExpectProceededAsync(Where.SSTP_REQUEST, SstpClient.SstpRequestTimeout).ConfigureAwait(false))
        {
            return;
        }

        _sstpClient.LaunchJobControl();

        _pppClient = new PppClient(_bridge);
        _incomingManager.RegisterMailbox(_pppClient);
        _pppClient.LaunchJobControl();

        _lcpClient = new LcpClient(_bridge);
        _incomingManager.RegisterMailbox(_lcpClient);
        _lcpClient.LaunchJobNegotiation();

        if (!await ExpectProceededAsync(Where.LCP, Configuration.PppNegotiationTimeout).ConfigureAwait(false))
        {
            return;
        }

        _incomingManager.UnregisterMailbox(_lcpClient);

        var authTimeout = (int)_bridge.Params.AuthTimeout;
        switch (_bridge.CurrentAuth)
        {
            case AuthProtocol.Pap:
                _papClient = new PapClient(_bridge);
                _incomingManager.RegisterMailbox(_papClient);
                _papClient.LaunchJobAuth();

                if (!await ExpectProceededAsync(Where.PAP, authTimeout).ConfigureAwait(false))
                {
                    return;
                }

                _incomingManager.UnregisterMailbox(_papClient);
                break;

            case AuthProtocol.MsChapV2:
                _chapClient = new ChapMsChapV2Client(_bridge);
                _incomingManager.RegisterMailbox(_chapClient);
                _chapClient.LaunchJobAuth();

                if (!await ExpectProceededAsync(Where.CHAP, authTimeout).ConfigureAwait(false))
                {
                    return;
                }

                break;

            case AuthProtocol.EapMsChapV2:
                _eapClient = new EapMsAuthClient(_bridge);
                _incomingManager.RegisterMailbox(_eapClient);
                _eapClient.LaunchJobAuth();

                if (!await ExpectProceededAsync(Where.EAP, authTimeout).ConfigureAwait(false))
                {
                    return;
                }

                break;

            default:
                throw new NotSupportedException(_bridge.CurrentAuth);
        }

        await _sstpClient.SendCallConnectedAsync().ConfigureAwait(false);

        if (_bridge.PppIPv4Enabled)
        {
            _ipcpClient = new IpcpClient(_bridge);
            _incomingManager.RegisterMailbox(_ipcpClient);
            _ipcpClient.LaunchJobNegotiation();

            if (!await ExpectProceededAsync(Where.IPCP, Configuration.PppNegotiationTimeout).ConfigureAwait(false))
            {
                return;
            }

            _incomingManager.UnregisterMailbox(_ipcpClient);
        }

        if (_bridge.PppIPv6Enabled)
        {
            _ipv6cpClient = new Ipv6cpClient(_bridge);
            _incomingManager.RegisterMailbox(_ipv6cpClient);
            _ipv6cpClient.LaunchJobNegotiation();

            if (!await ExpectProceededAsync(Where.IPV6CP, Configuration.PppNegotiationTimeout).ConfigureAwait(false))
            {
                return;
            }

            _incomingManager.UnregisterMailbox(_ipv6cpClient);
        }

        if (IsProbe)
        {
            // A probe session goes this far only: IP/DNS have been taken
            // from the server and later errors simply end up in the
            // mailbox until the caller tears the session down.
            SstpDebug.AssertAlways(_bridge.Ip is null);
            _bridge.Log("OSC: session established (probe)");
            _bridge.OnNegotiated();
            return;
        }

        await _bridge.Ip!.InitializeAsync().ConfigureAwait(false);
        if (!await ExpectProceededAsync(Where.IP, null).ConfigureAwait(false))
        {
            return;
        }

        _outgoingManager = new OutgoingManager(_bridge);
        _outgoingManager.LaunchJobMain();

        // The tunnel now passes packets both ways: the moment where a
        // chain becomes a working VPN.
        _bridge.OnNegotiated();

        // wait ERR_ message until disconnection
        await ExpectProceededAsync(Where.SSTP_CONTROL, null).ConfigureAwait(false);
    }

    private async Task<bool> ExpectProceededAsync(Where where, int? timeout)
    {
        ControlMessage received;
        if (timeout is int ms)
        {
            var tried = await _bridge.ControlMailbox.Reader
                .ReceiveWithTimeoutAsync(ms, _bridge.Token).ConfigureAwait(false);
            received = tried ?? new ControlMessage(where, Result.ERR_TIMEOUT);
        }
        else
        {
            received = await _bridge.ControlMailbox.Reader.ReadAsync(_bridge.Token).ConfigureAwait(false);
        }

        if (received.Result == Result.PROCEEDED)
        {
            SstpDebug.AssertAlways(received.From == where);

            return true;
        }

        var lastPacketType = received.Result == Result.ERR_DISCONNECT_REQUESTED
            ? SstpPackets.MessageCallDisconnectAck
            : SstpPackets.MessageCallAbort;

        Kill(async () =>
        {
            if (_sstpClient is not null)
            {
                await _sstpClient.SendLastPacketAsync(lastPacketType).ConfigureAwait(false);
            }

            var log = $"{received.From}: {received.Result}";
            if (received.Supplement is not null)
            {
                log += $"\n{received.Supplement}";
            }

            _bridge.Log(log);
        });

        return false;
    }

    /// <summary>Use if the user wants to normally disconnect.</summary>
    public void Disconnect()
    {
        Kill(async () =>
        {
            if (_sstpClient is not null)
            {
                await _sstpClient.SendLastPacketAsync(SstpPackets.MessageCallDisconnect).ConfigureAwait(false);
            }
        });
    }

    public void Kill(Func<Task>? cleanup = null)
    {
        if (Interlocked.Exchange(ref _killed, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // cancel jobMain and every client job
                _bridge.Cancel();

                if (cleanup is not null)
                {
                    await cleanup().ConfigureAwait(false);
                }

                CloseTerminals();
            }
            catch
            {
                // best effort: nothing left to do but report the session ended
            }
            finally
            {
                _bridge.OnSessionEnded();
            }
        });
    }

    private void CloseTerminals()
    {
        _bridge.Ssl?.Close();
        _bridge.Ip?.Close();
    }
}

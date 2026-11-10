namespace DeltaTor.Core.Sstp;

/// <summary>kittoku.osc.client.ppp.LCPClient.</summary>
internal sealed class LcpClient : ConfigClient<LcpConfigureFrame>
{
    private bool _isMruRejected;

    public LcpClient(SharedBridge bridge) : base(Where.LCP, bridge) { }

    protected override LcpConfigureFrame? TryCreateServerReject(LcpConfigureFrame request)
    {
        var reject = new LcpOptionPack();

        if (request.Options.UnknownOptions.Count > 0)
        {
            reject.UnknownOptions = request.Options.UnknownOptions;
        }

        if (reject.AllOptions.Count > 0)
        {
            var frame = new LcpConfigureReject
            {
                Id = request.Id,
                Options = reject
            };
            frame.Options.Order = request.Options.Order;
            return frame;
        }

        return null;
    }

    protected override LcpConfigureFrame? TryCreateServerNak(LcpConfigureFrame request)
    {
        var nak = new LcpOptionPack();

        var serverMru = request.Options.MruOption?.UnitSize ?? Configuration.DefaultMru;
        if (serverMru < Bridge.PppMtu)
        {
            nak.MruOption = new MruOption { UnitSize = Bridge.PppMtu };
        }

        var serverAuth = request.Options.AuthOption;
        var isAuthAcknowledged = false;

        switch (serverAuth?.Protocol)
        {
            case Ppp.ProtocolEap:
                if (Bridge.IsEnabled(AuthProtocol.EapMsChapV2))
                {
                    Bridge.CurrentAuth = AuthProtocol.EapMsChapV2;
                    isAuthAcknowledged = true;
                }

                break;

            case Ppp.ProtocolChap:
                if (serverAuth.Holder.Length != 1)
                {
                    throw new NotSupportedException(serverAuth.Holder.Length.ToString());
                }

                if (serverAuth.Holder[0] == Ppp.ChapAlgorithmMsChapV2 && Bridge.IsEnabled(AuthProtocol.MsChapV2))
                {
                    Bridge.CurrentAuth = AuthProtocol.MsChapV2;
                    isAuthAcknowledged = true;
                }

                break;

            case Ppp.ProtocolPap:
                if (Bridge.IsEnabled(AuthProtocol.Pap))
                {
                    Bridge.CurrentAuth = AuthProtocol.Pap;
                    isAuthAcknowledged = true;
                }

                break;
        }

        if (!isAuthAcknowledged)
        {
            var authOption = new AuthOption();
            if (Bridge.IsEnabled(AuthProtocol.EapMsChapV2))
            {
                authOption.Protocol = Ppp.ProtocolEap;
            }
            else if (Bridge.IsEnabled(AuthProtocol.MsChapV2))
            {
                authOption.Protocol = Ppp.ProtocolChap;
                authOption.Holder = new byte[] { Ppp.ChapAlgorithmMsChapV2 };
            }
            else if (Bridge.IsEnabled(AuthProtocol.Pap))
            {
                authOption.Protocol = Ppp.ProtocolPap;
            }
            else
            {
                throw new NotSupportedException("no authentication protocol");
            }

            nak.AuthOption = authOption;
        }

        if (nak.AllOptions.Count > 0)
        {
            var frame = new LcpConfigureNak
            {
                Id = request.Id,
                Options = nak
            };
            frame.Options.Order = request.Options.Order;
            return frame;
        }

        return null;
    }

    protected override LcpConfigureFrame CreateServerAck(LcpConfigureFrame request)
    {
        return new LcpConfigureAck
        {
            Id = request.Id,
            Options = request.Options
        };
    }

    protected override LcpConfigureFrame CreateClientRequest()
    {
        var request = new LcpConfigureRequest();

        if (!_isMruRejected)
        {
            request.Options.MruOption = new MruOption { UnitSize = Bridge.CurrentMru };
        }

        return request;
    }

    protected override Task TryAcceptClientNak(LcpConfigureFrame nak)
    {
        var mru = nak.Options.MruOption;
        if (mru is not null)
        {
            Bridge.CurrentMru = Math.Max(Math.Min(mru.UnitSize, Bridge.PppMru), Configuration.MinMru);
        }

        return Task.CompletedTask;
    }

    protected override Task TryAcceptClientReject(LcpConfigureFrame reject)
    {
        if (reject.Options.MruOption is not null)
        {
            _isMruRejected = true;

            if (Configuration.DefaultMru > Bridge.PppMru)
            {
                Bridge.SendControlMessage(new ControlMessage(Where.LCP_MRU, Result.ERR_OPTION_REJECTED));
            }
        }

        if (reject.Options.AuthOption is not null)
        {
            Bridge.SendControlMessage(new ControlMessage(Where.LCP_AUTH, Result.ERR_OPTION_REJECTED));
        }

        return Task.CompletedTask;
    }
}

/// <summary>kittoku.osc.client.ppp.IpcpClient.</summary>
internal sealed class IpcpClient : ConfigClient<IpcpConfigureFrame>
{
    // DHCP-less VPN Gate hubs hand out DNS over IPCP: always ask for it.
    private const bool IsDnsRequested = true;
    private bool _isDnsRejected;
    private readonly byte[]? _requestedAddress = null;

    public IpcpClient(SharedBridge bridge) : base(Where.IPCP, bridge) { }

    protected override IpcpConfigureFrame? TryCreateServerReject(IpcpConfigureFrame request)
    {
        var reject = new IpcpOptionPack();

        if (request.Options.UnknownOptions.Count > 0)
        {
            reject.UnknownOptions = request.Options.UnknownOptions;
        }

        if (request.Options.DnsOption is not null)
        {
            // client doesn't have dns server
            reject.DnsOption = request.Options.DnsOption;
        }

        if (reject.AllOptions.Count > 0)
        {
            var frame = new IpcpConfigureReject
            {
                Id = request.Id,
                Options = reject
            };
            frame.Options.Order = request.Options.Order;
            return frame;
        }

        return null;
    }

    protected override IpcpConfigureFrame? TryCreateServerNak(IpcpConfigureFrame request) => null;

    protected override IpcpConfigureFrame CreateServerAck(IpcpConfigureFrame request)
    {
        return new IpcpConfigureAck
        {
            Id = request.Id,
            Options = request.Options
        };
    }

    protected override IpcpConfigureFrame CreateClientRequest()
    {
        var request = new IpcpConfigureRequest();

        if (_requestedAddress is not null)
        {
            _requestedAddress.CopyTo(Bridge.CurrentIPv4, 0);
        }

        var ipOption = new IpcpAddressOption(Ppp.OptionTypeIpcpIp);
        Bridge.CurrentIPv4.CopyTo(ipOption.Address, 0);
        request.Options.IpOption = ipOption;

        if (IsDnsRequested && !_isDnsRejected)
        {
            var dnsOption = new IpcpAddressOption(Ppp.OptionTypeIpcpDns);
            Bridge.CurrentProposedDns.CopyTo(dnsOption.Address, 0);
            request.Options.DnsOption = dnsOption;
        }

        return request;
    }

    protected override Task TryAcceptClientNak(IpcpConfigureFrame nak)
    {
        var ip = nak.Options.IpOption;
        if (ip is not null)
        {
            if (_requestedAddress is not null)
            {
                Bridge.SendControlMessage(new ControlMessage(Where.IPCP, Result.ERR_ADDRESS_REJECTED));
            }
            else
            {
                ip.Address.CopyTo(Bridge.CurrentIPv4, 0);
            }
        }

        var dns = nak.Options.DnsOption;
        if (dns is not null)
        {
            dns.Address.CopyTo(Bridge.CurrentProposedDns, 0);
        }

        return Task.CompletedTask;
    }

    protected override Task TryAcceptClientReject(IpcpConfigureFrame reject)
    {
        if (reject.Options.IpOption is not null)
        {
            Bridge.SendControlMessage(new ControlMessage(Where.IPCP_IP, Result.ERR_OPTION_REJECTED));
        }

        if (reject.Options.DnsOption is not null)
        {
            _isDnsRejected = true;
        }

        return Task.CompletedTask;
    }
}

/// <summary>kittoku.osc.client.ppp.Ipv6cpClient.</summary>
internal sealed class Ipv6cpClient : ConfigClient<Ipv6cpConfigureFrame>
{
    public Ipv6cpClient(SharedBridge bridge) : base(Where.IPV6CP, bridge) { }

    protected override Ipv6cpConfigureFrame? TryCreateServerReject(Ipv6cpConfigureFrame request)
    {
        var reject = new Ipv6cpOptionPack();

        if (request.Options.UnknownOptions.Count > 0)
        {
            reject.UnknownOptions = request.Options.UnknownOptions;
        }

        if (reject.AllOptions.Count > 0)
        {
            var frame = new Ipv6cpConfigureReject
            {
                Id = request.Id,
                Options = reject
            };
            frame.Options.Order = request.Options.Order;
            return frame;
        }

        return null;
    }

    protected override Ipv6cpConfigureFrame? TryCreateServerNak(Ipv6cpConfigureFrame request) => null;

    protected override Ipv6cpConfigureFrame CreateServerAck(Ipv6cpConfigureFrame request)
    {
        return new Ipv6cpConfigureAck
        {
            Id = request.Id,
            Options = request.Options
        };
    }

    protected override Ipv6cpConfigureFrame CreateClientRequest()
    {
        var request = new Ipv6cpConfigureRequest();

        var option = new Ipv6cpIdentifierOption();
        Bridge.CurrentIPv6.CopyTo(option.Identifier, 0);
        request.Options.IdentifierOption = option;

        return request;
    }

    protected override Task TryAcceptClientNak(Ipv6cpConfigureFrame nak)
    {
        var identifier = nak.Options.IdentifierOption;
        if (identifier is not null)
        {
            identifier.Identifier.CopyTo(Bridge.CurrentIPv6, 0);
        }

        return Task.CompletedTask;
    }

    protected override Task TryAcceptClientReject(Ipv6cpConfigureFrame reject)
    {
        if (reject.Options.IdentifierOption is not null)
        {
            Bridge.SendControlMessage(new ControlMessage(Where.IPV6CP_IDENTIFIER, Result.ERR_OPTION_REJECTED));
        }

        return Task.CompletedTask;
    }
}

namespace DeltaTor.Core.Sstp;

/// <summary>PPP frame/option constants (Frame.kt, LCPFrame.kt, lcp.kt, ipcp.kt, ipv6cp.kt).</summary>
internal static class Ppp
{
    public const short HdlcHeader = unchecked((short)0xFF03);
    public const int HeaderSize = 4; // code, id, and frame length

    public const short ProtocolLcp = unchecked((short)0xC021);
    public const short ProtocolPap = unchecked((short)0xC023);
    public const short ProtocolChap = unchecked((short)0xC223);
    public const short ProtocolEap = unchecked((short)0xC227);
    public const short ProtocolIpcp = unchecked((short)0x8021);
    public const short ProtocolIpv6cp = unchecked((short)0x8057);
    public const short ProtocolIp = 0x0021;
    public const short ProtocolIpv6 = 0x0057;

    public const byte LcpCodeConfigureRequest = 1;
    public const byte LcpCodeConfigureAck = 2;
    public const byte LcpCodeConfigureNak = 3;
    public const byte LcpCodeConfigureReject = 4;
    public const byte LcpCodeTerminateRequest = 5;
    public const byte LcpCodeTerminateAck = 6;
    public const byte LcpCodeCodeReject = 7;
    public const byte LcpCodeProtocolReject = 8;
    public const byte LcpCodeEchoRequest = 9;
    public const byte LcpCodeEchoReply = 10;
    public const byte LcpCodeDiscardRequest = 11;

    public const byte OptionTypeLcpMru = 1;
    public const byte OptionTypeLcpAuth = 3;
    public const byte ChapAlgorithmMsChapV2 = 0x81;

    public const byte OptionTypeIpcpIp = 0x03;
    public const byte OptionTypeIpcpDns = 0x81;
    public const byte OptionTypeIpv6cpIdentifier = 0x01;
}

/// <summary>Frame.kt — the common SSTP-data-framed PPP record.</summary>
internal abstract class PppFrame : DataUnit
{
    public abstract byte Code { get; }
    public abstract short Protocol { get; }

    private const int OffsetSize = 8; // from SSTP header to PPP HDLC header
    protected const int HeaderSize = OffsetSize + Ppp.HeaderSize;

    protected int GivenLength;

    public byte Id { get; set; }

    protected void ReadHeader(ByteBuffer buffer)
    {
        SstpDebug.AssertAlways(buffer.GetShort() == SstpPackets.PacketTypeData);
        GivenLength = buffer.GetShort().ToIntAsUShort();

        SstpDebug.AssertAlways(buffer.GetShort() == Ppp.HdlcHeader);
        SstpDebug.AssertAlways(buffer.GetShort() == Protocol);
        SstpDebug.AssertAlways(buffer.Get() == Code);
        Id = buffer.Get();
        SstpDebug.AssertAlways(buffer.GetShort().ToIntAsUShort() + OffsetSize == GivenLength);
    }

    protected void WriteHeader(ByteBuffer buffer)
    {
        buffer.PutShort(SstpPackets.PacketTypeData);
        buffer.PutShort((short)Length);

        buffer.PutShort(Ppp.HdlcHeader);
        buffer.PutShort(Protocol);
        buffer.Put(Code);
        buffer.Put(Id);
        buffer.PutShort((short)(Length - OffsetSize));
    }
}

// ---- options -------------------------------------------------------------

internal abstract class PppOption : DataUnit
{
    public abstract byte Type { get; }

    protected const int OptionHeaderSize = 2;
    protected int GivenLength;

    protected virtual void ReadHeader(ByteBuffer buffer)
    {
        SstpDebug.AssertAlways(Type == buffer.Get());
        GivenLength = buffer.Get().ToIntAsUByte();
    }

    protected virtual void WriteHeader(ByteBuffer buffer)
    {
        buffer.Put(Type);
        buffer.Put((byte)Length);
    }
}

internal sealed class UnknownOption : PppOption
{
    public UnknownOption(byte unknownType) => Type = unknownType;

    public override byte Type { get; }

    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override int Length => OptionHeaderSize + Holder.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        var holderSize = GivenLength - Length;
        SstpDebug.AssertAlways(holderSize >= 0);

        if (holderSize > 0)
        {
            Holder = new byte[holderSize];
            buffer.Get(Holder);
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put(Holder);
    }
}

internal abstract class PppOptionPack : DataUnit
{
    private readonly int _givenLength;

    protected PppOptionPack(int givenLength = 0) => _givenLength = givenLength;

    public abstract IReadOnlyList<PppOption> KnownOptions { get; }

    public List<UnknownOption> UnknownOptions { get; set; } = new();

    public IReadOnlyList<PppOption> AllOptions
    {
        get
        {
            var list = new List<PppOption>(KnownOptions);
            list.AddRange(UnknownOptions);
            return list;
        }
    }

    public Dictionary<byte, int> Order { get; set; } = new();

    public override int Length => AllOptions.Sum(option => option.Length);

    protected abstract PppOption RetrieveOption(ByteBuffer buffer);

    private void EnsureValidOrder()
    {
        var nextIndex = Order.Count > 0 ? Order.Values.Max() : 0;

        foreach (var option in AllOptions)
        {
            if (!Order.ContainsKey(option.Type))
            {
                Order[option.Type] = nextIndex;
                nextIndex++;
            }
        }
    }

    public override void Read(ByteBuffer buffer)
    {
        var remaining = _givenLength - Length;

        var currentOrder = new Dictionary<byte, int>();
        var currentUnknownOptions = new List<UnknownOption>();

        var i = 0;
        while (true)
        {
            SstpDebug.AssertAlways(remaining >= 0);
            if (remaining == 0)
            {
                break;
            }

            var option = RetrieveOption(buffer);
            // if the option type is duplicated, the last option is preferred now
            currentOrder[option.Type] = i;
            remaining -= option.Length;

            if (option is UnknownOption unknown)
            {
                currentUnknownOptions.Add(unknown);
            }

            i++;
        }

        Order = currentOrder;
        UnknownOptions = currentUnknownOptions;
    }

    public override void Write(ByteBuffer buffer)
    {
        EnsureValidOrder();

        foreach (var option in AllOptions.OrderBy(option => Order[option.Type]))
        {
            option.Write(buffer);
        }
    }
}

internal sealed class MruOption : PppOption
{
    public override byte Type => Ppp.OptionTypeLcpMru;
    public override int Length => OptionHeaderSize + 2;

    public int UnitSize { get; set; }

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        UnitSize = buffer.GetShort().ToIntAsUShort();
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PutShort((short)UnitSize);
    }
}

internal sealed class AuthOption : PppOption
{
    public override byte Type => Ppp.OptionTypeLcpAuth;
    public short Protocol { get; set; }
    public byte[] Holder { get; set; } = System.Array.Empty<byte>();
    public override int Length => OptionHeaderSize + 2 + Holder.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Protocol = buffer.GetShort();

        var holderSize = GivenLength - Length;
        SstpDebug.AssertAlways(holderSize >= 0);

        if (holderSize > 0)
        {
            Holder = new byte[holderSize];
            buffer.Get(Holder);
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PutShort(Protocol);
        buffer.Put(Holder);
    }
}

internal sealed class LcpOptionPack : PppOptionPack
{
    public LcpOptionPack(int givenLength = 0) : base(givenLength) { }

    public MruOption? MruOption { get; set; }
    public AuthOption? AuthOption { get; set; }

    public override IReadOnlyList<PppOption> KnownOptions
    {
        get
        {
            var options = new List<PppOption>();
            if (MruOption is not null) options.Add(MruOption);
            if (AuthOption is not null) options.Add(AuthOption);
            return options;
        }
    }

    protected override PppOption RetrieveOption(ByteBuffer buffer)
    {
        PppOption option = buffer.ProbeByte(0) switch
        {
            Ppp.OptionTypeLcpMru => MruOption = new MruOption(),
            Ppp.OptionTypeLcpAuth => AuthOption = new AuthOption(),
            var type => new UnknownOption(type)
        };

        option.Read(buffer);

        return option;
    }
}

internal sealed class IpcpAddressOption : PppOption
{
    public IpcpAddressOption(byte optionType) => Type = optionType;

    public override byte Type { get; }

    public byte[] Address { get; } = new byte[4];

    public override int Length => OptionHeaderSize + Address.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        buffer.Get(Address);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put(Address);
    }
}

internal sealed class IpcpOptionPack : PppOptionPack
{
    public IpcpOptionPack(int givenLength = 0) : base(givenLength) { }

    public IpcpAddressOption? IpOption { get; set; }
    public IpcpAddressOption? DnsOption { get; set; }

    public override IReadOnlyList<PppOption> KnownOptions
    {
        get
        {
            var options = new List<PppOption>();
            if (IpOption is not null) options.Add(IpOption);
            if (DnsOption is not null) options.Add(DnsOption);
            return options;
        }
    }

    protected override PppOption RetrieveOption(ByteBuffer buffer)
    {
        var type = buffer.ProbeByte(0);
        PppOption option = type switch
        {
            Ppp.OptionTypeIpcpIp => IpOption = new IpcpAddressOption(type),
            Ppp.OptionTypeIpcpDns => DnsOption = new IpcpAddressOption(type),
            _ => new UnknownOption(type)
        };

        option.Read(buffer);

        return option;
    }
}

internal sealed class Ipv6cpIdentifierOption : PppOption
{
    public override byte Type => Ppp.OptionTypeIpv6cpIdentifier;
    public byte[] Identifier { get; } = new byte[8];
    public override int Length => OptionHeaderSize + Identifier.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        buffer.Get(Identifier);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put(Identifier);
    }
}

internal sealed class Ipv6cpOptionPack : PppOptionPack
{
    public Ipv6cpOptionPack(int givenLength = 0) : base(givenLength) { }

    public Ipv6cpIdentifierOption? IdentifierOption { get; set; }

    public override IReadOnlyList<PppOption> KnownOptions
    {
        get
        {
            var options = new List<PppOption>();
            if (IdentifierOption is not null) options.Add(IdentifierOption);
            return options;
        }
    }

    protected override PppOption RetrieveOption(ByteBuffer buffer)
    {
        var type = buffer.ProbeByte(0);
        PppOption option = type switch
        {
            Ppp.OptionTypeIpv6cpIdentifier =>
                IdentifierOption = new Ipv6cpIdentifierOption(),
            _ => new UnknownOption(type)
        };

        option.Read(buffer);

        return option;
    }
}

// ---- LCP frames ----------------------------------------------------------

internal abstract class LcpFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolLcp;
}

internal abstract class LcpConfigureFrame : LcpFrame
{
    public LcpOptionPack Options { get; set; } = new();

    public override int Length => HeaderSize + Options.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Options = new LcpOptionPack(GivenLength - Length);
        Options.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Options.Write(buffer);
    }
}

internal sealed class LcpConfigureRequest : LcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureRequest;
}

internal sealed class LcpConfigureAck : LcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureAck;
}

internal sealed class LcpConfigureNak : LcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureNak;
}

internal sealed class LcpConfigureReject : LcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureReject;
}

internal abstract class LcpDataHoldingFrame : LcpFrame
{
    public override int Length => HeaderSize + Holder.Length;

    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        var holderSize = GivenLength - Length;
        SstpDebug.AssertAlways(holderSize >= 0);

        if (holderSize > 0)
        {
            Holder = new byte[holderSize];
            buffer.Get(Holder);
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put(Holder);
    }
}

internal sealed class LcpTerminalRequest : LcpDataHoldingFrame
{
    public override byte Code => Ppp.LcpCodeTerminateRequest;
}

internal sealed class LcpTerminalAck : LcpDataHoldingFrame
{
    public override byte Code => Ppp.LcpCodeTerminateAck;
}

internal sealed class LcpCodeReject : LcpDataHoldingFrame
{
    public override byte Code => Ppp.LcpCodeCodeReject;
}

internal sealed class LcpProtocolReject : LcpFrame
{
    public override byte Code => Ppp.LcpCodeProtocolReject;
    public override int Length => HeaderSize + 2 + Holder.Length;

    public short RejectedProtocol { get; set; }

    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        RejectedProtocol = buffer.GetShort();

        var holderSize = GivenLength - Length;
        SstpDebug.AssertAlways(holderSize >= 0);

        if (holderSize > 0)
        {
            Holder = new byte[holderSize];
            buffer.Get(Holder);
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PutShort(RejectedProtocol);
        buffer.Put(Holder);
    }
}

internal abstract class LcpMagicNumberFrame : LcpFrame
{
    public override int Length => HeaderSize + 4 + Holder.Length;

    public int MagicNumber { get; set; }

    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        MagicNumber = buffer.GetInt();

        var holderSize = GivenLength - Length;
        SstpDebug.AssertAlways(holderSize >= 0);

        if (holderSize > 0)
        {
            Holder = new byte[holderSize];
            buffer.Get(Holder);
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PutInt(MagicNumber);
        buffer.Put(Holder);
    }
}

internal sealed class LcpEchoRequest : LcpMagicNumberFrame
{
    public override byte Code => Ppp.LcpCodeEchoRequest;
}

internal sealed class LcpEchoReply : LcpMagicNumberFrame
{
    public override byte Code => Ppp.LcpCodeEchoReply;
}

internal sealed class LcpDiscardRequest : LcpMagicNumberFrame
{
    public override byte Code => Ppp.LcpCodeDiscardRequest;
}

// ---- IPCP frames ---------------------------------------------------------

internal abstract class IpcpFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolIpcp;
}

internal abstract class IpcpConfigureFrame : IpcpFrame
{
    public IpcpOptionPack Options { get; set; } = new();

    public override int Length => HeaderSize + Options.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Options = new IpcpOptionPack(GivenLength - Length);
        Options.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Options.Write(buffer);
    }
}

internal sealed class IpcpConfigureRequest : IpcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureRequest;
}

internal sealed class IpcpConfigureAck : IpcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureAck;
}

internal sealed class IpcpConfigureNak : IpcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureNak;
}

internal sealed class IpcpConfigureReject : IpcpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureReject;
}

// ---- IPv6CP frames -------------------------------------------------------

internal abstract class Ipv6cpFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolIpv6cp;
}

internal abstract class Ipv6cpConfigureFrame : Ipv6cpFrame
{
    public Ipv6cpOptionPack Options { get; set; } = new();

    public override int Length => HeaderSize + Options.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Options = new Ipv6cpOptionPack(GivenLength - Length);
        Options.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Options.Write(buffer);
    }
}

internal sealed class Ipv6cpConfigureRequest : Ipv6cpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureRequest;
}

internal sealed class Ipv6cpConfigureAck : Ipv6cpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureAck;
}

internal sealed class Ipv6cpConfigureNak : Ipv6cpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureNak;
}

internal sealed class Ipv6cpConfigureReject : Ipv6cpConfigureFrame
{
    public override byte Code => Ppp.LcpCodeConfigureReject;
}

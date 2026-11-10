namespace DeltaTor.Core.Sstp;

/// <summary>SSTP packet/message/attribute constants (kittoku/osc/unit/sstp).</summary>
internal static class SstpPackets
{
    public const short PacketTypeData = 0x1000;
    public const short PacketTypeControl = 0x1001;

    public const short MessageCallConnectRequest = 1;
    public const short MessageCallConnectAck = 2;
    public const short MessageCallConnectNak = 3;
    public const short MessageCallConnected = 4;
    public const short MessageCallAbort = 5;
    public const short MessageCallDisconnect = 6;
    public const short MessageCallDisconnectAck = 7;
    public const short MessageEchoRequest = 8;
    public const short MessageEchoResponse = 9;

    public const byte AttributeIdNoError = 0;
    public const byte AttributeIdEncapsulatedProtocolId = 1;
    public const byte AttributeIdStatusInfo = 2;
    public const byte AttributeIdCryptoBinding = 3;
    public const byte AttributeIdCryptoBindingReq = 4;

    public const byte CertHashProtocolSha1 = 1;
    public const byte CertHashProtocolSha256 = 2;
}

/// <summary>Attribute.kt.</summary>
internal abstract class Attribute : DataUnit
{
    public abstract byte Id { get; }

    protected int GivenLength;

    protected void ReadHeader(ByteBuffer buffer)
    {
        buffer.Move(1);
        SstpDebug.AssertAlways(buffer.Get() == Id);
        GivenLength = buffer.GetShort().ToIntAsUShort();
    }

    protected void WriteHeader(ByteBuffer buffer)
    {
        buffer.Put(0);
        buffer.Put(Id);
        buffer.PutShort((short)Length);
    }
}

internal sealed class EncapsulatedProtocolId : Attribute
{
    public override byte Id => SstpPackets.AttributeIdEncapsulatedProtocolId;
    public override int Length => 6;

    public short ProtocolId { get; set; } = 1;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);

        ProtocolId = buffer.GetShort();
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PutShort(ProtocolId);
    }
}

internal sealed class StatusInfo : Attribute
{
    public override byte Id => SstpPackets.AttributeIdStatusInfo;

    private const int MinimumLength = 12;
    private const int MaximumHolderSize = 64;

    public override int Length => MinimumLength + Holder.Length;

    public byte TargetId { get; set; }
    public int Status { get; set; }
    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        var holderSize = GivenLength - MinimumLength;
        SstpDebug.AssertAlways(holderSize >= 0 && holderSize <= MaximumHolderSize);

        buffer.Move(3);
        TargetId = buffer.Get();
        Status = buffer.GetInt();
        Holder = new byte[holderSize];
        buffer.Get(Holder);
    }

    public override void Write(ByteBuffer buffer)
    {
        SstpDebug.AssertAlways(Holder.Length <= MaximumHolderSize);

        WriteHeader(buffer);
        buffer.PadZeroByte(3);
        buffer.Put(TargetId);
        buffer.PutInt(Status);
        buffer.Put(Holder);
    }
}

internal sealed class CryptoBinding : Attribute
{
    public override byte Id => SstpPackets.AttributeIdCryptoBinding;
    public override int Length => 104;

    public byte HashProtocol { get; set; } = 2;
    public byte[] Nonce { get; } = new byte[32];
    public byte[] CertHash { get; } = new byte[32];
    public byte[] CompoundMac { get; } = new byte[32];

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);

        buffer.Move(3);
        HashProtocol = buffer.Get();
        buffer.Get(Nonce);
        buffer.Get(CertHash);
        buffer.Get(CompoundMac);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);
        buffer.PadZeroByte(3);
        buffer.Put(HashProtocol);
        buffer.Put(Nonce);
        buffer.Put(CertHash);
        buffer.Put(CompoundMac);
    }
}

internal sealed class CryptoBindingRequest : Attribute
{
    public override byte Id => SstpPackets.AttributeIdCryptoBindingReq;
    public override int Length => 40;

    public byte Bitmask { get; set; } = 3;
    public byte[] Nonce { get; } = new byte[32];

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);

        buffer.Move(3);
        Bitmask = buffer.Get();
        buffer.Get(Nonce);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.PadZeroByte(3);
        buffer.Put(Bitmask);
        buffer.Put(Nonce);
    }
}

/// <summary>ControlPacket.kt.</summary>
internal abstract class ControlPacket : DataUnit
{
    public abstract short Type { get; }
    public abstract short NumAttribute { get; }

    protected int GivenLength;
    protected int GivenNumAttribute;

    protected void ReadHeader(ByteBuffer buffer)
    {
        SstpDebug.AssertAlways(buffer.GetShort() == SstpPackets.PacketTypeControl);
        GivenLength = buffer.GetShort().ToIntAsUShort();
        SstpDebug.AssertAlways(buffer.GetShort() == Type);
        GivenNumAttribute = buffer.GetShort().ToIntAsUShort();
    }

    protected void WriteHeader(ByteBuffer buffer)
    {
        buffer.PutShort(SstpPackets.PacketTypeControl);
        buffer.PutShort((short)Length);
        buffer.PutShort(Type);
        buffer.PutShort(NumAttribute);
    }
}

internal sealed class SstpCallConnectRequest : ControlPacket
{
    public override short Type => SstpPackets.MessageCallConnectRequest;
    public override int Length => 14;
    public override short NumAttribute => 1;

    public EncapsulatedProtocolId Protocol { get; set; } = new();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);
        SstpDebug.AssertAlways(GivenNumAttribute == NumAttribute);

        Protocol.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Protocol.Write(buffer);
    }
}

internal sealed class SstpCallConnectAck : ControlPacket
{
    public override short Type => SstpPackets.MessageCallConnectAck;
    public override int Length => 48;
    public override short NumAttribute => 1;

    public CryptoBindingRequest Request { get; set; } = new();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);
        SstpDebug.AssertAlways(GivenNumAttribute == NumAttribute);

        Request.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Request.Write(buffer);
    }
}

internal sealed class SstpCallConnectNak : ControlPacket
{
    public override short Type => SstpPackets.MessageCallConnectNak;

    public override int Length =>
        8 + StatusInfos.Sum(info => info.Length) + Holder.Length;

    public override short NumAttribute => (short)StatusInfos.Count;

    public List<StatusInfo> StatusInfos { get; } = new();

    public byte[] Holder { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        for (var i = 0; i < GivenNumAttribute; i++)
        {
            var info = new StatusInfo();
            info.Read(buffer);
            StatusInfos.Add(info);
        }

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

        foreach (var info in StatusInfos)
        {
            info.Write(buffer);
        }

        buffer.Put(Holder);
    }
}

internal sealed class SstpCallConnected : ControlPacket
{
    public override short Type => SstpPackets.MessageCallConnected;
    public override int Length => 112;
    public override short NumAttribute => 1;

    public CryptoBinding Binding { get; set; } = new();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
        SstpDebug.AssertAlways(GivenLength == Length);
        SstpDebug.AssertAlways(GivenNumAttribute == NumAttribute);

        Binding.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Binding.Write(buffer);
    }
}

internal abstract class TerminatePacket : ControlPacket
{
    public override int Length => 8 + (StatusInfo?.Length ?? 0);
    public override short NumAttribute => (short)(StatusInfo is null ? 0 : 1);

    public StatusInfo? StatusInfo { get; set; }

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        StatusInfo = GivenNumAttribute switch
        {
            0 => null,
            1 => ReadOne(buffer),
            _ => throw new ParsingDataUnitException()
        };

        SstpDebug.AssertAlways(GivenLength == Length);

        static StatusInfo ReadOne(ByteBuffer buffer)
        {
            var info = new StatusInfo();
            info.Read(buffer);
            return info;
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        StatusInfo?.Write(buffer);
    }
}

internal sealed class SstpCallAbort : TerminatePacket
{
    public override short Type => SstpPackets.MessageCallAbort;
}

internal sealed class SstpCallDisconnect : TerminatePacket
{
    public override short Type => SstpPackets.MessageCallDisconnect;
}

internal abstract class NoAttributePacket : ControlPacket
{
    public override int Length => 8;
    public override short NumAttribute => 0;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);
    }
}

internal sealed class SstpCallDisconnectAck : NoAttributePacket
{
    public override short Type => SstpPackets.MessageCallDisconnectAck;
}

internal sealed class SstpEchoRequest : NoAttributePacket
{
    public override short Type => SstpPackets.MessageEchoRequest;
}

internal sealed class SstpEchoResponse : NoAttributePacket
{
    public override short Type => SstpPackets.MessageEchoResponse;
}

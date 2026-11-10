namespace DeltaTor.Core.Sstp;

/// <summary>Auth protocol codes (PAPFrame.kt, ChapFrame.kt, EAPFrame.kt).</summary>
internal static class AuthCodes
{
    public const byte PapAuthenticateRequest = 1;
    public const byte PapAuthenticateAck = 2;
    public const byte PapAuthenticateNak = 3;

    public const byte ChapChallenge = 1;
    public const byte ChapResponse = 2;
    public const byte ChapSuccess = 3;
    public const byte ChapFailure = 4;

    public const byte EapRequest = 1;
    public const byte EapResponse = 2;
    public const byte EapSuccess = 3;
    public const byte EapFailure = 4;

    public const byte EapTypeIdentity = 1;
    public const byte EapTypeNotification = 2;
    public const byte EapTypeNak = 3;
    public const byte EapTypeMsAuth = 26; // MSCHAPV2
}

/// <summary>ChapField.kt.</summary>
internal sealed class ChapValueNameField : DataUnit
{
    public byte[] Value { get; set; } = System.Array.Empty<byte>();
    public byte[] Name { get; set; } = System.Array.Empty<byte>();
    public int GivenLength { get; set; } // must be given before reading

    public override int Length => 1 + Value.Length + Name.Length;

    public override void Read(ByteBuffer buffer)
    {
        Value = new byte[buffer.Get().ToIntAsUByte()];
        buffer.Get(Value);

        Name = new byte[GivenLength - Length];
        buffer.Get(Name);
    }

    public override void Write(ByteBuffer buffer)
    {
        buffer.Put((byte)Value.Length);
        buffer.Put(Value);
        buffer.Put(Name);
    }
}

internal sealed class ChapMessageField : DataUnit
{
    public byte[] Message { get; set; } = System.Array.Empty<byte>();
    public int GivenLength { get; set; } // must be given before reading

    public override int Length => Message.Length;

    public override void Read(ByteBuffer buffer)
    {
        Message = new byte[GivenLength];
        buffer.Get(Message);
    }

    public override void Write(ByteBuffer buffer)
    {
        buffer.Put(Message);
    }
}

// ---- PAP -----------------------------------------------------------------

internal abstract class PapFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolPap;
}

internal sealed class PapAuthenticateRequest : PapFrame
{
    public override byte Code => AuthCodes.PapAuthenticateRequest;

    public override int Length => HeaderSize + 1 + IdField.Length + 1 + PasswordField.Length;

    public byte[] IdField { get; set; } = System.Array.Empty<byte>();
    public byte[] PasswordField { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        var idLength = buffer.Get().ToIntAsUByte();
        IdField = new byte[idLength];
        buffer.Get(IdField);

        var passwordLength = buffer.Get().ToIntAsUByte();
        PasswordField = new byte[passwordLength];
        buffer.Get(PasswordField);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put((byte)IdField.Length);
        buffer.Put(IdField);
        buffer.Put((byte)PasswordField.Length);
        buffer.Put(PasswordField);
    }
}

internal abstract class PapAuthenticateAcknowledgement : PapFrame
{
    public override int Length => HeaderSize + (Message.Length == 0 ? 0 : Message.Length + 1);

    private byte[] Message { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        var remaining = Length - HeaderSize;
        switch (remaining)
        {
            case 0:
                break;
            case > 0:
                var messageLength = buffer.Get().ToIntAsUByte();
                SstpDebug.AssertAlways(messageLength == remaining - 1);
                Message = new byte[messageLength];
                buffer.Get(Message);
                break;
            default:
                throw new ParsingDataUnitException();
        }
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        if (Message.Length != 0)
        {
            buffer.Put((byte)Message.Length);
            buffer.Put(Message);
        }
    }
}

internal sealed class PapAuthenticateAck : PapAuthenticateAcknowledgement
{
    public override byte Code => AuthCodes.PapAuthenticateAck;
}

internal sealed class PapAuthenticateNak : PapAuthenticateAcknowledgement
{
    public override byte Code => AuthCodes.PapAuthenticateNak;
}

// ---- CHAP ----------------------------------------------------------------

internal abstract class ChapFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolChap;
}

internal abstract class ChapValueNameFrame : ChapFrame
{
    public ChapValueNameField ValueName { get; set; } = new();

    public override int Length => HeaderSize + ValueName.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        ValueName.GivenLength = GivenLength - HeaderSize;
        ValueName.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        ValueName.Write(buffer);
    }
}

internal sealed class ChapChallenge : ChapValueNameFrame
{
    public override byte Code => AuthCodes.ChapChallenge;
}

internal sealed class ChapResponse : ChapValueNameFrame
{
    public override byte Code => AuthCodes.ChapResponse;
}

internal abstract class ChapMessageFrame : ChapFrame
{
    public ChapMessageField Message { get; set; } = new();

    public override int Length => HeaderSize + Message.Length;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Message.GivenLength = GivenLength - HeaderSize;
        Message.Read(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        Message.Write(buffer);
    }
}

internal sealed class ChapSuccess : ChapMessageFrame
{
    public override byte Code => AuthCodes.ChapSuccess;
}

internal sealed class ChapFailure : ChapMessageFrame
{
    public override byte Code => AuthCodes.ChapFailure;
}

// ---- EAP -----------------------------------------------------------------

internal abstract class EapFrame : PppFrame
{
    public override short Protocol => Ppp.ProtocolEap;
}

internal abstract class EapDataFrame : EapFrame
{
    public override int Length => HeaderSize + 1 + TypeData.Length;

    public byte Type { get; set; }

    public byte[] TypeData { get; set; } = System.Array.Empty<byte>();

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);

        Type = buffer.Get();
        TypeData = new byte[GivenLength - Length];
        buffer.Get(TypeData);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);

        buffer.Put(Type);
        buffer.Put(TypeData);
    }
}

internal sealed class EapRequest : EapDataFrame
{
    public override byte Code => AuthCodes.EapRequest;
}

internal sealed class EapResponse : EapDataFrame
{
    public override byte Code => AuthCodes.EapResponse;
}

internal abstract class EapResultFrame : EapFrame
{
    public override int Length => HeaderSize;

    public override void Read(ByteBuffer buffer)
    {
        ReadHeader(buffer);
    }

    public override void Write(ByteBuffer buffer)
    {
        WriteHeader(buffer);
    }
}

internal sealed class EapSuccess : EapResultFrame
{
    public override byte Code => AuthCodes.EapSuccess;
}

internal sealed class EapFailure : EapResultFrame
{
    public override byte Code => AuthCodes.EapFailure;
}

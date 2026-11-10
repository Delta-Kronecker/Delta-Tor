using System.Text;
using System.Threading.Channels;

namespace DeltaTor.Core.Sstp;

/// <summary>kittoku.osc.client.ppp.auth.PAPClient.</summary>
internal sealed class PapClient
{
    private readonly SharedBridge _bridge;

    public Channel<PapFrame> Mailbox { get; } = Channel.CreateUnbounded<PapFrame>();

    public PapClient(SharedBridge bridge) => _bridge = bridge;

    public void LaunchJobAuth()
    {
        _bridge.Launch(async () =>
        {
            var currentId = await _bridge.AllocateNewFrameIdAsync().ConfigureAwait(false);

            await SendPapRequestAsync(currentId).ConfigureAwait(false);

            while (!_bridge.Token.IsCancellationRequested)
            {
                var received = await Mailbox.Reader.ReadAsync(_bridge.Token).ConfigureAwait(false);

                if (received.Id != currentId)
                {
                    continue;
                }

                switch (received.Code)
                {
                    case AuthCodes.PapAuthenticateAck:
                        _bridge.SendControlMessage(new ControlMessage(Where.PAP, Result.PROCEEDED));
                        break;

                    case AuthCodes.PapAuthenticateNak:
                        _bridge.SendControlMessage(new ControlMessage(Where.PAP, Result.ERR_AUTHENTICATION_FAILED));
                        break;
                }
            }
        });
    }

    private async Task SendPapRequestAsync(byte id)
    {
        var request = new PapAuthenticateRequest
        {
            Id = id,
            IdField = Encoding.ASCII.GetBytes(_bridge.HomeUsername),
            PasswordField = Encoding.ASCII.GetBytes(_bridge.HomePassword)
        };

        await _bridge.Ssl!.SendAsync(request.ToByteBuffer()).ConfigureAwait(false);
    }

    public void Cancel() => Mailbox.Writer.TryComplete();
}

/// <summary>kittoku.osc.client.ppp.auth.ChapClient (abstract).</summary>
internal abstract class ChapClient
{
    protected readonly SharedBridge Bridge;

    public Channel<ChapFrame> Mailbox { get; } = Channel.CreateUnbounded<ChapFrame>();

    protected byte ChallengeId;

    protected ChapClient(SharedBridge bridge) => Bridge = bridge;

    public void LaunchJobAuth()
    {
        Bridge.Launch(async () =>
        {
            while (!Bridge.Token.IsCancellationRequested)
            {
                var received = await Mailbox.Reader.ReadAsync(Bridge.Token).ConfigureAwait(false);

                switch (received)
                {
                    case ChapChallenge challenge:
                        ChallengeId = challenge.Id;

                        await ResponseChallengeAsync(challenge).ConfigureAwait(false);
                        break;

                    case ChapMessageFrame message:
                        if (message.Id == ChallengeId)
                        {
                            await ProcessResultAsync(message).ConfigureAwait(false);
                        }

                        break;
                }
            }
        });
    }

    protected abstract Task ResponseChallengeAsync(ChapChallenge challenge);

    protected abstract Task ProcessResultAsync(ChapMessageFrame result);

    public void Cancel() => Mailbox.Writer.TryComplete();
}

/// <summary>kittoku.osc.client.ppp.auth.ChapMSCHAPV2Client.</summary>
internal sealed class ChapMsChapV2Client : ChapClient
{
    private readonly MsChapV2Authenticator _authClient;

    public ChapMsChapV2Client(SharedBridge bridge) : base(bridge) =>
        _authClient = new MsChapV2Authenticator(bridge);

    protected override async Task ResponseChallengeAsync(ChapChallenge challenge)
    {
        var response = new ChapResponse
        {
            Id = ChallengeId,
            ValueName = _authClient.ProcessChallenge(challenge.ValueName)
        };

        await Bridge.Ssl!.SendAsync(response.ToByteBuffer()).ConfigureAwait(false);
    }

    protected override Task ProcessResultAsync(ChapMessageFrame result)
    {
        switch (result)
        {
            case ChapSuccess success:
                if (_authClient.VerifyAuthenticator(success.Message))
                {
                    _authClient.PrepareHlak();

                    Bridge.SendControlMessage(new ControlMessage(Where.CHAP, Result.PROCEEDED));
                }
                else
                {
                    Bridge.SendControlMessage(new ControlMessage(Where.MSCHAPV2, Result.ERR_VERIFICATION_FAILED));
                }

                break;

            case ChapFailure:
                Bridge.SendControlMessage(new ControlMessage(Where.MSCHAPV2, Result.ERR_AUTHENTICATION_FAILED));
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>kittoku.osc.client.ppp.auth.EAPClient (abstract).</summary>
internal abstract class EapClient
{
    protected readonly SharedBridge Bridge;

    protected abstract byte Algorithm { get; }

    public Channel<EapFrame> Mailbox { get; } = Channel.CreateUnbounded<EapFrame>();

    protected bool IsResultAcceptable;

    protected EapClient(SharedBridge bridge) => Bridge = bridge;

    public void LaunchJobAuth()
    {
        Bridge.Launch(async () =>
        {
            while (!Bridge.Token.IsCancellationRequested)
            {
                var received = await Mailbox.Reader.ReadAsync(Bridge.Token).ConfigureAwait(false);

                switch (received)
                {
                    case EapRequest request:
                        switch (request.Type)
                        {
                            case AuthCodes.EapTypeIdentity:
                                await SendIdentityAsync(request).ConfigureAwait(false);
                                break;

                            case AuthCodes.EapTypeNotification:
                            case AuthCodes.EapTypeNak:
                                break;

                            default:
                                if (request.Type == Algorithm)
                                {
                                    await ResponseRequestAsync(request).ConfigureAwait(false);
                                }
                                else
                                {
                                    await SendNakAsync(request).ConfigureAwait(false);
                                }

                                break;
                        }

                        break;

                    case EapResultFrame resultFrame:
                        if (IsResultAcceptable)
                        {
                            switch (resultFrame)
                            {
                                case EapSuccess:
                                    Bridge.SendControlMessage(new ControlMessage(Where.EAP, Result.PROCEEDED));
                                    break;

                                case EapFailure:
                                    Bridge.SendControlMessage(new ControlMessage(Where.EAP, Result.ERR_AUTHENTICATION_FAILED));
                                    break;
                            }
                        }

                        break;
                }
            }
        });
    }

    private async Task SendIdentityAsync(EapRequest serverIdentity)
    {
        var response = new EapResponse
        {
            Id = serverIdentity.Id,
            Type = AuthCodes.EapTypeIdentity,
            TypeData = Encoding.ASCII.GetBytes(Bridge.HomeUsername)
        };

        await Bridge.Ssl!.SendAsync(response.ToByteBuffer()).ConfigureAwait(false);
    }

    private async Task SendNakAsync(EapRequest serverRequest)
    {
        var response = new EapResponse
        {
            Id = serverRequest.Id,
            Type = AuthCodes.EapTypeNak,
            TypeData = new[] { Algorithm }
        };

        await Bridge.Ssl!.SendAsync(response.ToByteBuffer()).ConfigureAwait(false);
    }

    protected abstract Task ResponseRequestAsync(EapRequest request);

    public void Cancel() => Mailbox.Writer.TryComplete();
}

/// <summary>kittoku.osc.client.ppp.auth.EAPMSAuthClient.</summary>
internal sealed class EapMsAuthClient : EapClient
{
    protected override byte Algorithm => AuthCodes.EapTypeMsAuth;

    private byte _challengeId;
    private readonly MsChapV2Authenticator _authClient;

    public EapMsAuthClient(SharedBridge bridge) : base(bridge) =>
        _authClient = new MsChapV2Authenticator(bridge);

    protected override async Task ResponseRequestAsync(EapRequest request)
    {
        var wrapped = ByteBuffer.Wrap(request.TypeData);

        var opCode = wrapped.Get();
        var givenInnerId = wrapped.Get();
        var fieldLength = wrapped.GetShort().ToIntAsUShort() - Ppp.HeaderSize;
        SstpDebug.AssertAlways(fieldLength == wrapped.Remaining);

        switch (opCode)
        {
            case AuthCodes.ChapChallenge:
                IsResultAcceptable = false; // ensure this for (re)starting authentication
                _challengeId = givenInnerId;

                var field = new ChapValueNameField { GivenLength = fieldLength };
                field.Read(wrapped);

                var response = _authClient.ProcessChallenge(field);

                var eap = new EapResponse
                {
                    Id = request.Id,
                    Type = AuthCodes.EapTypeMsAuth
                };

                var header = ByteBuffer.Allocate(Ppp.HeaderSize);
                header.Put(AuthCodes.ChapResponse);
                header.Put(_challengeId);
                header.PutShort((short)(Ppp.HeaderSize + response.Length));

                var responseBytes = response.ToByteBuffer().Array;
                var typeData = new byte[header.Array.Length + responseBytes.Length];
                System.Array.Copy(header.Array, typeData, header.Array.Length);
                System.Array.Copy(responseBytes, 0, typeData, header.Array.Length, responseBytes.Length);
                eap.TypeData = typeData;

                await Bridge.Ssl!.SendAsync(eap.ToByteBuffer()).ConfigureAwait(false);
                break;

            case AuthCodes.ChapSuccess:
                SstpDebug.AssertAlways(givenInnerId == _challengeId);

                var message = new ChapMessageField { GivenLength = fieldLength };
                message.Read(wrapped);

                if (_authClient.VerifyAuthenticator(message))
                {
                    IsResultAcceptable = true;
                    _authClient.PrepareHlak();

                    var success = new EapResponse
                    {
                        Id = request.Id,
                        Type = AuthCodes.EapTypeMsAuth,
                        TypeData = new[] { AuthCodes.ChapSuccess }
                    };

                    await Bridge.Ssl!.SendAsync(success.ToByteBuffer()).ConfigureAwait(false);
                }
                else
                {
                    Bridge.SendControlMessage(new ControlMessage(Where.MSCHAPV2, Result.ERR_VERIFICATION_FAILED));
                }

                break;

            case AuthCodes.ChapFailure:
                SstpDebug.AssertAlways(givenInnerId == _challengeId);

                Bridge.SendControlMessage(new ControlMessage(Where.MSCHAPV2, Result.ERR_AUTHENTICATION_FAILED));
                break;
        }
    }
}

/// <summary>
/// kittoku.osc.client.ppp.auth.MSCHAPV2Client: the challenge/response exchange
/// and the derived keys. MD4 is supplied by <see cref="Crypto"/>; DES-ECB by the
/// framework cipher.
/// </summary>
internal sealed class MsChapV2Authenticator
{
    private readonly SharedBridge _bridge;
    private readonly byte[] _serverChallenge = new byte[16];
    private readonly byte[] _clientChallenge = new byte[16];
    private readonly byte[] _serverResponse = new byte[42];
    private readonly byte[] _clientResponse = new byte[24];

    public MsChapV2Authenticator(SharedBridge bridge) => _bridge = bridge;

    public ChapValueNameField ProcessChallenge(ChapValueNameField challenge)
    {
        challenge.Value.CopyTo(_serverChallenge, 0);

        PrepareClientResponse();

        var result = new ChapValueNameField
        {
            Name = Encoding.ASCII.GetBytes(_bridge.HomeUsername),
            Value = new byte[49]
        };

        _clientChallenge.CopyTo(result.Value, 0);
        _clientResponse.CopyTo(result.Value, 24);

        return result;
    }

    public bool VerifyAuthenticator(ChapMessageField success)
    {
        if (success.Message.Length < _serverResponse.Length)
        {
            return false;
        }

        System.Array.Copy(success.Message, _serverResponse, _serverResponse.Length);

        return CheckServerResponse();
    }

    private static bool IsEvenBits(int value)
    {
        // only count the bits of the least significant byte
        var count = 0;
        var holder = value;
        for (var i = 0; i < 8; i++)
        {
            count += holder & 1;
            holder >>= 1;
        }

        return count % 2 == 0;
    }

    private static byte[] AddParity(byte[] bytes)
    {
        var buffer = ByteBuffer.Allocate(8);
        buffer.Position = 1;
        buffer.Put(bytes);
        buffer.Clear();
        var holder = buffer.GetLong(0);
        const long mask = 0b0111_1111L;

        for (var i = 0; i < 8; i++)
        {
            var b = (int)(holder & mask);
            b = (b << 1) + (IsEvenBits(b) ? 1 : 0);
            buffer.Array[7 - i] = (byte)b;
            holder >>= 7;
        }

        return buffer.Array;
    }

    private void PrepareClientResponse()
    {
        Crypto.RandomBytes(16).CopyTo(_clientChallenge, 0);

        var userArray = Encoding.ASCII.GetBytes(_bridge.HomeUsername);
        var passArray = Encoding.Unicode.GetBytes(_bridge.HomePassword);

        var challenge = Crypto.Sha1(_clientChallenge, _serverChallenge, userArray)[..8];

        var zeroPassHash = new byte[21];
        Crypto.HashMd4(passArray).CopyTo(zeroPassHash, 0);

        for (var i = 0; i < 3; i++)
        {
            var key = AddParity(zeroPassHash[(i * 7)..((i + 1) * 7)]);
            var encrypted = Crypto.DesEncrypt(key, challenge);
            encrypted.CopyTo(_clientResponse, i * 8);
        }
    }

    private byte[] ChallengeDigest(byte[] userArray) =>
        Crypto.Sha1(_clientChallenge, _serverChallenge, userArray)[..8];

    private bool CheckServerResponse()
    {
        var userArray = Encoding.ASCII.GetBytes(_bridge.HomeUsername);
        var passArray = Encoding.Unicode.GetBytes(_bridge.HomePassword);

        var magic1 = UnitExtensions.Sum(
            "4D616769632073657276657220746F20",
            "636C69656E74207369676E696E672063",
            "6F6E7374616E74").ToHexByteArray();

        var magic2 = UnitExtensions.Sum(
            "50616420746F206D616B652069742064",
            "6F206D6F7265207468616E206F6E6520",
            "697465726174696F6E").ToHexByteArray();

        var challenge = ChallengeDigest(userArray);

        var inner = Crypto.Sha1(Crypto.HashMd4(Crypto.HashMd4(passArray)), _clientResponse, magic1);
        var digest = Crypto.Sha1(inner, challenge, magic2);

        var expected = Encoding.ASCII.GetBytes("S=" + digest.ToHexString());

        return expected.AsSpan().SequenceEqual(_serverResponse);
    }

    public void PrepareHlak()
    {
        var passArray = Encoding.Unicode.GetBytes(_bridge.HomePassword);

        var magic1 = UnitExtensions.Sum(
            "5468697320697320746865204D505045",
            "204D6173746572204B6579").ToHexByteArray();

        var magic2 = UnitExtensions.Sum(
            "4F6E2074686520636C69656E74207369",
            "64652C20746869732069732074686520",
            "73656E64206B65793B206F6E20746865",
            "2073657276657220736964652C206974",
            "20697320746865207265636569766520",
            "6B65792E").ToHexByteArray();

        var magic3 = UnitExtensions.Sum(
            "4F6E2074686520636C69656E74207369",
            "64652C20746869732069732074686520",
            "72656365697665206B65793B206F6E20",
            "7468652073657276657220736964652C",
            "206974206973207468652073656E6420",
            "6B65792E").ToHexByteArray();

        var pad1 = UnitExtensions.Sum(
            "00000000000000000000000000000000",
            "00000000000000000000000000000000",
            "0000000000000000").ToHexByteArray();

        var pad2 = UnitExtensions.Sum(
            "F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2",
            "F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2",
            "F2F2F2F2F2F2F2F2").ToHexByteArray();

        var masterKey = Crypto.Sha1(Crypto.HashMd4(Crypto.HashMd4(passArray)), _clientResponse, magic1)[..16];

        var hlak = new byte[32];
        Crypto.Sha1(masterKey, pad1, magic2, pad2).AsSpan(0, 16).CopyTo(hlak.AsSpan(0, 16));
        Crypto.Sha1(masterKey, pad1, magic3, pad2).AsSpan(0, 16).CopyTo(hlak.AsSpan(16, 16));

        _bridge.Hlak = hlak;
    }
}

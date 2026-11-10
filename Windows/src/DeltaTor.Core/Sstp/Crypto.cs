using System.Security.Cryptography;

namespace DeltaTor.Core.Sstp;

/// <summary>
/// The cryptographic primitives MSCHAPv2 and the SSTP compound MAC need:
/// MD4 (absent from .NET, so ported from kittoku/osc/cipher/hash.kt) and a raw
/// single-block DES-ECB helper used to build the NT response.
/// </summary>
internal static class Crypto
{
    /// <summary>
    /// Direct port of <c>hashMd4</c>: standard MD4 (the Kotlin implementation's
    /// "reversed" constants and little-endian length are just the RFC 1320
    /// little-endian layout spelled out). Kept as an explicit round-by-round
    /// transcription so the output is bit-identical to the Android build.
    /// </summary>
    public static byte[] HashMd4(byte[] input)
    {
        var inputLength = input.Length;
        var remainder = inputLength % 64;
        var padLength = remainder >= 56 ? 64 - (remainder - 56) : 56 - remainder;
        var bytesLength = inputLength + padLength + 8;

        var bytes = new byte[bytesLength];
        System.Array.Copy(input, bytes, inputLength);
        bytes[inputLength] = 0x80;
        // Length in bits, little-endian, in the final 8 bytes.
        var bitLength = (ulong)inputLength * 8;
        for (var i = 0; i < 8; i++)
        {
            bytes[bytesLength - 8 + i] = (byte)(bitLength >> (8 * i));
        }

        uint a = 0x67452301;
        uint b = 0xEFCDAB89;
        uint c = 0x98BADCFE;
        uint d = 0x10325476;

        var x = new uint[16];

        for (var block = 0; block < bytesLength / 64; block++)
        {
            var offset = block * 64;
            for (var i = 0; i < 16; i++)
            {
                x[i] = (uint)(bytes[offset + i * 4]
                    | (bytes[offset + i * 4 + 1] << 8)
                    | (bytes[offset + i * 4 + 2] << 16)
                    | (bytes[offset + i * 4 + 3] << 24));
            }

            var aa = a;
            var bb = b;
            var cc = c;
            var dd = d;

            // round 1: F(x,y,z) = (x & y) | (~x & z)
            Round1(ref a, b, c, d, x[0], 3);
            Round1(ref d, a, b, c, x[1], 7);
            Round1(ref c, d, a, b, x[2], 11);
            Round1(ref b, c, d, a, x[3], 19);
            Round1(ref a, b, c, d, x[4], 3);
            Round1(ref d, a, b, c, x[5], 7);
            Round1(ref c, d, a, b, x[6], 11);
            Round1(ref b, c, d, a, x[7], 19);
            Round1(ref a, b, c, d, x[8], 3);
            Round1(ref d, a, b, c, x[9], 7);
            Round1(ref c, d, a, b, x[10], 11);
            Round1(ref b, c, d, a, x[11], 19);
            Round1(ref a, b, c, d, x[12], 3);
            Round1(ref d, a, b, c, x[13], 7);
            Round1(ref c, d, a, b, x[14], 11);
            Round1(ref b, c, d, a, x[15], 19);

            // round 2: G(x,y,z) = (x & y) | (x & z) | (y & z) + 0x5A827999
            Round2(ref a, b, c, d, x[0], 3);
            Round2(ref d, a, b, c, x[4], 5);
            Round2(ref c, d, a, b, x[8], 9);
            Round2(ref b, c, d, a, x[12], 13);
            Round2(ref a, b, c, d, x[1], 3);
            Round2(ref d, a, b, c, x[5], 5);
            Round2(ref c, d, a, b, x[9], 9);
            Round2(ref b, c, d, a, x[13], 13);
            Round2(ref a, b, c, d, x[2], 3);
            Round2(ref d, a, b, c, x[6], 5);
            Round2(ref c, d, a, b, x[10], 9);
            Round2(ref b, c, d, a, x[14], 13);
            Round2(ref a, b, c, d, x[3], 3);
            Round2(ref d, a, b, c, x[7], 5);
            Round2(ref c, d, a, b, x[11], 9);
            Round2(ref b, c, d, a, x[15], 13);

            // round 3: H(x,y,z) = x ^ y ^ z + 0x6ED9EBA1
            Round3(ref a, b, c, d, x[0], 3);
            Round3(ref d, a, b, c, x[8], 9);
            Round3(ref c, d, a, b, x[4], 11);
            Round3(ref b, c, d, a, x[12], 15);
            Round3(ref a, b, c, d, x[2], 3);
            Round3(ref d, a, b, c, x[10], 9);
            Round3(ref c, d, a, b, x[6], 11);
            Round3(ref b, c, d, a, x[14], 15);
            Round3(ref a, b, c, d, x[1], 3);
            Round3(ref d, a, b, c, x[9], 9);
            Round3(ref c, d, a, b, x[5], 11);
            Round3(ref b, c, d, a, x[13], 15);
            Round3(ref a, b, c, d, x[3], 3);
            Round3(ref d, a, b, c, x[11], 9);
            Round3(ref c, d, a, b, x[7], 11);
            Round3(ref b, c, d, a, x[15], 15);

            a = unchecked(a + aa);
            b = unchecked(b + bb);
            c = unchecked(c + cc);
            d = unchecked(d + dd);
        }

        var output = new byte[16];
        WriteLittleEndian(output, 0, a);
        WriteLittleEndian(output, 4, b);
        WriteLittleEndian(output, 8, c);
        WriteLittleEndian(output, 12, d);
        return output;
    }

    private static uint Rotl(uint x, int s) => (x << s) | (x >> (32 - s));

    private static void Round1(ref uint a, uint b, uint c, uint d, uint xk, int s) =>
        a = Rotl(unchecked(a + ((b & c) | (~b & d)) + xk), s);

    private static void Round2(ref uint a, uint b, uint c, uint d, uint xk, int s) =>
        a = Rotl(unchecked(a + ((b & c) | (b & d) | (c & d)) + xk + 0x5A827999), s);

    private static void Round3(ref uint a, uint b, uint c, uint d, uint xk, int s) =>
        a = Rotl(unchecked(a + (b ^ c ^ d) + xk + 0x6ED9EBA1), s);

    private static void WriteLittleEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// Encrypt one 8-byte block with raw DES/ECB/NoPadding — exactly what the
    /// Kotlin client does three times with consecutive 7-byte key slices.
    /// </summary>
    public static byte[] DesEncrypt(byte[] key, byte[] data)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;

        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public static byte[] Sha1(params byte[][] parts)
    {
        using var sha = SHA1.Create();
        foreach (var part in parts)
        {
            sha.TransformBlock(part, 0, part.Length, null, 0);
        }

        sha.TransformFinalBlock(System.Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    public static byte[] Hmac(byte[] key, string algorithm, byte[] data)
    {
        using HMAC mac = algorithm switch
        {
            "HmacSHA1" => new HMACSHA1(key),
            "HmacSHA256" => new HMACSHA256(key),
            _ => throw new NotSupportedException(algorithm)
        };

        return mac.ComputeHash(data);
    }

    public static byte[] RandomBytes(int count)
    {
        var buffer = new byte[count];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    public static byte[] Digest(string algorithm, byte[] data)
    {
        return algorithm switch
        {
            "SHA-1" => SHA1.HashData(data),
            "SHA-256" => SHA256.HashData(data),
            _ => throw new NotSupportedException(algorithm)
        };
    }
}

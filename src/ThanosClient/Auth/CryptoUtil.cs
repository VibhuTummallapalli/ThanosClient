using System.Security.Cryptography;
using System.Text;

namespace ThanosClient.Auth;

public static class CryptoUtil
{
    /// <summary>Fresh 16-byte AES shared secret for a login handshake.</summary>
    public static byte[] GenerateSharedSecret()
    {
        byte[] secret = new byte[16];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    /// <summary>
    /// Minecraft's server id digest: SHA-1 over serverId + sharedSecret + publicKey,
    /// rendered as a signed two's-complement hex string. Notably it can be negative,
    /// which is why this is not just a plain hex digest.
    /// </summary>
    public static string ServerHash(string serverId, byte[] sharedSecret, byte[] publicKey)
    {
        byte[] input = new byte[Encoding.ASCII.GetByteCount(serverId) + sharedSecret.Length + publicKey.Length];
        int offset = Encoding.ASCII.GetBytes(serverId, 0, serverId.Length, input, 0);
        Buffer.BlockCopy(sharedSecret, 0, input, offset, sharedSecret.Length);
        offset += sharedSecret.Length;
        Buffer.BlockCopy(publicKey, 0, input, offset, publicKey.Length);

        byte[] hash = SHA1.HashData(input);
        bool negative = (hash[0] & 0x80) != 0;
        if (negative) TwosComplement(hash);

        string hex = Convert.ToHexString(hash).ToLowerInvariant().TrimStart('0');
        if (hex.Length == 0) hex = "0";
        return negative ? "-" + hex : hex;
    }

    private static void TwosComplement(byte[] data)
    {
        bool carry = true;
        for (int i = data.Length - 1; i >= 0; i--)
        {
            data[i] = unchecked((byte)~data[i]);
            if (carry)
            {
                carry = data[i] == 0xFF;
                data[i]++;
            }
        }
    }

    /// <summary>RSA/PKCS#1 v1.5 encryption against the server's X.509 SubjectPublicKeyInfo key.</summary>
    public static byte[] RsaEncrypt(byte[] publicKeyDer, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
        return rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    /// <summary>Offline-mode UUID: MD5 of "OfflinePlayer:name", version 3 name-based.</summary>
    public static Guid OfflineUuid(string username)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}

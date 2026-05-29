using System;
using System.Security.Cryptography;

namespace FileLocker;

internal static class KdfSecretMaterial
{
    internal static byte[] Build(byte[] passwordBytes, byte[]? keyfileBytes, out byte[]? combinedSecret)
    {
        ArgumentNullException.ThrowIfNull(passwordBytes);

        combinedSecret = null;
        if (keyfileBytes is not { Length: > 0 })
        {
            return passwordBytes;
        }

        combinedSecret = new byte[passwordBytes.Length + keyfileBytes.Length];
        Buffer.BlockCopy(passwordBytes, 0, combinedSecret, 0, passwordBytes.Length);
        Buffer.BlockCopy(keyfileBytes, 0, combinedSecret, passwordBytes.Length, keyfileBytes.Length);
        return SHA256.HashData(combinedSecret);
    }
}

using System;
using System.Text;

namespace FileLocker;

internal static class KdfSecretValidator
{
    internal const int MaxSecretTextBytes = 1024 * 1024;

    internal static void Validate(string? secretText, string description)
    {
        if (string.IsNullOrEmpty(secretText))
        {
            return;
        }

        if (secretText.Length > MaxSecretTextBytes)
        {
            throw new ArgumentException($"{description} is too long.");
        }

        if (Encoding.UTF8.GetByteCount(secretText) > MaxSecretTextBytes)
        {
            throw new ArgumentException($"{description} is too long.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FileLocker;

internal static class HashInputNormalizer
{
    private const int MaxInputChars = 8 * 1024;
    private const int MaxSuffixCandidateChars = 256;

    internal static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (input.Length > MaxInputChars)
        {
            return string.Empty;
        }

        foreach (string candidate in EnumerateHexRuns(input))
        {
            if (IsSupportedHash(candidate))
            {
                return candidate.ToLowerInvariant();
            }
        }

        string compact = RemoveSeparators(input);
        if (IsSupportedHash(compact))
        {
            return compact.ToLowerInvariant();
        }

        foreach (string candidate in EnumerateSeparatedHexGroups(input))
        {
            if (TryGetSupportedHashSuffix(candidate, out string hash))
            {
                return hash.ToLowerInvariant();
            }
        }

        return RemoveWhitespace(input).ToLowerInvariant();
    }

    internal static bool IsSupportedHash(string hashHex)
    {
        return FileHashService.IsSupportedHexLength(hashHex.Length) && hashHex.All(IsHexDigit);
    }

    internal static bool IsHashForAlgorithm(string hashHex, string? algorithm)
    {
        return FileHashService.IsExpectedHexLength(algorithm, hashHex.Length) && hashHex.All(IsHexDigit);
    }

    internal static bool TryNormalizeSupportedHash(string? input, out string hash)
    {
        hash = Normalize(input);
        return IsSupportedHash(hash);
    }

    private static string RemoveSeparators(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (char character in input)
        {
            if (char.IsWhiteSpace(character) || character is '-' or ':')
            {
                continue;
            }

            if (!IsHexDigit(character))
            {
                return string.Empty;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string RemoveWhitespace(string input) =>
        new(input
            .Where(character =>
                !char.IsWhiteSpace(character) &&
                !char.IsControl(character) &&
                CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)
            .ToArray());

    private static IEnumerable<string> EnumerateHexRuns(string input)
    {
        var builder = new StringBuilder();
        foreach (char character in input)
        {
            if (IsHexDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static IEnumerable<string> EnumerateSeparatedHexGroups(string input)
    {
        var builder = new StringBuilder();
        foreach (char character in input)
        {
            if (IsHexDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (character is ':' or '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool TryGetSupportedHashSuffix(string candidate, out string hash)
    {
        if (IsSupportedHash(candidate))
        {
            hash = candidate;
            return true;
        }

        if (candidate.Length > MaxSuffixCandidateChars)
        {
            hash = string.Empty;
            return false;
        }

        if (candidate.Length > 128)
        {
            string sha512 = candidate[^128..];
            if (IsSupportedHash(sha512))
            {
                hash = sha512;
                return true;
            }
        }

        if (candidate.Length > 64)
        {
            string sha256 = candidate[^64..];
            if (IsSupportedHash(sha256))
            {
                hash = sha256;
                return true;
            }
        }

        hash = string.Empty;
        return false;
    }

    private static bool IsHexDigit(char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F');
}

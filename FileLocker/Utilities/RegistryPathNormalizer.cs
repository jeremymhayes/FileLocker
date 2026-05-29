using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FileLocker;

internal static class RegistryPathNormalizer
{
    internal const int MaxRegistryPathChars = 32_767;

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxRegistryPathChars)
        {
            return null;
        }

        if (!TryExpandEnvironmentVariables(trimmed, out string expanded))
        {
            return null;
        }

        string normalized = expanded.Trim('"').Trim();
        return normalized.Length is 0 or > MaxRegistryPathChars ? null : normalized;
    }

    internal static bool TryExpandEnvironmentVariables(string value, out string expanded)
    {
        expanded = string.Empty;
        if (ContainsInvalidFormatting(value))
        {
            return false;
        }

        foreach (string variableName in EnumerateEnvironmentVariableNames(value))
        {
            string? variableValue = Environment.GetEnvironmentVariable(variableName);
            if (variableValue != null && ContainsInvalidFormatting(variableValue))
            {
                return false;
            }
        }

        expanded = Environment.ExpandEnvironmentVariables(value);
        return !ContainsInvalidFormatting(expanded);
    }

    private static bool ContainsInvalidFormatting(string value)
    {
        return value.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }

    private static IEnumerable<string> EnumerateEnvironmentVariableNames(string value)
    {
        int searchIndex = 0;
        while (searchIndex < value.Length)
        {
            int start = value.IndexOf('%', searchIndex);
            if (start < 0 || start == value.Length - 1)
            {
                yield break;
            }

            int end = value.IndexOf('%', start + 1);
            if (end < 0)
            {
                yield break;
            }

            if (end > start + 1)
            {
                yield return value[(start + 1)..end];
            }

            searchIndex = end + 1;
        }
    }
}

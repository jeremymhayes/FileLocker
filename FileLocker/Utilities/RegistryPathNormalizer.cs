using System;

namespace FileLocker;

internal static class RegistryPathNormalizer
{
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return expanded.Trim('"').Trim();
    }
}

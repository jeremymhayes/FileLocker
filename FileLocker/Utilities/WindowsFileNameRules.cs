using System;
using System.IO;

namespace FileLocker;

internal static class WindowsFileNameRules
{
    internal static bool IsReservedDeviceName(string? fileNameOrStem)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrStem))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileNameOrStem).TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            IsReservedDeviceSeries(stem, "COM") ||
            IsReservedDeviceSeries(stem, "LPT");
    }

    private static bool IsReservedDeviceSeries(string stem, string prefix)
    {
        return stem.Length == prefix.Length + 1 &&
            stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            IsReservedDeviceSeriesSuffix(stem[^1]);
    }

    private static bool IsReservedDeviceSeriesSuffix(char suffix)
    {
        return suffix is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';
    }
}

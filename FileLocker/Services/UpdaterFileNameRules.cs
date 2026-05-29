using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FileLocker;

internal static class UpdaterFileNameRules
{
    private const string InstallerFileNamePrefix = "FileLocker-Setup";
    private const int MaxUpdaterFileNameChars = 255;

    internal static bool IsOwnedInstallerFileName(string? fileName)
    {
        return HasOwnedInstallerPrefix(fileName) &&
            fileName!.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOwnedDownloadFileName(string? fileName)
    {
        return HasOwnedInstallerPrefix(fileName) &&
            fileName!.Trim().EndsWith(".download", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOwnedInstallerPrefix(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string trimmed = fileName.Trim();
        if (trimmed.Length > MaxUpdaterFileNameChars ||
            trimmed is "." or ".." ||
            trimmed.Any(character => CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
            trimmed.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (!trimmed.StartsWith(InstallerFileNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length == InstallerFileNamePrefix.Length)
        {
            return false;
        }

        return trimmed[InstallerFileNamePrefix.Length] is '.' or '-' or '_';
    }
}

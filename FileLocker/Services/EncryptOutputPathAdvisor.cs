using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FileLocker;

internal static class EncryptOutputPathAdvisor
{
    private const string MultiFolderOutputName = "FileLocker Encrypted";

    internal static string? SuggestForSelectedPaths(IEnumerable<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);

        List<string> folderRoots = [];
        foreach (string? path in selectedPaths)
        {
            if (!TryNormalizeDirectoryPath(path, out string normalizedPath) ||
                !Directory.Exists(normalizedPath))
            {
                continue;
            }

            folderRoots.Add(normalizedPath);
        }

        return SuggestForFolderRoots(folderRoots);
    }

    internal static string? SuggestForFolderRoots(IEnumerable<string> folderRoots)
    {
        ArgumentNullException.ThrowIfNull(folderRoots);

        List<string> roots = [];
        foreach (string? path in folderRoots)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (TryNormalizeDirectoryPath(path, out string normalizedPath))
            {
                roots.Add(normalizedPath);
            }
        }

        roots = roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            return null;
        }

        if (roots.Count == 1)
        {
            string root = roots[0];
            string? parentDirectory = Path.GetDirectoryName(root);
            string folderName = Path.GetFileName(root);
            if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(folderName))
            {
                return null;
            }

            return Path.Combine(parentDirectory, $"{folderName} (Encrypted)");
        }

        List<string?> parentDirectories = roots
            .Select(Path.GetDirectoryName)
            .ToList();
        if (parentDirectories.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        List<string> distinctParentDirectories = parentDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        if (distinctParentDirectories.Count != 1)
        {
            return null;
        }

        return Path.Combine(distinctParentDirectories[0], MultiFolderOutputName);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim());
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryNormalizeDirectoryPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || ContainsControlOrFormatCharacter(path))
        {
            return false;
        }

        try
        {
            string trimmedPath = path.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            normalizedPath = NormalizeDirectoryPath(trimmedPath);
            if (ContainsAlternateDataStreamToken(normalizedPath))
            {
                normalizedPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool ContainsControlOrFormatCharacter(string value)
    {
        return value.Any(character =>
            char.IsControl(character) ||
            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }

    private static bool ContainsAlternateDataStreamToken(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        string pathWithoutRoot = path.Length > root.Length ? path[root.Length..] : string.Empty;
        return pathWithoutRoot.Contains(':', StringComparison.Ordinal);
    }
}

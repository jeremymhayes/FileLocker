using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileLocker;

internal static class EncryptOutputPathAdvisor
{
    private const string MultiFolderOutputName = "FileLocker Encrypted";

    internal static string? SuggestForSelectedPaths(IEnumerable<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);

        List<string> folderRoots = selectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Where(Directory.Exists)
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return SuggestForFolderRoots(folderRoots);
    }

    internal static string? SuggestForFolderRoots(IEnumerable<string> folderRoots)
    {
        ArgumentNullException.ThrowIfNull(folderRoots);

        List<string> roots = folderRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectoryPath)
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

        List<string> parentDirectories = roots
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        if (parentDirectories.Count != 1)
        {
            return null;
        }

        return Path.Combine(parentDirectories[0], MultiFolderOutputName);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}

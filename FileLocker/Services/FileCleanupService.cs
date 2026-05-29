using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FileLocker;

internal static class FileCleanupService
{
    private const int MaxTemporaryCleanupScanFiles = 50_000;

    internal static FileCleanupSummary DeleteTemporaryFilesUnderDirectory(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) ||
            !IsNormalPath(rootPath, requireLeafName: false) ||
            !Directory.Exists(rootPath))
        {
            return new FileCleanupSummary(0, 0);
        }

        if (IsReparsePoint(rootPath))
        {
            return new FileCleanupSummary(0, 1);
        }

        int deletedFiles = 0;
        int failedFiles = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            int inspectedFiles = 0;
            foreach (string file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                if (inspectedFiles >= MaxTemporaryCleanupScanFiles)
                {
                    failedFiles++;
                    break;
                }

                inspectedFiles++;
                if (!IsTemporaryCleanupCandidate(file))
                {
                    continue;
                }

                try
                {
                    DeleteFileWithoutFollowingReparsePoint(file);
                    deletedFiles++;
                }
                catch
                {
                    failedFiles++;
                }
            }
        }
        catch
        {
            failedFiles++;
        }

        return new FileCleanupSummary(deletedFiles, failedFiles);
    }

    internal static IReadOnlyList<string> DeleteTemporaryFiles(params string?[] paths)
    {
        var failures = new List<string>();
        if (paths is not { Length: > 0 })
        {
            return failures;
        }

        foreach (string? path in paths)
        {
            if (!TryDeleteTemporaryFile(path, out string? failure))
            {
                failures.Add(failure ?? "Unknown cleanup failure.");
            }
        }

        return failures;
    }

    internal static bool TryDeleteTemporaryFile(string? path, out string? failure)
    {
        return TryDeleteFile(path, out failure);
    }

    internal static bool TryDeleteFile(string? path, out string? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        if (!IsNormalPath(path, requireLeafName: true))
        {
            failure = "Invalid cleanup path.";
            return false;
        }

        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            DeleteFileWithoutFollowingReparsePoint(path);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"{Path.GetFileName(path)}: {SensitiveDataRedactor.RedactMessage(ex.Message)}";
            return false;
        }
    }

    private static void DeleteFileWithoutFollowingReparsePoint(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException("File cleanup does not delete file reparse points.");
        }

        ClearReadOnlyAttribute(path);
        File.Delete(path);
    }

    private static bool IsTemporaryCleanupCandidate(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".download", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = Path.GetFileName(path);
        return UpdaterFileNameRules.IsOwnedInstallerFileName(fileName);
    }

    internal static void ClearReadOnlyAttribute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsNormalPath(path, requireLeafName: true))
        {
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsControlOrFormatCharacter(string value)
    {
        return value.Any(character =>
            char.IsControl(character) ||
            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }

    private static bool IsNormalPath(string path, bool requireLeafName)
    {
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

            string fullPath = Path.GetFullPath(trimmedPath);
            if (requireLeafName && string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            {
                return false;
            }

            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            return !pathWithoutRoot.Contains(':', StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed record FileCleanupSummary(int DeletedFiles, int FailedFiles);

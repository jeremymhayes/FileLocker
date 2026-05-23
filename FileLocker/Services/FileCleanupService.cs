using System;
using System.Collections.Generic;
using System.IO;

namespace FileLocker;

internal static class FileCleanupService
{
    internal static FileCleanupSummary DeleteTemporaryFilesUnderDirectory(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
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
            foreach (string file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                if (!IsTemporaryCleanupCandidate(file))
                {
                    continue;
                }

                try
                {
                    DeleteTemporaryFile(file);
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
        failure = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return true;
        }

        try
        {
            DeleteTemporaryFile(path);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"{Path.GetFileName(path)}: {SensitiveDataRedactor.RedactMessage(ex.Message)}";
            return false;
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException("Temporary cleanup does not delete file reparse points.");
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
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            fileName.StartsWith("FileLocker-Setup", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ClearReadOnlyAttribute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
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
}

internal sealed record FileCleanupSummary(int DeletedFiles, int FailedFiles);

using System;
using System.Collections.Generic;
using System.IO;

namespace FileLocker;

internal static class FileCleanupService
{
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
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"{Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }
}

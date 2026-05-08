using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class HashManifestService
{
    internal static async Task<HashManifestResult> CreateManifestAsync(
        IEnumerable<string> inputPaths,
        string algorithm,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        string[] inputs = inputPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] files = ExpandFiles(inputPaths).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("No files were available for the hash manifest.");
        }

        Directory.CreateDirectory(outputDirectory);
        string normalizedAlgorithm = NormalizeAlgorithm(algorithm);
        string extension = normalizedAlgorithm == "SHA-512" ? ".sha512" : ".sha256";
        string manifestPath = Path.Combine(outputDirectory, $"FileLocker-manifest-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}");
        string[] inputRoots = ResolveInputRoots(inputs).ToArray();
        string commonRoot = inputRoots.Length > 0
            ? FindCommonDirectory(inputRoots)
            : FindCommonRoot(files);
        IReadOnlyDictionary<string, string> hashes = await FileHashService.ComputeHashesHexAsync(files, normalizedAlgorithm, cancellationToken: cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"# FileLocker {normalizedAlgorithm} manifest");
        builder.AppendLine($"# GeneratedUtc: {DateTime.UtcNow:O}");
        builder.AppendLine($"# Paths are relative to: {SensitiveDataRedactor.RedactPath(commonRoot)}");

        foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = GetManifestRelativePath(commonRoot, file);
            builder.Append(hashes[file]);
            builder.Append("  ");
            builder.AppendLine(EscapeManifestPath(relativePath));
        }

        await File.WriteAllTextAsync(manifestPath, builder.ToString(), Encoding.UTF8, cancellationToken);
        return new HashManifestResult(manifestPath, Path.GetFileName(manifestPath), normalizedAlgorithm, files.Length);
    }

    internal static async Task<HashManifestVerificationResult> VerifyManifestAsync(
        string manifestPath,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Hash manifest was not found.", manifestPath);
        }

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException("The manifest root folder was not found.");
        }

        string algorithm = Path.GetExtension(manifestPath).Equals(".sha512", StringComparison.OrdinalIgnoreCase)
            ? "SHA-512"
            : "SHA-256";
        List<HashManifestEntry> entries = ParseManifest(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        int matched = 0;
        int mismatched = 0;
        int missing = 0;

        foreach (HashManifestEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = Path.GetFullPath(Path.Combine(rootDirectory, entry.RelativePath));
            if (!File.Exists(filePath))
            {
                missing++;
                continue;
            }

            string actual = await FileHashService.ComputeHashHexAsync(filePath, algorithm, cancellationToken: cancellationToken);
            if (string.Equals(actual, entry.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
            else
            {
                mismatched++;
            }
        }

        return new HashManifestVerificationResult(manifestPath, entries.Count, matched, mismatched, missing);
    }

    internal static List<HashManifestEntry> ParseManifest(string content)
    {
        var entries = new List<HashManifestEntry>();
        foreach (string rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            string relativePath = UnescapeManifestPath(parts[1].TrimStart('*'));
            if (relativePath.Length == 0 || Path.IsPathFullyQualified(relativePath))
            {
                continue;
            }

            entries.Add(new HashManifestEntry(parts[0], relativePath));
        }

        return entries;
    }

    private static IEnumerable<string> ExpandFiles(IEnumerable<string> inputPaths)
    {
        foreach (string rawPath in inputPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.GetFullPath(rawPath);
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> ResolveInputRoots(IEnumerable<string> inputPaths)
    {
        foreach (string path in inputPaths)
        {
            if (Directory.Exists(path))
            {
                yield return path;
            }
            else if (File.Exists(path))
            {
                yield return Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            }
        }
    }

    private static string NormalizeAlgorithm(string algorithm) =>
        algorithm.Contains("512", StringComparison.OrdinalIgnoreCase) ? "SHA-512" : "SHA-256";

    private static string FindCommonRoot(IReadOnlyList<string> files)
    {
        string[] directories = files
            .Select(path => Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory())
            .ToArray();
        return FindCommonDirectory(directories);
    }

    private static string FindCommonDirectory(IReadOnlyList<string> directories)
    {
        string common = directories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (string directory in directories.Skip(1))
        {
            string candidate = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!IsSameDirectoryOrChild(candidate, common))
            {
                string? parent = Path.GetDirectoryName(common);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, common, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetPathRoot(common) ?? common;
                }

                common = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        return common;
    }

    private static bool IsSameDirectoryOrChild(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetManifestRelativePath(string commonRoot, string filePath)
    {
        string relative = Path.GetRelativePath(commonRoot, filePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string EscapeManifestPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string UnescapeManifestPath(string path) =>
        path.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}

internal sealed record HashManifestResult(
    string ManifestPath,
    string FileName,
    string Algorithm,
    int FileCount);

internal sealed record HashManifestVerificationResult(
    string ManifestPath,
    int EntryCount,
    int MatchedCount,
    int MismatchedCount,
    int MissingCount);

internal sealed record HashManifestEntry(
    string HashHex,
    string RelativePath);

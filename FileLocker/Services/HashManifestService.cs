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
        IEnumerable<string>? inputPaths,
        string? algorithm,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedAlgorithm = NormalizeAlgorithm(algorithm);
        string[] inputs = NormalizeManifestInputPaths(inputPaths, cancellationToken);
        string[] files = ExpandFiles(inputs, cancellationToken).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("No files were available for the hash manifest.");
        }

        Directory.CreateDirectory(outputDirectory);
        string extension = normalizedAlgorithm == "SHA-512" ? ".sha512" : ".sha256";
        string manifestPath = ResolveAvailableManifestPath(Path.Combine(outputDirectory, $"FileLocker-manifest-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}"));
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

        await WriteAllTextAtomicallyAsync(manifestPath, builder.ToString(), Encoding.UTF8, cancellationToken);
        return new HashManifestResult(manifestPath, Path.GetFileName(manifestPath), normalizedAlgorithm, files.Length);
    }

    internal static async Task<HashManifestVerificationResult> VerifyManifestAsync(
        string manifestPath,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();

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
        int entryCount = 0;
        int matched = 0;
        int mismatched = 0;
        int missing = 0;

        await foreach (string rawLine in File.ReadLinesAsync(manifestPath, Encoding.UTF8, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseManifestEntry(rawLine, out HashManifestEntry? entry) || entry is null)
            {
                continue;
            }

            entryCount++;
            if (!TryResolveManifestEntryPath(rootDirectory, entry.RelativePath, out string filePath) ||
                !File.Exists(filePath))
            {
                missing++;
                continue;
            }

            string? actual = await TryComputeManifestEntryHashAsync(filePath, algorithm, cancellationToken);
            if (actual is null)
            {
                missing++;
                continue;
            }

            if (string.Equals(actual, entry.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
            else
            {
                mismatched++;
            }
        }

        if (entryCount == 0)
        {
            throw new InvalidOperationException("The hash manifest does not contain any verifiable file entries.");
        }

        return new HashManifestVerificationResult(manifestPath, entryCount, matched, mismatched, missing);
    }

    internal static List<HashManifestEntry> ParseManifest(string content)
    {
        var entries = new List<HashManifestEntry>();
        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            if (TryParseManifestEntry(rawLine, out HashManifestEntry? entry) && entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    internal static IEnumerable<string> ExpandFiles(IEnumerable<string> inputPaths, CancellationToken cancellationToken = default)
    {
        foreach (string rawPath in inputPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetFullPath(rawPath, out string path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in EnumerateManifestFiles(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsGeneratedManifestFile(file))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private static string[] NormalizeManifestInputPaths(IEnumerable<string> inputPaths, CancellationToken cancellationToken)
    {
        var inputs = new List<string>();
        foreach (string? path in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (TryGetFullPath(path, out string fullPath))
            {
                inputs.Add(fullPath);
            }
        }

        return inputs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    internal static bool IsGeneratedManifestFile(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.StartsWith("FileLocker-manifest-", StringComparison.OrdinalIgnoreCase) &&
            (fileName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase));
    }

    internal static EnumerationOptions CreateManifestEnumerationOptions()
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
    }

    private static IEnumerable<string> EnumerateManifestFiles(string rootPath)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory
                .EnumerateFiles(rootPath, "*", CreateManifestEnumerationOptions())
                .GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch
                {
                    yield break;
                }

                yield return current;
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

    private static string NormalizeAlgorithm(string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return "SHA-256";
        }

        string normalized = algorithm.Trim();
        if (normalized.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
        {
            return "SHA-256";
        }

        if (normalized.Equals("SHA-512", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA512", StringComparison.OrdinalIgnoreCase))
        {
            return "SHA-512";
        }

        throw new ArgumentException("Unsupported hash algorithm. Use SHA-256 or SHA-512.", nameof(algorithm));
    }

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

    internal static string GetManifestRelativePath(string commonRoot, string filePath)
    {
        string relative = Path.GetRelativePath(commonRoot, filePath);
        if (!IsSafeManifestRelativePath(relative))
        {
            throw new InvalidOperationException("Hash manifest inputs must share a common root.");
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string EscapeManifestPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool TryParseManifestEntry(string rawLine, out HashManifestEntry? entry)
    {
        entry = null;
        if (!TryParseManifestLine(rawLine, out string hashHex, out string escapedPath))
        {
            return false;
        }

        if (!IsSupportedManifestHash(hashHex))
        {
            return false;
        }

        string relativePath = UnescapeManifestPath(escapedPath);
        if (!IsSafeManifestRelativePath(relativePath))
        {
            return false;
        }

        entry = new HashManifestEntry(hashHex, relativePath);
        return true;
    }

    private static bool TryParseManifestLine(string rawLine, out string hashHex, out string escapedPath)
    {
        hashHex = string.Empty;
        escapedPath = string.Empty;

        string line = rawLine.TrimStart();
        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        int hashEnd = line.IndexOfAny([' ', '\t']);
        if (hashEnd <= 0)
        {
            return false;
        }

        hashHex = line[..hashEnd];
        string remainder = line[hashEnd..];
        if (remainder.StartsWith("  ", StringComparison.Ordinal))
        {
            escapedPath = remainder[2..];
        }
        else if (remainder.StartsWith(" *", StringComparison.Ordinal))
        {
            escapedPath = remainder[2..];
        }
        else
        {
            escapedPath = remainder.TrimStart(' ', '\t');
            if (escapedPath.StartsWith("*", StringComparison.Ordinal))
            {
                escapedPath = escapedPath[1..];
            }
        }

        return escapedPath.Length > 0;
    }

    private static string UnescapeManifestPath(string path)
    {
        var builder = new StringBuilder(path.Length);
        for (int index = 0; index < path.Length; index++)
        {
            char current = path[index];
            if (current != '\\' || index == path.Length - 1)
            {
                builder.Append(current);
                continue;
            }

            char escaped = path[++index];
            if (escaped == 'n')
            {
                builder.Append('\n');
            }
            else if (escaped == '\\')
            {
                builder.Append('\\');
            }
            else
            {
                builder.Append('\\');
                builder.Append(escaped);
            }
        }

        return builder.ToString();
    }

    private static bool IsSafeManifestRelativePath(string relativePath)
    {
        if (relativePath.Length == 0 ||
            relativePath.Any(char.IsControl) ||
            Path.IsPathFullyQualified(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(segment => segment is not "." and not "..");
    }

    internal static bool IsSupportedManifestHash(string hashHex)
    {
        return HashInputNormalizer.IsSupportedHash(hashHex);
    }

    private static async Task<string?> TryComputeManifestEntryHashAsync(
        string filePath,
        string algorithm,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FileHashService.ComputeHashHexAsync(filePath, algorithm, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteAllTextAtomicallyAsync(
        string path,
        string contents,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        await FileWriteService.WriteAllTextAtomicallyAsync(path, contents, encoding, cancellationToken);
    }

    internal static string ResolveAvailableManifestPath(string path)
    {
        return FileWriteService.ResolveAvailablePath(path);
    }

    internal static bool TryResolveManifestEntryPath(string rootDirectory, string relativePath, out string filePath)
    {
        filePath = string.Empty;
        if (!IsSafeManifestRelativePath(relativePath))
        {
            return false;
        }

        try
        {
            string rootFullPath = Path.GetFullPath(rootDirectory);
            string candidate = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
            if (!IsSameDirectoryOrChild(candidate, rootFullPath))
            {
                return false;
            }

            filePath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

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

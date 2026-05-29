using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class HashManifestService
{
    internal const int MaxManifestLineChars = 64 * 1024;
    internal const int MaxManifestEntries = FileHashService.MaxBatchHashPaths;
    internal const int MaxManifestLines = MaxManifestEntries + 1024;

    internal static async Task<HashManifestResult> CreateManifestAsync(
        IEnumerable<string>? inputPaths,
        string? algorithm,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedOutputDirectory = NormalizeManifestPath(outputDirectory, requireLeafName: false, "Hash manifest output folder is invalid.");
        string normalizedAlgorithm = FileHashService.NormalizeAlgorithmName(algorithm);
        string[] inputs = NormalizeManifestInputPaths(inputPaths, cancellationToken);
        string[] files = NormalizeManifestFiles(ExpandFiles(inputs, cancellationToken), cancellationToken);
        if (files.Length == 0)
        {
            throw new InvalidOperationException("No files were available for the hash manifest.");
        }

        Directory.CreateDirectory(normalizedOutputDirectory);
        string extension = normalizedAlgorithm == FileHashService.Sha512 ? ".sha512" : ".sha256";
        string manifestPath = ResolveAvailableManifestPath(Path.Combine(normalizedOutputDirectory, $"FileLocker-manifest-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}"));
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

        string normalizedManifestPath = NormalizeManifestPath(manifestPath, requireLeafName: true, "Hash manifest path is invalid.");
        string normalizedRootDirectory = NormalizeManifestPath(rootDirectory, requireLeafName: false, "Hash manifest root folder is invalid.");

        if (!File.Exists(normalizedManifestPath))
        {
            throw new FileNotFoundException("Hash manifest was not found.", normalizedManifestPath);
        }

        if (!Directory.Exists(normalizedRootDirectory))
        {
            throw new DirectoryNotFoundException("The manifest root folder was not found.");
        }

        string algorithm = GetManifestAlgorithmFromPath(normalizedManifestPath);
        int entryCount = 0;
        int matched = 0;
        int mismatched = 0;
        int missing = 0;
        int inspectedLines = 0;

        await foreach (string rawLine in File.ReadLinesAsync(normalizedManifestPath, Encoding.UTF8, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspectedLines++;
            if (inspectedLines > MaxManifestLines)
            {
                throw new InvalidDataException("The hash manifest contains too many lines.");
            }

            if (rawLine.Length > MaxManifestLineChars)
            {
                throw new InvalidDataException("The hash manifest contains a line that is too long.");
            }

            if (IsIgnorableManifestLine(rawLine))
            {
                continue;
            }

            if (!TryParseManifestEntry(rawLine, algorithm, out HashManifestEntry? entry) || entry is null)
            {
                throw new InvalidDataException("The hash manifest contains malformed or unsafe entries.");
            }

            entryCount++;
            if (entryCount > MaxManifestEntries)
            {
                throw new InvalidDataException("The hash manifest contains too many entries.");
            }

            if (!TryResolveManifestEntryPath(normalizedRootDirectory, entry.RelativePath, out string filePath) ||
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

        return new HashManifestVerificationResult(normalizedManifestPath, entryCount, matched, mismatched, missing);
    }

    internal static List<HashManifestEntry> ParseManifest(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<HashManifestEntry>();
        using var reader = new StringReader(content);
        string? rawLine;
        int inspectedLines = 0;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            inspectedLines++;
            if (inspectedLines > MaxManifestLines)
            {
                break;
            }

            if (rawLine.Length > MaxManifestLineChars)
            {
                continue;
            }

            if (TryParseManifestEntry(rawLine, out HashManifestEntry? entry) && entry is not null)
            {
                entries.Add(entry);
                if (entries.Count >= MaxManifestEntries)
                {
                    break;
                }
            }
        }

        return entries;
    }

    internal static IEnumerable<string> ExpandFiles(IEnumerable<string> inputPaths, CancellationToken cancellationToken = default)
    {
        foreach (string rawPath in inputPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetNormalFullPath(rawPath, out string path))
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

    internal static string[] NormalizeManifestInputPaths(IEnumerable<string> inputPaths, CancellationToken cancellationToken)
    {
        var inputs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalizedPath = path.Trim();
            if (normalizedPath.Length > FileHashService.MaxHashPathChars)
            {
                throw new InvalidOperationException("A hash manifest input path is too long.");
            }

            if (ContainsControlOrFormatCharacters(normalizedPath))
            {
                throw new InvalidOperationException("A hash manifest input path contains invalid characters.");
            }

            if (!TryGetNormalFullPath(normalizedPath, out string fullPath))
            {
                throw new InvalidOperationException("A hash manifest input path contains invalid characters.");
            }

            if (seen.Add(fullPath))
            {
                if (inputs.Count >= MaxManifestEntries)
                {
                    throw new InvalidOperationException("The hash manifest contains too many input paths.");
                }

                inputs.Add(fullPath);
            }
        }

        return inputs.ToArray();
    }

    internal static string[] NormalizeManifestFiles(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var normalizedFiles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length > FileHashService.MaxHashPathChars)
            {
                throw new InvalidOperationException("A hash manifest file path is too long.");
            }

            if (ContainsControlOrFormatCharacters(file))
            {
                throw new InvalidOperationException("A hash manifest file path contains invalid characters.");
            }

            if (!TryGetNormalFullPath(file, out string fullPath))
            {
                throw new InvalidOperationException("A hash manifest file path contains invalid characters.");
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            if (normalizedFiles.Count >= MaxManifestEntries)
            {
                throw new InvalidOperationException("The hash manifest contains too many files.");
            }

            normalizedFiles.Add(fullPath);
        }

        return normalizedFiles.ToArray();
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        string trimmedPath = path.Trim();
        if (trimmedPath.Length == 0 || ContainsControlOrFormatCharacters(trimmedPath))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            fullPath = Path.GetFullPath(trimmedPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryGetNormalFullPath(string path, out string fullPath)
    {
        if (!TryGetFullPath(path, out fullPath))
        {
            return false;
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        return !pathWithoutRoot.Contains(':', StringComparison.Ordinal);
    }

    private static string NormalizeManifestPath(string path, bool requireLeafName, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(path) || ContainsControlOrFormatCharacters(path))
        {
            throw new InvalidOperationException(errorMessage);
        }

        try
        {
            string trimmedPath = path.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                throw new InvalidOperationException(errorMessage);
            }

            string fullPath = Path.GetFullPath(trimmedPath);
            if (requireLeafName && string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            {
                throw new InvalidOperationException(errorMessage);
            }

            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(errorMessage);
            }

            return fullPath;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(errorMessage, ex);
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

    private static string FindCommonRoot(IReadOnlyList<string> files)
    {
        string[] directories = files
            .Select(path => Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory())
            .ToArray();
        return FindCommonDirectory(directories);
    }

    private static string FindCommonDirectory(IReadOnlyList<string> directories)
    {
        string common = NormalizeManifestDirectory(directories[0]);

        foreach (string directory in directories.Skip(1))
        {
            string candidate = NormalizeManifestDirectory(directory);
            while (!IsSameDirectoryOrChild(candidate, common))
            {
                string? parent = Path.GetDirectoryName(common);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, common, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetPathRoot(common) ?? common;
                }

                common = NormalizeManifestDirectory(parent);
            }
        }

        return common;
    }

    private static string NormalizeManifestDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.IsNullOrWhiteSpace(root) &&
            string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase)
            ? root
            : trimmed;
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
        return TryParseManifestEntry(rawLine, algorithm: null, entry: out entry);
    }

    private static bool TryParseManifestEntry(string rawLine, string? algorithm, out HashManifestEntry? entry)
    {
        entry = null;
        if (!TryParseManifestLine(rawLine, out string hashHex, out string escapedPath))
        {
            return false;
        }

        if (!IsSupportedManifestHash(hashHex, algorithm))
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

    private static bool IsIgnorableManifestLine(string rawLine)
    {
        string line = rawLine.TrimStart();
        return line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal);
    }

    private static bool TryParseManifestLine(string rawLine, out string hashHex, out string escapedPath)
    {
        hashHex = string.Empty;
        escapedPath = string.Empty;

        if (IsIgnorableManifestLine(rawLine))
        {
            return false;
        }

        string line = rawLine.TrimStart();
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
            ContainsControlOrFormatCharacters(relativePath) ||
            Path.IsPathFullyQualified(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(segment => segment is not "." and not ".." && !segment.Contains(':', StringComparison.Ordinal));
    }

    private static bool ContainsControlOrFormatCharacters(string text)
    {
        return text.Any(character =>
            char.IsControl(character) ||
            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }

    internal static bool IsSupportedManifestHash(string hashHex)
    {
        return HashInputNormalizer.IsSupportedHash(hashHex);
    }

    internal static bool IsSupportedManifestHash(string hashHex, string? algorithm)
    {
        return string.IsNullOrWhiteSpace(algorithm)
            ? HashInputNormalizer.IsSupportedHash(hashHex)
            : HashInputNormalizer.IsHashForAlgorithm(hashHex, algorithm);
    }

    internal static string GetManifestAlgorithmFromPath(string manifestPath)
    {
        string extension = Path.GetExtension(manifestPath);
        if (extension.Equals(".sha256", StringComparison.OrdinalIgnoreCase))
        {
            return FileHashService.Sha256;
        }

        if (extension.Equals(".sha512", StringComparison.OrdinalIgnoreCase))
        {
            return FileHashService.Sha512;
        }

        throw new InvalidOperationException("Hash manifests must use a supported .sha256 or .sha512 file extension.");
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

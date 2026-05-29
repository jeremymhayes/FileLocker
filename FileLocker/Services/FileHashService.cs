using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class FileHashService
{
    internal const string Sha256 = "SHA-256";
    internal const string Sha512 = "SHA-512";
    internal const int MaxHashAlgorithmNameChars = 64;
    private const int BufferSize = 131072;
    internal const int MaxBatchHashPaths = 100_000;
    internal const int MaxHashPathChars = 32_767;

    public static async Task<string> ComputeHashHexAsync(
        string filePath,
        string? algorithm,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        string normalizedPath = filePath.Trim();
        ValidateHashPathText(normalizedPath, nameof(filePath), "Hash file path");
        return await ComputeHashHexAsync(normalizedPath, algorithmName, progress, cancellationToken);
    }

    private static async Task<string> ComputeHashHexAsync(
        string filePath,
        HashAlgorithmName algorithmName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using IncrementalHash incrementalHash = IncrementalHash.CreateHash(algorithmName);
        byte[] buffer = new byte[BufferSize];
        try
        {
            long totalLength = Math.Max(1, stream.Length);
            long processed = 0;

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                incrementalHash.AppendData(buffer, 0, read);
                processed += read;
                progress?.Report(Math.Min(100, processed * 100d / totalLength));
            }

            string hash = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
            progress?.Report(100);
            return hash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public static async Task<IReadOnlyDictionary<string, string>> ComputeHashesHexAsync(
        IEnumerable<string> filePaths,
        string? algorithm,
        int maxDegreeOfParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        cancellationToken.ThrowIfCancellationRequested();

        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        string[] paths = NormalizeBatchHashPaths(filePaths, cancellationToken);

        var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism > 0
                ? maxDegreeOfParallelism
                : Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
        };

        await Parallel.ForEachAsync(paths, options, async (path, token) =>
        {
            results[path] = await ComputeHashHexAsync(path, algorithmName, cancellationToken: token);
        });

        return results;
    }

    public static int GetExpectedHexLength(string? algorithm)
    {
        string algorithmName = NormalizeAlgorithmName(algorithm);
        return string.Equals(algorithmName, Sha512, StringComparison.Ordinal) ? 128 : 64;
    }

    public static int GetDigestBits(string? algorithm)
    {
        string algorithmName = NormalizeAlgorithmName(algorithm);
        return string.Equals(algorithmName, Sha512, StringComparison.Ordinal) ? 512 : 256;
    }

    internal static bool IsSupportedHexLength(int length) =>
        length == GetExpectedHexLength(Sha256) ||
        length == GetExpectedHexLength(Sha512);

    internal static bool IsExpectedHexLength(string? algorithm, int length) =>
        length == GetExpectedHexLength(algorithm);

    private static string[] NormalizeBatchHashPaths(IEnumerable<string> filePaths, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Batch hash paths cannot contain blank entries.", nameof(filePaths));
            }

            string normalizedPath = path.Trim();
            if (normalizedPath.Length > MaxHashPathChars)
            {
                throw new ArgumentException("Batch hash paths cannot contain entries longer than the Windows path limit.", nameof(filePaths));
            }

            if (ContainsControlOrFormatCharacters(normalizedPath))
            {
                throw new ArgumentException("Batch hash paths cannot contain control characters or Unicode format characters.", nameof(filePaths));
            }

            ValidateHashPathText(normalizedPath, nameof(filePaths), "Batch hash path");

            if (!seen.Add(normalizedPath))
            {
                continue;
            }

            paths.Add(normalizedPath);
            if (paths.Count > MaxBatchHashPaths)
            {
                throw new ArgumentException("Batch hash path list is too large.", nameof(filePaths));
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateHashPathText(string normalizedPath, string parameterName, string label)
    {
        if (normalizedPath.Length > MaxHashPathChars)
        {
            throw new ArgumentException($"{label} cannot be longer than the Windows path limit.", parameterName);
        }

        if (ContainsControlOrFormatCharacters(normalizedPath))
        {
            throw new ArgumentException($"{label} cannot contain control characters or Unicode format characters.", parameterName);
        }

        try
        {
            if (!Path.IsPathFullyQualified(normalizedPath))
            {
                throw new ArgumentException($"{label} must reference a normal file path.", parameterName);
            }

            string fullPath = Path.GetFullPath(normalizedPath);
            string fileName = Path.GetFileName(fullPath);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;

            if (string.IsNullOrWhiteSpace(fileName) ||
                pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException($"{label} must reference a normal file path.", parameterName);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{label} must reference a normal file path.", parameterName, ex);
        }
    }

    internal static string NormalizeAlgorithmName(string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return Sha256;
        }

        if (algorithm.Length > MaxHashAlgorithmNameChars ||
            ContainsControlOrFormatCharacters(algorithm))
        {
            throw new ArgumentException("Unsupported hash algorithm. Use SHA-256 or SHA-512.", nameof(algorithm));
        }

        string normalized = algorithm.Trim();
        if (normalized.Equals(Sha256, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
        {
            return Sha256;
        }

        if (normalized.Equals(Sha512, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA512", StringComparison.OrdinalIgnoreCase))
        {
            return Sha512;
        }

        throw new ArgumentException("Unsupported hash algorithm. Use SHA-256 or SHA-512.", nameof(algorithm));
    }

    private static HashAlgorithmName NormalizeAlgorithm(string? algorithm)
    {
        string algorithmName = NormalizeAlgorithmName(algorithm);
        return string.Equals(algorithmName, Sha512, StringComparison.Ordinal)
            ? HashAlgorithmName.SHA512
            : HashAlgorithmName.SHA256;
    }

    private static bool ContainsControlOrFormatCharacters(string text)
    {
        return text.Any(character =>
            char.IsControl(character) ||
            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }
}

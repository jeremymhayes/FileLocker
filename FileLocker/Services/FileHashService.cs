using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class FileHashService
{
    private const int BufferSize = 131072;

    public static async Task<string> ComputeHashHexAsync(
        string filePath,
        string? algorithm,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        return await ComputeHashHexAsync(filePath.Trim(), algorithmName, progress, cancellationToken);
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
        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        return algorithmName == HashAlgorithmName.SHA512 ? 128 : 64;
    }

    public static int GetDigestBits(string? algorithm)
    {
        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        return algorithmName == HashAlgorithmName.SHA512 ? 512 : 256;
    }

    private static string[] NormalizeBatchHashPaths(IEnumerable<string> filePaths, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (string? path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Batch hash paths cannot contain blank entries.", nameof(filePaths));
            }

            paths.Add(path.Trim());
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HashAlgorithmName NormalizeAlgorithm(string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return HashAlgorithmName.SHA256;
        }

        string normalized = algorithm.Trim();
        if (normalized.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
        {
            return HashAlgorithmName.SHA256;
        }

        if (normalized.Equals("SHA-512", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SHA512", StringComparison.OrdinalIgnoreCase))
        {
            return HashAlgorithmName.SHA512;
        }

        throw new ArgumentException("Unsupported hash algorithm. Use SHA-256 or SHA-512.", nameof(algorithm));
    }
}

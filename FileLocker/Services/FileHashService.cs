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
        string algorithm,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);

        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using IncrementalHash incrementalHash = IncrementalHash.CreateHash(algorithmName);
        byte[] buffer = new byte[BufferSize];
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

        return Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<IReadOnlyDictionary<string, string>> ComputeHashesHexAsync(
        IEnumerable<string> filePaths,
        string algorithm,
        int maxDegreeOfParallelism = 0,
        CancellationToken cancellationToken = default)
    {
        string[] paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
            results[path] = await ComputeHashHexAsync(path, algorithm, cancellationToken: token);
        });

        return results;
    }

    public static int GetExpectedHexLength(string algorithm)
    {
        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        return algorithmName == HashAlgorithmName.SHA512 ? 128 : 64;
    }

    public static int GetDigestBits(string algorithm)
    {
        HashAlgorithmName algorithmName = NormalizeAlgorithm(algorithm);
        return algorithmName == HashAlgorithmName.SHA512 ? 512 : 256;
    }

    private static HashAlgorithmName NormalizeAlgorithm(string algorithm)
    {
        return algorithm.Contains("512", StringComparison.OrdinalIgnoreCase)
            ? HashAlgorithmName.SHA512
            : HashAlgorithmName.SHA256;
    }
}

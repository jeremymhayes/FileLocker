using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace FileLocker;

internal readonly record struct CompressionPlan(
    bool ShouldCompress,
    string Reason,
    long EstimatedCompressedSize);

internal static class CompressionAdvisor
{
    private const int SampleSizeBytes = 1024 * 1024;
    private const long SmallFileThresholdBytes = 512;
    private const long MinimumSavingsBytes = 4096;
    private const double MinimumSavingsRatio = 0.01;

    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z",
        ".aac",
        ".apk",
        ".avi",
        ".avif",
        ".br",
        ".bz2",
        ".cab",
        ".docm",
        ".docx",
        ".flac",
        ".gif",
        ".gz",
        ".gzip",
        ".heic",
        ".iso",
        ".jar",
        ".jpeg",
        ".jpg",
        ".m4a",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp3",
        ".mp4",
        ".msi",
        ".ogg",
        ".pdf",
        ".png",
        ".pptm",
        ".pptx",
        ".rar",
        ".webm",
        ".webp",
        ".wma",
        ".wmv",
        ".xlsm",
        ".xlsx",
        ".xz",
        ".zip",
        ".zst"
    };

    internal static CompressionPlan CreatePlan(string filePath, long fileSizeBytes, bool compressionRequested)
    {
        if (!compressionRequested)
        {
            return new CompressionPlan(false, "Compression disabled.", fileSizeBytes);
        }

        filePath = NormalizeCompressionFilePath(filePath);

        if (fileSizeBytes <= 0)
        {
            return new CompressionPlan(false, "Empty files are not compressed.", fileSizeBytes);
        }

        if (fileSizeBytes < SmallFileThresholdBytes)
        {
            return new CompressionPlan(false, "File is too small to benefit from compression.", fileSizeBytes);
        }

        if (HasKnownIncompressibleExtension(filePath))
        {
            return new CompressionPlan(false, "File type is already compressed.", fileSizeBytes);
        }

        byte[] sample = ReadCompressionSample(filePath, fileSizeBytes);
        long estimatedCompressedSize;
        long estimatedSavings;
        long requiredSavings;
        try
        {
            estimatedCompressedSize = EstimateCompressedSize(sample, fileSizeBytes);
            estimatedSavings = fileSizeBytes - estimatedCompressedSize;
            requiredSavings = CalculateMinimumUsefulSavings(fileSizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sample);
        }

        if (estimatedSavings < requiredSavings)
        {
            return new CompressionPlan(false, "Sample indicates compression would not save enough space.", estimatedCompressedSize);
        }

        return new CompressionPlan(true, "Sample indicates useful compression savings.", estimatedCompressedSize);
    }

    internal static bool HasKnownIncompressibleExtension(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && IncompressibleExtensions.Contains(extension);
    }

    private static string NormalizeCompressionFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string trimmedPath = filePath.Trim();
        if (trimmedPath.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            throw new ArgumentException("Compression source path contains invalid characters.", nameof(filePath));
        }

        if (!Path.IsPathFullyQualified(trimmedPath))
        {
            throw new ArgumentException("Compression source path must be fully qualified.", nameof(filePath));
        }

        try
        {
            string fullPath = Path.GetFullPath(trimmedPath);
            string fileName = Path.GetFileName(fullPath);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (string.IsNullOrWhiteSpace(fileName) ||
                pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("Compression source path must reference a normal file.", nameof(filePath));
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Compression source path must reference a normal file.", nameof(filePath), ex);
        }
    }

    internal static bool HasUsefulSavings(long originalSizeBytes, long compressedSizeBytes)
    {
        if (originalSizeBytes <= 0 || compressedSizeBytes <= 0)
        {
            return false;
        }

        return originalSizeBytes - compressedSizeBytes >= CalculateMinimumUsefulSavings(originalSizeBytes);
    }

    private static long CalculateMinimumUsefulSavings(long originalSizeBytes)
    {
        if (originalSizeBytes < 64 * 1024)
        {
            return 128;
        }

        long ratioSavings = (long)Math.Ceiling(originalSizeBytes * MinimumSavingsRatio);
        return Math.Min(1024 * 1024, Math.Max(MinimumSavingsBytes, ratioSavings));
    }

    private static byte[] ReadCompressionSample(string filePath, long fileSizeBytes)
    {
        int sampleLength = (int)Math.Min(SampleSizeBytes, fileSizeBytes);
        byte[] sample = new byte[sampleLength];
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int offset = 0;
        while (offset < sample.Length)
        {
            int read = stream.Read(sample, offset, sample.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset == sample.Length)
        {
            return sample;
        }

        Array.Resize(ref sample, offset);
        return sample;
    }

    private static long EstimateCompressedSize(byte[] sample, long fileSizeBytes)
    {
        if (sample.Length == 0)
        {
            return fileSizeBytes;
        }

        long compressedSampleSize = CompressSample(sample);
        if (sample.LongLength >= fileSizeBytes)
        {
            return compressedSampleSize;
        }

        double ratio = compressedSampleSize / (double)sample.LongLength;
        return (long)Math.Ceiling(fileSizeBytes * ratio);
    }

    private static long CompressSample(byte[] sample)
    {
        using var output = new MemoryStream();
        try
        {
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(sample, 0, sample.Length);
            }

            return output.Length;
        }
        finally
        {
            if (output.TryGetBuffer(out ArraySegment<byte> compressedSample))
            {
                CryptographicOperations.ZeroMemory(compressedSample.AsSpan(0, (int)Math.Min(output.Length, compressedSample.Count)));
            }
        }
    }
}

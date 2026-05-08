using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

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
        long estimatedCompressedSize = EstimateCompressedSize(sample, fileSizeBytes);
        long estimatedSavings = fileSizeBytes - estimatedCompressedSize;
        long requiredSavings = CalculateMinimumUsefulSavings(fileSizeBytes);

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
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(sample, 0, sample.Length);
        }

        return output.Length;
    }
}

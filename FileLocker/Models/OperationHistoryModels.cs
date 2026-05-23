using System;
using System.Collections.Generic;
using System.Linq;

namespace FileLocker;

internal sealed class FileOperationResult
{
    public required string SourcePath { get; set; }
    public string? OutputPath { get; set; }
    public string? BackupPath { get; set; }
    public required string Status { get; set; }
    public string? Message { get; set; }
    public bool OriginalRetained { get; set; }
    public bool OutputVerified { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public long? OutputSizeBytes { get; set; }
    public bool CompressionRequested { get; set; }
    public bool CompressionApplied { get; set; }
    public string? CompressionReason { get; set; }
    public long? EstimatedCompressedSizeBytes { get; set; }
    public long? CompressedSizeBytes { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public string? FailureCategory { get; set; }
    public string? HashValue { get; set; }

    public long? NetStorageSavedBytes =>
        CompressionRequested && OriginalSizeBytes.HasValue
            ? GetCompressionPayloadSizeBytes() is long compressedSizeBytes
                ? Math.Max(0, OriginalSizeBytes.Value - compressedSizeBytes)
                : null
            : null;

    public long? NetStorageAddedBytes =>
        CompressionRequested && OriginalSizeBytes.HasValue
            ? GetCompressionPayloadSizeBytes() is long compressedSizeBytes
                ? Math.Max(0, compressedSizeBytes - OriginalSizeBytes.Value)
                : null
            : null;

    private long? GetCompressionPayloadSizeBytes() =>
        CompressedSizeBytes ?? EstimatedCompressedSizeBytes;
}

internal sealed class OperationHistoryEntry
{
    public required string Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public required string Operation { get; set; }
    public required string ProfileName { get; set; }
    public required string Algorithm { get; set; }
    public required string Mode { get; set; }
    public int KeySizeBits { get; set; }
    public bool UsedKeyfile { get; set; }
    public bool RemoveOriginalsAfterSuccess { get; set; }
    public bool SecureDeleteOriginals { get; set; }
    public bool VerifyAfterWrite { get; set; }
    public string BackupFolderPath { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long? TotalOriginalSizeBytes { get; set; }
    public long? TotalOutputSizeBytes { get; set; }
    public long? TotalStorageSavedBytes { get; set; }
    public long? TotalStorageAddedBytes { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public int CompressionRequestedCount { get; set; }
    public int CompressionAppliedCount { get; set; }
    public int CompressionSkippedCount { get; set; }
    public string? FailureCategorySummary { get; set; }
    public List<FileOperationResult> Results { get; set; } = [];
}

internal sealed record OperationMetricsSummary(
    long? TotalOriginalSizeBytes,
    long? TotalOutputSizeBytes,
    long? TotalStorageSavedBytes,
    long? TotalStorageAddedBytes,
    long? ElapsedMilliseconds,
    int CompressionRequestedCount,
    int CompressionAppliedCount,
    int CompressionSkippedCount,
    string? FailureCategorySummary);

internal static class OperationHistoryMetrics
{
    internal static OperationMetricsSummary Calculate(IEnumerable<FileOperationResult>? results)
    {
        FileOperationResult[] resultArray = (results ?? [])
            .Where(result => result is not null)
            .ToArray();

        long? totalOriginal = SumNullable(resultArray.Select(result => result.OriginalSizeBytes));
        long? totalOutput = SumNullable(resultArray.Select(result => result.OutputSizeBytes));
        long? elapsed = SumNullable(resultArray.Select(result => result.ElapsedMilliseconds));
        int compressionRequested = resultArray.Count(result => result.CompressionRequested);
        int compressionApplied = resultArray.Count(result => result.CompressionApplied);
        int compressionSkipped = compressionRequested - compressionApplied;

        string? failureSummary = string.Join(
            ", ",
            resultArray
                .Where(result => string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                .Select(result => string.IsNullOrWhiteSpace(result.FailureCategory) ? "Unknown" : result.FailureCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(failureSummary))
        {
            failureSummary = null;
        }

        long? saved = SumNullable(resultArray.Select(result => result.NetStorageSavedBytes));
        long? added = SumNullable(resultArray.Select(result => result.NetStorageAddedBytes));

        return new OperationMetricsSummary(
            totalOriginal,
            totalOutput,
            saved,
            added,
            elapsed,
            compressionRequested,
            compressionApplied,
            compressionSkipped,
            failureSummary);
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        long total = 0;
        bool hasValue = false;
        foreach (long? value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            total += value.Value;
            hasValue = true;
        }

        return hasValue ? total : null;
    }
}

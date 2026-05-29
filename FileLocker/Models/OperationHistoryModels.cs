using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
    public string? Algorithm { get; set; }
    public int? KeySizeBits { get; set; }

    public long? NetStorageSavedBytes =>
        HasCompressionAttempt && OperationHistorySanitizer.NormalizeNonNegativeMetric(OriginalSizeBytes) is long originalSizeBytes
            ? GetCompressionPayloadSizeBytes() is long compressedSizeBytes
                ? Math.Max(0, originalSizeBytes - compressedSizeBytes)
                : null
            : null;

    public long? NetStorageAddedBytes =>
        HasCompressionAttempt && OperationHistorySanitizer.NormalizeNonNegativeMetric(OriginalSizeBytes) is long originalSizeBytes
            ? GetCompressionPayloadSizeBytes() is long compressedSizeBytes
                ? Math.Max(0, compressedSizeBytes - originalSizeBytes)
                : null
            : null;

    private bool HasCompressionAttempt =>
        CompressionRequested || CompressionApplied;

    private long? GetCompressionPayloadSizeBytes() =>
        OperationHistorySanitizer.NormalizeNonNegativeMetric(CompressedSizeBytes)
        ?? OperationHistorySanitizer.NormalizeNonNegativeMetric(EstimatedCompressedSizeBytes);
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

internal static class OperationHistoryAlgorithm
{
    internal const string Unknown = "Unknown";
    internal const int MaxAlgorithmLabelLength = 128;

    internal static string NormalizeName(string? algorithm)
    {
        string normalized = NormalizeAlgorithmLabel(algorithm);
        if (normalized.Length == 0)
        {
            return Unknown;
        }

        if (EncryptionAlgorithmCatalog.TryNormalize(normalized, out string encryptionAlgorithm))
        {
            return encryptionAlgorithm;
        }

        try
        {
            return FileHashService.NormalizeAlgorithmName(normalized);
        }
        catch (ArgumentException)
        {
            return normalized;
        }
    }

    internal static int NormalizeKeySize(int? keySizeBits) =>
        keySizeBits is > 0 ? keySizeBits.Value : 0;

    internal static string Format(string? algorithm, int? keySizeBits, string fallback = Unknown)
    {
        string normalized = NormalizeName(algorithm);
        if (string.Equals(normalized, Unknown, StringComparison.Ordinal))
        {
            return string.Equals(fallback, Unknown, StringComparison.Ordinal)
                ? Unknown
                : fallback;
        }

        int keySize = NormalizeKeySize(keySizeBits);
        return keySize > 0 &&
            SupportsKeySizeDisplay(normalized) &&
            !normalized.Contains(keySize.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? $"{normalized} ({keySize}-bit)"
            : normalized;
    }

    private static bool SupportsKeySizeDisplay(string normalized)
    {
        if (EncryptionAlgorithmCatalog.TryNormalize(normalized, out _))
        {
            return true;
        }

        try
        {
            FileHashService.NormalizeAlgorithmName(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeAlgorithmLabel(string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(algorithm.Length, MaxAlgorithmLabelLength));
        bool pendingWhitespace = false;
        foreach (char character in algorithm.Trim())
        {
            if (char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingWhitespace = false;
            if (builder.Length >= MaxAlgorithmLabelLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }
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
    private const int MaxFailureCategoryCount = 10;
    private const int MaxFailureCategoryLength = 40;
    private const int MaxFailureCategorySummaryLength = 512;

    internal static OperationMetricsSummary Calculate(IEnumerable<FileOperationResult>? results)
    {
        FileOperationResult[] resultArray = (results ?? [])
            .Where(result => result is not null)
            .ToArray();

        long? totalOriginal = SumNonNegativeNullable(resultArray.Select(result => result.OriginalSizeBytes));
        long? totalOutput = SumNonNegativeNullable(resultArray.Select(result => result.OutputSizeBytes));
        long? elapsed = SumNonNegativeNullable(resultArray.Select(result => result.ElapsedMilliseconds));
        int compressionRequested = resultArray.Count(result => result.CompressionRequested || result.CompressionApplied);
        int compressionApplied = resultArray.Count(result => result.CompressionApplied);
        int compressionSkipped = Math.Max(0, compressionRequested - compressionApplied);

        string? failureSummary = BuildFailureCategorySummary(resultArray);

        if (string.IsNullOrWhiteSpace(failureSummary))
        {
            failureSummary = null;
        }

        long? saved = SumNonNegativeNullable(resultArray.Select(result => result.NetStorageSavedBytes));
        long? added = SumNonNegativeNullable(resultArray.Select(result => result.NetStorageAddedBytes));

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

    internal static bool IsSuccessfulStatus(string? status) =>
        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Verified", StringComparison.OrdinalIgnoreCase);

    internal static bool IsFailedStatus(string? status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);

    private static string? BuildFailureCategorySummary(IEnumerable<FileOperationResult> results)
    {
        string[] categories = results
            .Where(result => IsFailedStatus(result.Status))
            .Select(result => NormalizeFailureCategory(result.FailureCategory))
            .Where(category => category.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (categories.Length == 0)
        {
            return null;
        }

        List<string> selectedCategories = categories.Take(MaxFailureCategoryCount).ToList();
        if (categories.Length > MaxFailureCategoryCount)
        {
            selectedCategories.Add("Additional categories omitted");
        }

        string summary = string.Join(", ", selectedCategories);
        return summary.Length <= MaxFailureCategorySummaryLength
            ? summary
            : summary[..MaxFailureCategorySummaryLength].TrimEnd();
    }

    private static string NormalizeFailureCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Unknown";
        }

        var builder = new StringBuilder(Math.Min(category.Length, MaxFailureCategoryLength));
        bool pendingWhitespace = false;
        foreach (char character in category.Trim())
        {
            if (char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingWhitespace = false;
            if (builder.Length >= MaxFailureCategoryLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static long? SumNonNegativeNullable(IEnumerable<long?> values)
    {
        long total = 0;
        bool hasValue = false;
        foreach (long? value in values)
        {
            if (OperationHistorySanitizer.NormalizeNonNegativeMetric(value) is not long nonNegativeValue)
            {
                continue;
            }

            if (long.MaxValue - total < nonNegativeValue)
            {
                total = long.MaxValue;
            }
            else
            {
                total += nonNegativeValue;
            }

            hasValue = true;
        }

        return hasValue ? total : null;
    }
}

internal static class OperationHistorySanitizer
{
    private const int MaxHistoryTextLength = 512;
    private const int MaxHistoryMessageLength = 2048;
    internal const int MaxHistoryPathLength = 4096;
    internal const int MaxHistoryResultsPerEntry = 500;

    internal static List<OperationHistoryEntry> CloneEntries(IEnumerable<OperationHistoryEntry>? entries, bool includeFullPaths, int maxEntries = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);

        return (entries ?? Enumerable.Empty<OperationHistoryEntry>())
            .OfType<OperationHistoryEntry>()
            .Take(maxEntries)
            .Select(entry => CloneEntry(entry, includeFullPaths))
            .ToList();
    }

    internal static OperationHistoryEntry CloneEntry(OperationHistoryEntry entry, bool includeFullPaths)
    {
        return new OperationHistoryEntry
        {
            Id = NormalizeRequiredText(entry.Id, Guid.NewGuid().ToString("N")),
            TimestampUtc = entry.TimestampUtc,
            Operation = NormalizeRequiredText(entry.Operation, OperationHistoryAlgorithm.Unknown),
            ProfileName = NormalizeRequiredText(entry.ProfileName, OperationHistoryAlgorithm.Unknown),
            Algorithm = OperationHistoryAlgorithm.NormalizeName(entry.Algorithm),
            Mode = NormalizeRequiredText(entry.Mode, OperationHistoryAlgorithm.Unknown),
            KeySizeBits = OperationHistoryAlgorithm.NormalizeKeySize(entry.KeySizeBits),
            UsedKeyfile = entry.UsedKeyfile,
            RemoveOriginalsAfterSuccess = entry.RemoveOriginalsAfterSuccess,
            SecureDeleteOriginals = entry.SecureDeleteOriginals,
            VerifyAfterWrite = entry.VerifyAfterWrite,
            BackupFolderPath = FormatRequiredPath(entry.BackupFolderPath, includeFullPaths),
            Cancelled = entry.Cancelled,
            SuccessCount = NormalizeCount(entry.SuccessCount),
            FailureCount = NormalizeCount(entry.FailureCount),
            TotalOriginalSizeBytes = NormalizeNonNegativeMetric(entry.TotalOriginalSizeBytes),
            TotalOutputSizeBytes = NormalizeNonNegativeMetric(entry.TotalOutputSizeBytes),
            TotalStorageSavedBytes = NormalizeNonNegativeMetric(entry.TotalStorageSavedBytes),
            TotalStorageAddedBytes = NormalizeNonNegativeMetric(entry.TotalStorageAddedBytes),
            ElapsedMilliseconds = NormalizeNonNegativeMetric(entry.ElapsedMilliseconds),
            CompressionRequestedCount = NormalizeCount(entry.CompressionRequestedCount),
            CompressionAppliedCount = NormalizeCount(entry.CompressionAppliedCount),
            CompressionSkippedCount = NormalizeCount(entry.CompressionSkippedCount),
            FailureCategorySummary = NormalizeOptionalText(entry.FailureCategorySummary),
            Results = (entry.Results ?? [])
                .OfType<FileOperationResult>()
                .Take(MaxHistoryResultsPerEntry)
                .Select(result => CloneResult(result, includeFullPaths))
                .ToList()
        };
    }

    internal static long? NormalizeNonNegativeMetric(long? value) =>
        value is >= 0 ? value : null;

    private static FileOperationResult CloneResult(FileOperationResult result, bool includeFullPaths)
    {
        return new FileOperationResult
        {
            SourcePath = FormatRequiredPath(result.SourcePath, includeFullPaths),
            OutputPath = FormatOptionalPath(result.OutputPath, includeFullPaths),
            BackupPath = FormatOptionalPath(result.BackupPath, includeFullPaths),
            Status = NormalizeRequiredText(result.Status, OperationHistoryAlgorithm.Unknown),
            Message = FormatMessage(result.Message, includeFullPaths),
            OriginalRetained = result.OriginalRetained,
            OutputVerified = result.OutputVerified,
            OriginalSizeBytes = NormalizeNonNegativeMetric(result.OriginalSizeBytes),
            OutputSizeBytes = NormalizeNonNegativeMetric(result.OutputSizeBytes),
            CompressionRequested = result.CompressionRequested,
            CompressionApplied = result.CompressionApplied,
            CompressionReason = FormatMessage(result.CompressionReason, includeFullPaths),
            EstimatedCompressedSizeBytes = NormalizeNonNegativeMetric(result.EstimatedCompressedSizeBytes),
            CompressedSizeBytes = NormalizeNonNegativeMetric(result.CompressedSizeBytes),
            ElapsedMilliseconds = NormalizeNonNegativeMetric(result.ElapsedMilliseconds),
            FailureCategory = NormalizeOptionalText(result.FailureCategory),
            HashValue = NormalizeOptionalText(result.HashValue),
            Algorithm = OperationHistoryAlgorithm.NormalizeName(result.Algorithm),
            KeySizeBits = OperationHistoryAlgorithm.NormalizeKeySize(result.KeySizeBits)
        };
    }

    private static string NormalizeRequiredText(string? value, string fallback)
    {
        string normalized = NormalizeDisplayText(value, MaxHistoryTextLength);
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        string normalized = NormalizeDisplayText(value, MaxHistoryTextLength);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeDisplayText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        bool pendingWhitespace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingWhitespace = false;
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static int NormalizeCount(int value) =>
        Math.Max(0, value);

    private static string FormatRequiredPath(string? path, bool includeFullPaths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Trim();
        return NormalizePathText(includeFullPaths ? normalized : SensitiveDataRedactor.RedactPath(normalized));
    }

    private static string? FormatOptionalPath(string? path, bool includeFullPaths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Trim();
        return NormalizePathText(includeFullPaths ? normalized : SensitiveDataRedactor.RedactPath(normalized));
    }

    private static string NormalizePathText(string value)
    {
        string normalized = ReplacePathControlCharacters(value);
        if (normalized.Length <= MaxHistoryPathLength)
        {
            return normalized;
        }

        const string suffix = " ... path truncated";
        int bodyLength = Math.Max(0, MaxHistoryPathLength - suffix.Length);
        return normalized[..bodyLength].TrimEnd() + suffix;
    }

    private static string ReplacePathControlCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingControl = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingControl = true;
                continue;
            }

            if (pendingControl &&
                builder.Length > 0 &&
                !char.IsWhiteSpace(builder[^1]) &&
                !char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingControl = false;
        }

        return builder.ToString().Trim();
    }

    private static string? FormatMessage(string? message, bool includeFullPaths)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        string normalized = NormalizeDisplayText(message, MaxHistoryMessageLength);
        if (normalized.Length == 0)
        {
            return null;
        }

        return includeFullPaths ? normalized : SensitiveDataRedactor.RedactMessage(normalized);
    }
}

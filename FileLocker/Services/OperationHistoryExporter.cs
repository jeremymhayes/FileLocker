using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FileLocker;

internal static class OperationHistoryExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static string ExportJson(IEnumerable<OperationHistoryEntry> entries, bool includeFullPaths)
    {
        List<OperationHistoryEntry> exportEntries = PrepareEntries(entries, includeFullPaths);
        return JsonSerializer.Serialize(exportEntries, JsonOptions);
    }

    internal static string ExportCsv(IEnumerable<OperationHistoryEntry> entries, bool includeFullPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("timestampUtc,operation,profileName,status,successCount,failureCount,sourcePath,outputPath,verificationStatus,failureCategory,elapsedMilliseconds");

        foreach (OperationHistoryEntry entry in PrepareEntries(entries, includeFullPaths))
        {
            foreach (FileOperationResult result in entry.Results.DefaultIfEmpty(new FileOperationResult
            {
                SourcePath = string.Empty,
                Status = entry.Cancelled ? "Cancelled" : entry.FailureCount > 0 ? "Failed" : "Completed",
                OriginalRetained = true,
                OutputVerified = false
            }))
            {
                builder.AppendCsv(entry.TimestampUtc.ToString("O"));
                builder.Append(',');
                builder.AppendCsv(entry.Operation);
                builder.Append(',');
                builder.AppendCsv(entry.ProfileName);
                builder.Append(',');
                builder.AppendCsv(result.Status);
                builder.Append(',');
                builder.Append(entry.SuccessCount);
                builder.Append(',');
                builder.Append(entry.FailureCount);
                builder.Append(',');
                builder.AppendCsv(result.SourcePath);
                builder.Append(',');
                builder.AppendCsv(result.OutputPath ?? string.Empty);
                builder.Append(',');
                builder.AppendCsv(result.OutputVerified ? "Verified" : "Not verified");
                builder.Append(',');
                builder.AppendCsv(result.FailureCategory ?? string.Empty);
                builder.Append(',');
                builder.Append(result.ElapsedMilliseconds ?? entry.ElapsedMilliseconds ?? 0);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static List<OperationHistoryEntry> PrepareEntries(IEnumerable<OperationHistoryEntry> entries, bool includeFullPaths)
    {
        return entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Select(entry => CloneEntry(entry, includeFullPaths))
            .ToList();
    }

    private static OperationHistoryEntry CloneEntry(OperationHistoryEntry entry, bool includeFullPaths)
    {
        return new OperationHistoryEntry
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            Operation = entry.Operation,
            ProfileName = entry.ProfileName,
            Algorithm = entry.Algorithm,
            Mode = entry.Mode,
            KeySizeBits = entry.KeySizeBits,
            UsedKeyfile = entry.UsedKeyfile,
            RemoveOriginalsAfterSuccess = entry.RemoveOriginalsAfterSuccess,
            SecureDeleteOriginals = entry.SecureDeleteOriginals,
            VerifyAfterWrite = entry.VerifyAfterWrite,
            BackupFolderPath = includeFullPaths ? entry.BackupFolderPath : SensitiveDataRedactor.RedactPath(entry.BackupFolderPath),
            Cancelled = entry.Cancelled,
            SuccessCount = entry.SuccessCount,
            FailureCount = entry.FailureCount,
            TotalOriginalSizeBytes = entry.TotalOriginalSizeBytes,
            TotalOutputSizeBytes = entry.TotalOutputSizeBytes,
            TotalStorageSavedBytes = entry.TotalStorageSavedBytes,
            TotalStorageAddedBytes = entry.TotalStorageAddedBytes,
            ElapsedMilliseconds = entry.ElapsedMilliseconds,
            CompressionRequestedCount = entry.CompressionRequestedCount,
            CompressionAppliedCount = entry.CompressionAppliedCount,
            CompressionSkippedCount = entry.CompressionSkippedCount,
            FailureCategorySummary = entry.FailureCategorySummary,
            Results = entry.Results.Select(result => CloneResult(result, includeFullPaths)).ToList()
        };
    }

    private static FileOperationResult CloneResult(FileOperationResult result, bool includeFullPaths)
    {
        return new FileOperationResult
        {
            SourcePath = includeFullPaths ? result.SourcePath : SensitiveDataRedactor.RedactPath(result.SourcePath),
            OutputPath = includeFullPaths ? result.OutputPath : SensitiveDataRedactor.RedactPath(result.OutputPath),
            BackupPath = includeFullPaths ? result.BackupPath : SensitiveDataRedactor.RedactPath(result.BackupPath),
            Status = result.Status,
            Message = SensitiveDataRedactor.RedactMessage(result.Message),
            OriginalRetained = result.OriginalRetained,
            OutputVerified = result.OutputVerified,
            OriginalSizeBytes = result.OriginalSizeBytes,
            OutputSizeBytes = result.OutputSizeBytes,
            CompressionRequested = result.CompressionRequested,
            CompressionApplied = result.CompressionApplied,
            CompressionReason = result.CompressionReason,
            EstimatedCompressedSizeBytes = result.EstimatedCompressedSizeBytes,
            CompressedSizeBytes = result.CompressedSizeBytes,
            ElapsedMilliseconds = result.ElapsedMilliseconds,
            FailureCategory = result.FailureCategory,
            HashValue = result.HashValue
        };
    }

    private static void AppendCsv(this StringBuilder builder, string value)
    {
        string escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        builder.Append('"');
        builder.Append(escaped);
        builder.Append('"');
    }
}

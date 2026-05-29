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

    internal static string ExportJson(IEnumerable<OperationHistoryEntry>? entries, bool includeFullPaths)
    {
        List<OperationHistoryEntry> exportEntries = PrepareEntries(entries, includeFullPaths);
        return JsonSerializer.Serialize(exportEntries, JsonOptions);
    }

    internal static string ExportCsv(IEnumerable<OperationHistoryEntry>? entries, bool includeFullPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("timestampUtc,operation,profileName,algorithm,keySizeBits,resultAlgorithm,resultKeySizeBits,status,message,successCount,failureCount,sourcePath,outputPath,verificationStatus,failureCategory,elapsedMilliseconds");

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
                builder.AppendCsv(OperationHistoryAlgorithm.NormalizeName(entry.Algorithm));
                builder.Append(',');
                builder.Append(FormatKeySizeBits(OperationHistoryAlgorithm.NormalizeKeySize(entry.KeySizeBits)));
                builder.Append(',');
                builder.AppendCsv(result.Algorithm ?? string.Empty);
                builder.Append(',');
                builder.Append(FormatKeySizeBits(result.KeySizeBits));
                builder.Append(',');
                builder.AppendCsv(result.Status);
                builder.Append(',');
                builder.AppendCsv(result.Message ?? string.Empty);
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

    private static List<OperationHistoryEntry> PrepareEntries(IEnumerable<OperationHistoryEntry>? entries, bool includeFullPaths)
    {
        return OperationHistorySanitizer.CloneEntries(entries, includeFullPaths)
            .OrderByDescending(entry => entry.TimestampUtc)
            .ToList();
    }

    private static string FormatKeySizeBits(int? keySizeBits) =>
        keySizeBits is > 0 ? keySizeBits.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    private static void AppendCsv(this StringBuilder builder, string value)
    {
        builder.Append(CsvCellFormatter.Format(value, alwaysQuote: true));
    }
}

using System.Text.Json;
using FileLocker;

namespace FileLocker.Tests;

public sealed class OperationHistoryExporterTests
{
    [Fact]
    public void ExportJson_RedactsPathsWhenFullPathsAreDisabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Users\jerem\Secrets\plan.txt");

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: false);

        Assert.DoesNotContain(@"C:\Users\jerem\Secrets", json);
        Assert.Contains("[redacted]", json);
        Assert.Contains("plan.txt", json);
    }

    [Fact]
    public void ExportCsv_IncludesVerificationAndFailureColumns()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\broken.txt");
        entry.Results[0].Status = "Failed";
        entry.Results[0].FailureCategory = "Missing file";

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: false);

        Assert.Contains("verificationStatus", csv);
        Assert.Contains("Missing file", csv);
        Assert.Contains("\"[redacted]", csv);
    }

    [Fact]
    public void ExportJson_CanRoundTrip()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: true);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.Equal(@"C:\Files\payload.locked", parsed[0].Results[0].SourcePath);
    }

    private static OperationHistoryEntry CreateHistoryEntry(string sourcePath)
    {
        return new OperationHistoryEntry
        {
            Id = "history-1",
            TimestampUtc = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
            Operation = "Encrypt",
            ProfileName = "Recommended",
            Algorithm = "AES-GCM",
            Mode = "Encrypt / Decrypt",
            KeySizeBits = 256,
            VerifyAfterWrite = true,
            SuccessCount = 1,
            Results =
            [
                new FileOperationResult
                {
                    SourcePath = sourcePath,
                    OutputPath = sourcePath + ".locked",
                    Status = "Completed",
                    OriginalRetained = true,
                    OutputVerified = true
                }
            ]
        };
    }
}

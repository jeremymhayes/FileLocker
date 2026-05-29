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
        Assert.Contains("message", csv);
        Assert.Contains("Missing file", csv);
        Assert.Contains("\"[redacted]", csv);
    }

    [Fact]
    public void ExportCsv_IncludesOperationAndResultAlgorithmColumns()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Algorithm = "Mixed payload algorithms";
        entry.KeySizeBits = 0;
        entry.Results[0].Algorithm = EncryptionAlgorithmCatalog.ChaCha20Poly1305;
        entry.Results[0].KeySizeBits = 256;

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: true);

        Assert.Contains("algorithm,keySizeBits,resultAlgorithm,resultKeySizeBits", csv);
        Assert.Contains("\"Mixed payload algorithms\",,\"ChaCha20-Poly1305\",256", csv);
    }

    [Fact]
    public void ExportCsv_UsesUnknownAlgorithmForLegacyEntries()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Algorithm = null!;
        entry.KeySizeBits = -1;
        entry.Results[0].Algorithm = null;
        entry.Results[0].KeySizeBits = -1;

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: true);

        Assert.Contains("\"Unknown\",", csv);
        Assert.DoesNotContain("\"\",-1", csv);
    }

    [Fact]
    public void ExportCsv_IncludesRedactedResultMessages()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\broken.txt");
        entry.Results[0].Message = @"Could not open 'D:\Vault\secret.txt'.";

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: false);

        Assert.DoesNotContain(@"D:\Vault", csv);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), csv);
    }

    [Fact]
    public void ExportCsv_PreservesResultMessagesWhenFullPathsAreEnabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\broken.txt");
        entry.Results[0].Message = @"Could not open 'D:\Vault\secret.txt'.";

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: true);

        Assert.Contains(@"D:\Vault\secret.txt", csv);
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

    [Fact]
    public void ExportJson_RedactsCompressionReasonPaths()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results[0].CompressionReason = @"Skipped compression for D:\Vault\secret.txt.";

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: false);

        Assert.DoesNotContain(@"D:\Vault", json);
        Assert.Contains("[redacted]", json);
        Assert.Contains("secret.txt", json);
    }

    [Fact]
    public void ExportJson_PreservesCompressionReasonPathsWhenFullPathsAreEnabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results[0].CompressionReason = @"Skipped compression for D:\Vault\secret.txt.";

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: true);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Equal(@"Skipped compression for D:\Vault\secret.txt.", parsed[0].Results[0].CompressionReason);
    }

    [Fact]
    public void ExportCsv_EscapesFormulaLikeTextCells()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.ProfileName = "=Launch";
        entry.Results[0].Message = "+Run";
        entry.Results[0].FailureCategory = "  @Risk";

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: true);

        Assert.Contains("\"'=Launch\"", csv);
        Assert.Contains("\"'+Run\"", csv);
        Assert.Contains("\"'@Risk\"", csv);
    }

    [Fact]
    public void ExportCsv_TreatsNullResultListsAsEmpty()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results = null!;

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: false);

        Assert.Contains("Completed", csv);
        Assert.Contains("Not verified", csv);
    }

    [Fact]
    public void ExportJson_TreatsNullResultListsAsEmpty()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results = null!;

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: false);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Empty(parsed[0].Results);
    }

    [Fact]
    public void ExportJson_SkipsNullResultRows()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results.Add(null!);

        string json = OperationHistoryExporter.ExportJson([entry], includeFullPaths: true);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Single(parsed[0].Results);
        Assert.Equal(@"C:\Files\payload.locked", parsed[0].Results[0].SourcePath);
    }

    [Fact]
    public void ExportCsv_TreatsNullResultRowsAsEmpty()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");
        entry.Results = [null!];

        string csv = OperationHistoryExporter.ExportCsv([entry], includeFullPaths: false);

        Assert.Contains("Completed", csv);
        Assert.Contains("Not verified", csv);
    }

    [Fact]
    public void ExportJson_SkipsNullHistoryEntries()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");

        string json = OperationHistoryExporter.ExportJson([null!, entry], includeFullPaths: true);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.Equal(@"C:\Files\payload.locked", parsed[0].Results[0].SourcePath);
    }

    [Fact]
    public void ExportJson_TreatsNullHistorySequenceAsEmpty()
    {
        string json = OperationHistoryExporter.ExportJson(null, includeFullPaths: false);
        List<OperationHistoryEntry>? parsed = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json);

        Assert.NotNull(parsed);
        Assert.Empty(parsed);
    }

    [Fact]
    public void ExportCsv_SkipsNullHistoryEntries()
    {
        OperationHistoryEntry entry = CreateHistoryEntry(@"C:\Files\payload.locked");

        string csv = OperationHistoryExporter.ExportCsv([null!, entry], includeFullPaths: true);
        string[] lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains(@"C:\Files\payload.locked", csv);
    }

    [Fact]
    public void ExportCsv_TreatsNullHistorySequenceAsEmpty()
    {
        string csv = OperationHistoryExporter.ExportCsv(null, includeFullPaths: false);
        string[] lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.Contains("timestampUtc", lines[0]);
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
                    OutputVerified = true,
                    Algorithm = "AES-GCM",
                    KeySizeBits = 256
                }
            ]
        };
    }
}

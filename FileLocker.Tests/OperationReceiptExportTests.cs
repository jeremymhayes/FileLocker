using System.Reflection;
using FileLocker;

namespace FileLocker.Tests;

public sealed class OperationReceiptExportTests
{
    [Fact]
    public void MarkdownReceipt_RedactsCompressionReasonPathsWhenFullPathsAreDisabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: false);

        Assert.DoesNotContain(@"D:\Vault", markdown);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), markdown);
    }

    [Fact]
    public void CsvReceipt_RedactsCompressionReasonPathsWhenFullPathsAreDisabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();

        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: false);

        Assert.DoesNotContain(@"D:\Vault", csv);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), csv);
    }

    [Fact]
    public void ReceiptExport_PreservesCompressionReasonPathsWhenFullPathsAreEnabled()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: true);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: true);

        Assert.Contains(@"D:\Vault\secret.txt", markdown);
        Assert.Contains(@"D:\Vault\secret.txt", csv);
    }

    [Fact]
    public void ReceiptExport_ToleratesNullResultLists()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results = null!;

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: false);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: false);

        Assert.Contains("| Source | Output | Status |", markdown);
        Assert.Contains("SourcePath,OutputPath,Status", csv);
    }

    [Fact]
    public void ReceiptExport_SkipsNullResultRows()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results.Add(null!);

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: false);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: false);

        Assert.Contains("Completed", markdown);
        Assert.Contains("Completed", csv);
    }

    [Fact]
    public void MarkdownReceipt_KeepsMultilineCellsOnOneTableRow()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results[0].Message = "Line one\r\nLine two | detail";

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: false);

        Assert.DoesNotContain("Line one\r\nLine two", markdown);
        Assert.Contains("Line one Line two \\| detail", markdown);
    }

    [Fact]
    public void MarkdownReceipt_EscapesTopLevelUserControlledFields()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Operation = "Encrypt\r\nInjected";
        entry.ProfileName = "Profile\r\n| detail";
        entry.BackupFolderPath = @"D:\Vault|Backups";
        entry.FailureCategorySummary = "Failure\r\n| detail";

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: true);

        Assert.DoesNotContain("Encrypt\r\nInjected", markdown);
        Assert.DoesNotContain("Profile\r\n| detail", markdown);
        Assert.Contains("# FileLocker Encrypt Injected Operation Receipt", markdown);
        Assert.Contains("- Profile: Profile \\| detail", markdown);
        Assert.Contains(@"- Backup folder: D:\Vault\|Backups", markdown);
        Assert.Contains("- Failure categories: Failure \\| detail", markdown);
    }

    [Fact]
    public void ReceiptExport_IncludesResultAlgorithmMetadata()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Algorithm = "Mixed payload algorithms";
        entry.KeySizeBits = 0;
        entry.Results[0].Algorithm = EncryptionAlgorithmCatalog.ChaCha20Poly1305;
        entry.Results[0].KeySizeBits = 256;

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: true);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: true);

        Assert.Contains("| Completed | ChaCha20-Poly1305 (256-bit) |", markdown);
        Assert.Contains("Status,Algorithm,KeySizeBits", csv);
        Assert.Contains("Completed,ChaCha20-Poly1305,256", csv);
    }

    [Fact]
    public void CsvReceipt_EscapesFormulaLikeTextCells()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results[0].Status = "=Completed";
        entry.Results[0].Message = "+Encrypted";
        entry.Results[0].FailureCategory = "  @Review";

        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: true);

        Assert.Contains("'=Completed", csv);
        Assert.Contains("'+Encrypted", csv);
        Assert.Contains("'@Review", csv);
    }

    [Fact]
    public void ReceiptExport_UsesUnknownAlgorithmForLegacyEntries()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Algorithm = null!;
        entry.KeySizeBits = -1;
        entry.Results[0].Algorithm = null;
        entry.Results[0].KeySizeBits = null;

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: true);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: true);

        Assert.Contains("- Algorithm: Unknown", markdown);
        Assert.Contains("| Completed | - |", markdown);
        Assert.Contains("Completed,Unknown,,Encrypted.", csv);
    }

    [Fact]
    public void ReceiptExport_CapsMalformedPathText()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results[0].SourcePath = @"C:\" + new string('a', OperationHistorySanitizer.MaxHistoryPathLength + 100);

        string markdown = InvokeReceiptBuilder("BuildMarkdownReport", entry, includeFullPaths: true);
        string csv = InvokeReceiptBuilder("BuildCsvReport", entry, includeFullPaths: true);

        Assert.Contains("path truncated", markdown);
        Assert.Contains("path truncated", csv);
        Assert.DoesNotContain(new string('a', OperationHistorySanitizer.MaxHistoryPathLength + 100), markdown);
        Assert.DoesNotContain(new string('a', OperationHistorySanitizer.MaxHistoryPathLength + 100), csv);
    }

    private static string InvokeReceiptBuilder(string methodName, OperationHistoryEntry entry, bool includeFullPaths)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} method not found.");

        return (string)(method.Invoke(null, [entry, includeFullPaths]) ?? string.Empty);
    }

    private static OperationHistoryEntry CreateHistoryEntry()
    {
        return new OperationHistoryEntry
        {
            Id = "receipt-1",
            TimestampUtc = DateTime.UtcNow,
            Operation = "Encrypt",
            ProfileName = "Default",
            Algorithm = "AES-256-GCM",
            Mode = "Encrypt",
            KeySizeBits = 256,
            SuccessCount = 1,
            FailureCount = 0,
            CompressionRequestedCount = 1,
            CompressionAppliedCount = 0,
            Results =
            [
                new FileOperationResult
                {
                    SourcePath = @"C:\Files\payload.txt",
                    OutputPath = @"C:\Files\payload.locked",
                    Status = "Completed",
                    Message = "Encrypted.",
                    OriginalRetained = true,
                    OutputVerified = true,
                    Algorithm = "AES-256-GCM",
                    KeySizeBits = 256,
                    CompressionRequested = true,
                    CompressionApplied = false,
                    CompressionReason = @"Skipped compression for D:\Vault\secret.txt."
                }
            ]
        };
    }
}

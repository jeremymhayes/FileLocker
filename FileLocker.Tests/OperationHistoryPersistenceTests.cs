using System.Reflection;

namespace FileLocker.Tests;

public sealed class OperationHistoryPersistenceTests
{
    [Fact]
    public void CloneHistoryEntries_RedactsAndNormalizesLegacyRows()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Algorithm = null!;
        entry.KeySizeBits = -1;
        entry.Results[0].Algorithm = null;
        entry.Results[0].KeySizeBits = -1;
        entry.Results.Add(null!);

        List<OperationHistoryEntry> cloned = InvokeCloneHistoryEntries([entry], includeFullPaths: false);

        OperationHistoryEntry clone = Assert.Single(cloned);
        Assert.Equal(OperationHistoryAlgorithm.Unknown, clone.Algorithm);
        Assert.Equal(0, clone.KeySizeBits);

        FileOperationResult result = Assert.Single(clone.Results);
        Assert.Equal(OperationHistoryAlgorithm.Unknown, result.Algorithm);
        Assert.Equal(0, result.KeySizeBits);
        Assert.DoesNotContain(@"D:\Vault", result.CompressionReason);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), result.CompressionReason);
    }

    [Fact]
    public void CloneHistoryEntries_NormalizesNullRequiredFieldsAndNegativeMetrics()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Id = null!;
        entry.Operation = null!;
        entry.ProfileName = null!;
        entry.Mode = null!;
        entry.SuccessCount = -1;
        entry.FailureCount = -2;
        entry.TotalOriginalSizeBytes = -3;
        entry.TotalOutputSizeBytes = -4;
        entry.TotalStorageSavedBytes = -5;
        entry.TotalStorageAddedBytes = -6;
        entry.ElapsedMilliseconds = -7;
        entry.CompressionRequestedCount = -8;
        entry.CompressionAppliedCount = -9;
        entry.CompressionSkippedCount = -10;
        entry.FailureCategorySummary = "   ";
        entry.Results[0].SourcePath = null!;
        entry.Results[0].Status = null!;
        entry.Results[0].OriginalSizeBytes = -11;
        entry.Results[0].OutputSizeBytes = -12;
        entry.Results[0].EstimatedCompressedSizeBytes = -13;
        entry.Results[0].CompressedSizeBytes = -14;
        entry.Results[0].ElapsedMilliseconds = -15;
        entry.Results[0].FailureCategory = "   ";
        entry.Results[0].HashValue = "   ";

        List<OperationHistoryEntry> cloned = InvokeCloneHistoryEntries([entry], includeFullPaths: true);

        OperationHistoryEntry clone = Assert.Single(cloned);
        Assert.False(string.IsNullOrWhiteSpace(clone.Id));
        Assert.Equal(OperationHistoryAlgorithm.Unknown, clone.Operation);
        Assert.Equal(OperationHistoryAlgorithm.Unknown, clone.ProfileName);
        Assert.Equal(OperationHistoryAlgorithm.Unknown, clone.Mode);
        Assert.Equal(0, clone.SuccessCount);
        Assert.Equal(0, clone.FailureCount);
        Assert.Null(clone.TotalOriginalSizeBytes);
        Assert.Null(clone.TotalOutputSizeBytes);
        Assert.Null(clone.TotalStorageSavedBytes);
        Assert.Null(clone.TotalStorageAddedBytes);
        Assert.Null(clone.ElapsedMilliseconds);
        Assert.Equal(0, clone.CompressionRequestedCount);
        Assert.Equal(0, clone.CompressionAppliedCount);
        Assert.Equal(0, clone.CompressionSkippedCount);
        Assert.Null(clone.FailureCategorySummary);

        FileOperationResult result = Assert.Single(clone.Results);
        Assert.Equal(string.Empty, result.SourcePath);
        Assert.Equal(OperationHistoryAlgorithm.Unknown, result.Status);
        Assert.Null(result.OriginalSizeBytes);
        Assert.Null(result.OutputSizeBytes);
        Assert.Null(result.EstimatedCompressedSizeBytes);
        Assert.Null(result.CompressedSizeBytes);
        Assert.Null(result.ElapsedMilliseconds);
        Assert.Null(result.FailureCategory);
        Assert.Null(result.HashValue);
    }

    [Fact]
    public void CloneHistoryEntries_TrimsLegacyTextFields()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Operation = "  Encrypt  ";
        entry.ProfileName = "  Default  ";
        entry.Mode = "  Encrypt  ";
        entry.BackupFolderPath = @"  C:\Backups  ";
        entry.FailureCategorySummary = "  Authentication failed  ";
        entry.Results[0].SourcePath = @"  C:\Files\payload.txt  ";
        entry.Results[0].OutputPath = @"  C:\Files\payload.txt.locked  ";
        entry.Results[0].Status = "  Completed  ";
        entry.Results[0].Message = "  Encrypted.  ";
        entry.Results[0].CompressionReason = "  Already compressed.  ";
        entry.Results[0].FailureCategory = "  Access denied  ";
        entry.Results[0].HashValue = "  abc123  ";

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal("Encrypt", clone.Operation);
        Assert.Equal("Default", clone.ProfileName);
        Assert.Equal("Encrypt", clone.Mode);
        Assert.Equal(@"C:\Backups", clone.BackupFolderPath);
        Assert.Equal("Authentication failed", clone.FailureCategorySummary);
        Assert.Equal(@"C:\Files\payload.txt", result.SourcePath);
        Assert.Equal(@"C:\Files\payload.txt.locked", result.OutputPath);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("Encrypted.", result.Message);
        Assert.Equal("Already compressed.", result.CompressionReason);
        Assert.Equal("Access denied", result.FailureCategory);
        Assert.Equal("abc123", result.HashValue);
    }

    [Fact]
    public void CloneHistoryEntries_CollapsesControlCharactersAndCapsDisplayText()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Operation = "Encrypt\r\nFiles";
        entry.ProfileName = "Custom\tProfile";
        entry.FailureCategorySummary = new string('x', 700);
        entry.Results[0].Message = "Line one\r\nLine two";

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal("Encrypt Files", clone.Operation);
        Assert.Equal("Custom Profile", clone.ProfileName);
        Assert.Equal(512, clone.FailureCategorySummary?.Length);
        Assert.Equal("Line one Line two", result.Message);
    }

    [Fact]
    public void CloneHistoryEntries_CollapsesUnicodeFormatCharactersInDisplayText()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Operation = "Encrypt\u202EFiles";
        entry.ProfileName = "Custom\u202EProfile";
        entry.Results[0].Message = "Line\u202ETwo";

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal("Encrypt Files", clone.Operation);
        Assert.Equal("Custom Profile", clone.ProfileName);
        Assert.Equal("Line Two", result.Message);
    }

    [Fact]
    public void CloneHistoryEntries_ReplacesControlCharactersInPathFields()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.BackupFolderPath = @"C:\Backups" + "\r\n" + "Today";
        entry.Results[0].SourcePath = @"C:\Files" + "\0" + "payload.txt";
        entry.Results[0].OutputPath = @"C:\Files" + "\t" + "payload.locked";

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal(@"C:\Backups Today", clone.BackupFolderPath);
        Assert.Equal(@"C:\Files payload.txt", result.SourcePath);
        Assert.Equal(@"C:\Files payload.locked", result.OutputPath);
    }

    [Fact]
    public void CloneHistoryEntries_ReplacesUnicodeFormatCharactersInPathFields()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.BackupFolderPath = @"C:\Backups" + "\u202E" + "Today";
        entry.Results[0].SourcePath = @"C:\Files" + "\u202E" + "payload.txt";
        entry.Results[0].OutputPath = @"C:\Files" + "\u202E" + "payload.locked";

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal(@"C:\Backups Today", clone.BackupFolderPath);
        Assert.Equal(@"C:\Files payload.txt", result.SourcePath);
        Assert.Equal(@"C:\Files payload.locked", result.OutputPath);
    }

    [Fact]
    public void CloneHistoryEntries_CapsMalformedPathText()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.BackupFolderPath = @"C:\" + new string('a', OperationHistorySanitizer.MaxHistoryPathLength + 100);
        entry.Results[0].SourcePath = @"C:\" + new string('b', OperationHistorySanitizer.MaxHistoryPathLength + 100);
        entry.Results[0].OutputPath = @"C:\" + new string('c', OperationHistorySanitizer.MaxHistoryPathLength + 100);

        OperationHistoryEntry clone = Assert.Single(InvokeCloneHistoryEntries([entry], includeFullPaths: true));
        FileOperationResult result = Assert.Single(clone.Results);

        Assert.Equal(OperationHistorySanitizer.MaxHistoryPathLength, clone.BackupFolderPath.Length);
        Assert.Equal(OperationHistorySanitizer.MaxHistoryPathLength, result.SourcePath.Length);
        Assert.Equal(OperationHistorySanitizer.MaxHistoryPathLength, result.OutputPath?.Length);
        Assert.EndsWith("path truncated", result.SourcePath);
    }

    [Fact]
    public void CloneEntries_RespectsMaxEntries()
    {
        OperationHistoryEntry first = CreateHistoryEntry();
        first.Id = "history-1";
        OperationHistoryEntry second = CreateHistoryEntry();
        second.Id = "history-2";
        OperationHistoryEntry third = CreateHistoryEntry();
        third.Id = "history-3";
        List<OperationHistoryEntry> entries =
        [
            first,
            second,
            third
        ];

        List<OperationHistoryEntry> cloned = OperationHistorySanitizer.CloneEntries(entries, includeFullPaths: true, maxEntries: 2);

        Assert.Equal(["history-1", "history-2"], cloned.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void CloneEntries_CapsResultsPerEntry()
    {
        OperationHistoryEntry entry = CreateHistoryEntry();
        entry.Results = Enumerable
            .Range(0, OperationHistorySanitizer.MaxHistoryResultsPerEntry + 20)
            .Select(index => new FileOperationResult
            {
                SourcePath = $@"C:\Files\payload-{index:D4}.txt",
                Status = "Completed"
            })
            .ToList();

        OperationHistoryEntry clone = Assert.Single(OperationHistorySanitizer.CloneEntries([entry], includeFullPaths: true));

        Assert.Equal(OperationHistorySanitizer.MaxHistoryResultsPerEntry, clone.Results.Count);
        Assert.EndsWith("payload-0000.txt", clone.Results[0].SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"payload-{OperationHistorySanitizer.MaxHistoryResultsPerEntry - 1:D4}.txt", clone.Results[^1].SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloneEntries_RejectsNegativeMaxEntries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationHistorySanitizer.CloneEntries([CreateHistoryEntry()], includeFullPaths: true, maxEntries: -1));
    }

    private static List<OperationHistoryEntry> InvokeCloneHistoryEntries(List<OperationHistoryEntry> entries, bool includeFullPaths)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("CloneHistoryEntries", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CloneHistoryEntries method not found.");

        return (List<OperationHistoryEntry>)(method.Invoke(null, [entries, includeFullPaths])
            ?? throw new InvalidOperationException("CloneHistoryEntries returned null."));
    }

    private static OperationHistoryEntry CreateHistoryEntry()
    {
        return new OperationHistoryEntry
        {
            Id = "history-1",
            TimestampUtc = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc),
            Operation = "Encrypt",
            ProfileName = "Default",
            Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
            Mode = "Encrypt",
            KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm),
            SuccessCount = 1,
            FailureCount = 0,
            Results =
            [
                new FileOperationResult
                {
                    SourcePath = @"C:\Files\payload.txt",
                    OutputPath = @"C:\Files\payload.txt.locked",
                    Status = "Completed",
                    Message = "Encrypted.",
                    OriginalRetained = true,
                    OutputVerified = true,
                    CompressionRequested = true,
                    CompressionApplied = false,
                    CompressionReason = @"Skipped compression for D:\Vault\secret.txt.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm)
                }
            ]
        };
    }
}

using System.Reflection;

namespace FileLocker.Tests;

public sealed class StorageSavingsTests
{
    [Fact]
    public void OperationHistoryMetrics_UsesCompressedPayloadSizeForCompressionSavings()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 250_000,
            outputSizeBytes: 260_000,
            compressedSizeBytes: 20_000);

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate([result]);

        Assert.Equal(250_000, summary.TotalOriginalSizeBytes);
        Assert.Equal(260_000, summary.TotalOutputSizeBytes);
        Assert.Equal(230_000, summary.TotalStorageSavedBytes);
        Assert.Equal(0, summary.TotalStorageAddedBytes);
    }

    [Fact]
    public void OperationHistoryMetrics_TracksCompressionIncreaseWhenCompressionDoesNotHelp()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 64_000,
            outputSizeBytes: 70_000,
            estimatedCompressedSizeBytes: 66_500,
            compressionApplied: false);

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate([result]);

        Assert.Equal(0, summary.TotalStorageSavedBytes);
        Assert.Equal(2_500, summary.TotalStorageAddedBytes);
    }

    [Fact]
    public void OperationHistoryMetrics_IgnoresNullResultRows()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 250_000,
            outputSizeBytes: 260_000,
            compressedSizeBytes: 20_000);

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate([null!, result]);

        Assert.Equal(250_000, summary.TotalOriginalSizeBytes);
        Assert.Equal(1, summary.CompressionRequestedCount);
    }

    [Fact]
    public void OperationHistoryMetrics_TreatsNullResultSequenceAsEmpty()
    {
        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate(null!);

        Assert.Null(summary.TotalOriginalSizeBytes);
        Assert.Null(summary.TotalOutputSizeBytes);
        Assert.Null(summary.TotalStorageSavedBytes);
        Assert.Equal(0, summary.CompressionRequestedCount);
        Assert.Equal(0, summary.CompressionAppliedCount);
        Assert.Equal(0, summary.CompressionSkippedCount);
        Assert.Null(summary.FailureCategorySummary);
    }

    [Fact]
    public void OperationHistoryMetrics_IgnoresNegativeMetricValues()
    {
        var result = new FileOperationResult
        {
            SourcePath = @"C:\private\source.txt",
            Status = "Completed",
            OriginalRetained = true,
            OutputVerified = true,
            OriginalSizeBytes = -1,
            OutputSizeBytes = -2,
            ElapsedMilliseconds = -3,
            CompressionRequested = true,
            CompressedSizeBytes = -4,
            EstimatedCompressedSizeBytes = -5
        };

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate([result]);

        Assert.Null(summary.TotalOriginalSizeBytes);
        Assert.Null(summary.TotalOutputSizeBytes);
        Assert.Null(summary.ElapsedMilliseconds);
        Assert.Null(summary.TotalStorageSavedBytes);
        Assert.Null(summary.TotalStorageAddedBytes);
        Assert.Equal(1, summary.CompressionRequestedCount);
    }

    [Fact]
    public void OperationHistoryMetrics_TreatsAppliedCompressionAsRequestedForInconsistentLegacyRows()
    {
        var result = new FileOperationResult
        {
            SourcePath = @"C:\private\source.txt",
            Status = "Completed",
            OriginalRetained = true,
            OutputVerified = true,
            OriginalSizeBytes = 120_000,
            CompressedSizeBytes = 80_000,
            CompressionRequested = false,
            CompressionApplied = true
        };

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate([result]);

        Assert.Equal(1, summary.CompressionRequestedCount);
        Assert.Equal(1, summary.CompressionAppliedCount);
        Assert.Equal(0, summary.CompressionSkippedCount);
        Assert.Equal(40_000, summary.TotalStorageSavedBytes);
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Verified", true)]
    [InlineData("Failed", false)]
    [InlineData(null, false)]
    public void OperationHistoryMetrics_UsesSharedSuccessStatusRules(string? status, bool expected)
    {
        Assert.Equal(expected, OperationHistoryMetrics.IsSuccessfulStatus(status));
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Completed", false)]
    [InlineData("Verified", false)]
    [InlineData(null, false)]
    public void OperationHistoryMetrics_UsesSharedFailureStatusRules(string? status, bool expected)
    {
        Assert.Equal(expected, OperationHistoryMetrics.IsFailedStatus(status));
    }

    [Fact]
    public void OperationHistoryMetrics_BoundsFailureCategorySummary()
    {
        FileOperationResult[] results = Enumerable.Range(0, 20)
            .Select(index => new FileOperationResult
            {
                SourcePath = $"source-{index}.txt",
                Status = "Failed",
                FailureCategory = $"Category {index:D2}\r\n{new string('x', 120)}",
                OriginalRetained = true,
                OutputVerified = false
            })
            .ToArray();

        OperationMetricsSummary summary = OperationHistoryMetrics.Calculate(results);

        Assert.NotNull(summary.FailureCategorySummary);
        Assert.True(summary.FailureCategorySummary.Length <= 512);
        Assert.DoesNotContain("\r", summary.FailureCategorySummary);
        Assert.DoesNotContain("\n", summary.FailureCategorySummary);
        Assert.Contains("Additional categories omitted", summary.FailureCategorySummary);
    }

    [Fact]
    public void TryGetTrackedStorageDeltaBytes_UsesStoredCompressedSizeWithRedactedPaths()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 128_000,
            outputSizeBytes: 140_000,
            compressedSizeBytes: 80_000,
            sourcePath: "[redacted]",
            outputPath: "[redacted]");

        (bool tracked, long deltaBytes) = InvokeTryGetTrackedStorageDeltaBytes(result);

        Assert.True(tracked);
        Assert.Equal(48_000, deltaBytes);
    }

    [Fact]
    public void TryGetTrackedStorageDeltaBytes_ReturnsNegativeDeltaForLargerCompressionEstimate()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 64_000,
            outputSizeBytes: 70_000,
            estimatedCompressedSizeBytes: 66_000,
            compressionApplied: false);

        (bool tracked, long deltaBytes) = InvokeTryGetTrackedStorageDeltaBytes(result);

        Assert.True(tracked);
        Assert.Equal(-2_000, deltaBytes);
    }

    [Fact]
    public void TryGetTrackedStorageDeltaBytes_TracksAppliedCompressionForLegacyRows()
    {
        FileOperationResult result = CreateCompletedResult(
            originalSizeBytes: 128_000,
            outputSizeBytes: 140_000,
            compressedSizeBytes: 80_000);
        result.CompressionRequested = false;
        result.CompressionApplied = true;

        (bool tracked, long deltaBytes) = InvokeTryGetTrackedStorageDeltaBytes(result);

        Assert.True(tracked);
        Assert.Equal(48_000, deltaBytes);
    }

    [Fact]
    public void TryGetTrackedStorageDeltaBytes_RejectsNegativeHistorySizes()
    {
        FileOperationResult negativeOriginal = CreateCompletedResult(
            originalSizeBytes: -1,
            outputSizeBytes: 10,
            compressedSizeBytes: 0);
        FileOperationResult negativeCompressed = CreateCompletedResult(
            originalSizeBytes: 10,
            outputSizeBytes: 10,
            compressedSizeBytes: -1);

        (bool originalTracked, _) = InvokeTryGetTrackedStorageDeltaBytes(negativeOriginal);
        (bool compressedTracked, _) = InvokeTryGetTrackedStorageDeltaBytes(negativeCompressed);

        Assert.False(originalTracked);
        Assert.False(compressedTracked);
    }

    private static FileOperationResult CreateCompletedResult(
        long originalSizeBytes,
        long outputSizeBytes,
        long? compressedSizeBytes = null,
        long? estimatedCompressedSizeBytes = null,
        bool compressionApplied = true,
        string sourcePath = @"C:\private\source.txt",
        string outputPath = @"C:\private\source.txt.locked")
    {
        return new FileOperationResult
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Status = "Completed",
            OriginalRetained = true,
            OutputVerified = true,
            OriginalSizeBytes = originalSizeBytes,
            OutputSizeBytes = outputSizeBytes,
            CompressionRequested = true,
            CompressionApplied = compressionApplied,
            EstimatedCompressedSizeBytes = estimatedCompressedSizeBytes ?? compressedSizeBytes,
            CompressedSizeBytes = compressionApplied ? compressedSizeBytes : null
        };
    }

    private static (bool Tracked, long DeltaBytes) InvokeTryGetTrackedStorageDeltaBytes(FileOperationResult result)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("TryGetTrackedStorageDeltaBytes", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryGetTrackedStorageDeltaBytes method not found.");
        object?[] args = [result, 0L];
        bool tracked = (bool)(method.Invoke(null, args) ?? false);
        return (tracked, (long)args[1]!);
    }
}

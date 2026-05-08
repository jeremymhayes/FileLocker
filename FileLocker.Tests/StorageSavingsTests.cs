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

namespace FileLocker.Tests;

public sealed class QueuedFileItemTests
{
    [Fact]
    public void UpdateProgress_TreatsNaNAsZero()
    {
        var item = new QueuedFileItem(
            @"C:\Temp\secret.txt",
            sourceRootPath: string.Empty,
            sourceRootIsFolder: false,
            sizeBytes: 1024);

        item.UpdateProgress(double.NaN);

        Assert.Equal(0, item.ProgressPercent);
        Assert.Equal("0%", item.ProgressStatus);
    }
}

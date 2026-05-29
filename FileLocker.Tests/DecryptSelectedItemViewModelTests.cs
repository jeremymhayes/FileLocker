namespace FileLocker.Tests;

public sealed class DecryptSelectedItemViewModelTests
{
    [Fact]
    public void SetCancelled_UsesCancelledStateWithoutBlockingRetry()
    {
        var item = new DecryptSelectedItemViewModel(
            @"C:\Temp\secret.locked",
            sourceRootPath: string.Empty,
            sourceRootIsFolder: false,
            sizeBytes: 1024,
            isSupportedEncryptedFile: true,
            status: "Ready",
            detail: "Ready to decrypt.");

        item.SetCancelled("Cancelled before this file started.");

        Assert.Equal("Cancelled", item.Status);
        Assert.Equal("Cancelled before this file started.", item.Detail);
        Assert.Equal("Cancelled", item.ProgressStatus);
        Assert.True(item.CanDecrypt);
    }

    [Fact]
    public void UpdateProgress_TreatsNaNAsZero()
    {
        var item = new DecryptSelectedItemViewModel(
            @"C:\Temp\secret.locked",
            sourceRootPath: string.Empty,
            sourceRootIsFolder: false,
            sizeBytes: 1024,
            isSupportedEncryptedFile: true,
            status: "Ready",
            detail: "Ready to decrypt.");

        item.UpdateProgress(double.NaN);

        Assert.Equal(0, item.ProgressPercent);
        Assert.Equal("0%", item.ProgressStatus);
    }
}

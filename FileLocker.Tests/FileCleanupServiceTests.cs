namespace FileLocker.Tests;

public sealed class FileCleanupServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_RemovesNestedFiles()
    {
        string nested = Path.Combine(_rootPath, "updater", "downloads");
        Directory.CreateDirectory(nested);
        string filePath = Path.Combine(nested, "installer.download");
        File.WriteAllText(filePath, "temp");

        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(_rootPath);

        Assert.Equal(1, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_RemovesReadOnlyFiles()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "installer.download");
        File.WriteAllText(filePath, "temp");
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);

        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(_rootPath);

        Assert.Equal(1, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_RemovesCachedInstallerFiles()
    {
        Directory.CreateDirectory(_rootPath);
        string installerPath = Path.Combine(_rootPath, "FileLocker-Setup-1.2.3.exe");
        File.WriteAllText(installerPath, "installer");

        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(_rootPath);

        Assert.Equal(1, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.False(File.Exists(installerPath));
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_PreservesUpdaterSettings()
    {
        Directory.CreateDirectory(_rootPath);
        string settingsPath = Path.Combine(_rootPath, "settings.json");
        string tempPath = Path.Combine(_rootPath, "installer.download");
        File.WriteAllText(settingsPath, "{}");
        File.WriteAllText(tempPath, "temp");

        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(_rootPath);

        Assert.Equal(1, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.True(File.Exists(settingsPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_MissingDirectoryIsNoOp()
    {
        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(Path.Combine(_rootPath, "missing"));

        Assert.Equal(0, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
    }

    [Fact]
    public void TryDeleteTemporaryFile_RejectsFileLinkWithoutTouchingTarget()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.download");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target");
        string linkPath = Path.Combine(_rootPath, "installer.download");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            bool deleted = FileCleanupService.TryDeleteTemporaryFile(linkPath, out string? failure);

            Assert.False(deleted);
            Assert.Contains("reparse", failure, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(linkPath));
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside));
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ClearReadOnlyAttribute_MissingPathIsNoOp()
    {
        FileCleanupService.ClearReadOnlyAttribute(Path.Combine(_rootPath, "missing.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearReadOnlyAttribute_BlankPathIsNoOp(string path)
    {
        FileCleanupService.ClearReadOnlyAttribute(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            foreach (string file in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

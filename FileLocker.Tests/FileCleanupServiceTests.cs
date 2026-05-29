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
    public void DeleteTemporaryFilesUnderDirectory_PreservesSimilarInstallerNames()
    {
        Directory.CreateDirectory(_rootPath);
        string ownedInstallerPath = Path.Combine(_rootPath, "FileLocker-Setup-1.2.3.exe");
        string similarInstallerPath = Path.Combine(_rootPath, "FileLocker-SetupEvil.exe");
        File.WriteAllText(ownedInstallerPath, "installer");
        File.WriteAllText(similarInstallerPath, "other");

        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(_rootPath);

        Assert.Equal(1, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.False(File.Exists(ownedInstallerPath));
        Assert.True(File.Exists(similarInstallerPath));
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
    public void DeleteTemporaryFilesUnderDirectory_ControlCharacterRootIsNoOp()
    {
        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory("C:\\bad\r\nroot");

        Assert.Equal(0, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_UnicodeFormatRootIsNoOp()
    {
        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(
            Path.Combine(_rootPath, "root" + "\u202E"));

        Assert.Equal(0, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_RelativeRootIsNoOp()
    {
        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory("downloads");

        Assert.Equal(0, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
    }

    [Fact]
    public void DeleteTemporaryFilesUnderDirectory_AlternateDataStreamRootIsNoOp()
    {
        FileCleanupSummary summary = FileCleanupService.DeleteTemporaryFilesUnderDirectory(Path.Combine(_rootPath, "root:stream"));

        Assert.Equal(0, summary.DeletedFiles);
        Assert.Equal(0, summary.FailedFiles);
    }

    [Fact]
    public void DeleteTemporaryFiles_NullPathArrayIsNoOp()
    {
        IReadOnlyList<string> failures = FileCleanupService.DeleteTemporaryFiles(null!);

        Assert.Empty(failures);
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
    public void TryDeleteFile_RemovesKnownNonTemporaryFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "history.json");
        File.WriteAllText(path, "{}");

        bool deleted = FileCleanupService.TryDeleteFile(path, out string? failure);

        Assert.True(deleted);
        Assert.Null(failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDeleteFile_RejectsControlCharacterPath()
    {
        bool deleted = FileCleanupService.TryDeleteFile("C:\\bad\r\nfile.tmp", out string? failure);

        Assert.False(deleted);
        Assert.Equal("Invalid cleanup path.", failure);
    }

    [Fact]
    public void TryDeleteFile_RejectsUnicodeFormatCharacterPath()
    {
        bool deleted = FileCleanupService.TryDeleteFile(
            Path.Combine(_rootPath, "history" + "\u202E" + ".json"),
            out string? failure);

        Assert.False(deleted);
        Assert.Equal("Invalid cleanup path.", failure);
    }

    [Fact]
    public void TryDeleteFile_RejectsRelativePath()
    {
        bool deleted = FileCleanupService.TryDeleteFile("history.json", out string? failure);

        Assert.False(deleted);
        Assert.Equal("Invalid cleanup path.", failure);
    }

    [Fact]
    public void TryDeleteFile_RejectsAlternateDataStreamPath()
    {
        bool deleted = FileCleanupService.TryDeleteFile(Path.Combine(_rootPath, "history.json:stream"), out string? failure);

        Assert.False(deleted);
        Assert.Equal("Invalid cleanup path.", failure);
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

    [Fact]
    public void ClearReadOnlyAttribute_ControlCharacterPathIsNoOp()
    {
        FileCleanupService.ClearReadOnlyAttribute("C:\\bad\tfile.tmp");
    }

    [Fact]
    public void ClearReadOnlyAttribute_UnicodeFormatCharacterPathIsNoOp()
    {
        FileCleanupService.ClearReadOnlyAttribute(Path.Combine(_rootPath, "settings" + "\u202E" + ".json"));
    }

    [Fact]
    public void ClearReadOnlyAttribute_RelativePathIsNoOp()
    {
        FileCleanupService.ClearReadOnlyAttribute("settings.json");
    }

    [Fact]
    public void ClearReadOnlyAttribute_AlternateDataStreamPathIsNoOp()
    {
        FileCleanupService.ClearReadOnlyAttribute(Path.Combine(_rootPath, "settings.json:stream"));
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

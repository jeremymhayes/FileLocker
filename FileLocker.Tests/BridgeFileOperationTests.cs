namespace FileLocker.Tests;

public sealed class BridgeFileOperationTests
{
    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_RejectsFolderRemovalWithoutConfirmation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                MainWindow.ValidateFolderSourceRemovalConfirmation(
                    removeOriginalsAfterSuccess: true,
                    [directory],
                    deleteConfirmation: ""));

            Assert.Equal("Folder-wide source removal requires typing DELETE before the run starts.", ex.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_AllowsFolderRemovalWithExactConfirmation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            MainWindow.ValidateFolderSourceRemovalConfirmation(
                removeOriginalsAfterSuccess: true,
                [directory],
                deleteConfirmation: "DELETE");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_AllowsFileOnlyRemovalWithoutFolderConfirmation()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"FileLocker-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(filePath, "contents");
        try
        {
            MainWindow.ValidateFolderSourceRemovalConfirmation(
                removeOriginalsAfterSuccess: true,
                [filePath],
                deleteConfirmation: "");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsNullPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths(null));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsEmptyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths([]));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsBlankOnlyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths([" ", "\t"]));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsMalformedPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths(["C:\\Temp\\bad\0path.txt"]));

        Assert.Equal("One or more selected paths are invalid.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_NormalizesAndDeduplicatesPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "payload.txt");
        string fullPath = Path.GetFullPath(path);

        string[] validated = MainWindow.ValidateFileOperationBridgePaths([
            $"  {path}  ",
            "",
            fullPath
        ]);

        string singlePath = Assert.Single(validated);
        Assert.Equal(fullPath, singlePath);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"FileLocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}

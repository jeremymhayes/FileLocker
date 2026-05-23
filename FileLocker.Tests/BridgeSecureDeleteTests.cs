namespace FileLocker.Tests;

public sealed class BridgeSecureDeleteTests
{
    [Fact]
    public void ValidateSecureDeleteConfirmation_RejectsMissingConfirmation()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteConfirmation(""));

        Assert.Equal("Confirm secure delete before deleting selected files or folders.", ex.Message);
    }

    [Fact]
    public void ValidateSecureDeleteConfirmation_AllowsExactConfirmation()
    {
        MainWindow.ValidateSecureDeleteConfirmation("DELETE");
    }

    [Fact]
    public void ValidateSecureDeleteBridgePaths_RejectsNullPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteBridgePaths(null));

        Assert.Equal("Select at least one file or folder to delete.", ex.Message);
    }

    [Fact]
    public void ValidateSecureDeleteBridgePaths_RejectsEmptyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteBridgePaths([]));

        Assert.Equal("Select at least one file or folder to delete.", ex.Message);
    }

    [Fact]
    public void ValidateSecureDeleteBridgePaths_RejectsBlankOnlyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteBridgePaths([" ", "\t"]));

        Assert.Equal("Select at least one file or folder to delete.", ex.Message);
    }

    [Fact]
    public void ValidateSecureDeleteBridgePaths_RejectsMalformedPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteBridgePaths(["C:\\Temp\\bad\0path.txt"]));

        Assert.Equal("One or more selected paths are invalid.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void ValidateSecureDeleteBridgePaths_NormalizesAndDeduplicatesPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "payload.txt");
        string fullPath = Path.GetFullPath(path);

        string[] validated = MainWindow.ValidateSecureDeleteBridgePaths([
            $"  {path}  ",
            "",
            fullPath
        ]);

        string singlePath = Assert.Single(validated);
        Assert.Equal(fullPath, singlePath);
    }
}

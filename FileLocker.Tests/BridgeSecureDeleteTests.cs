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
    public void ValidateSecureDeleteBridgePaths_RejectsControlCharacterPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateSecureDeleteBridgePaths(["C:\\Temp\\bad\0path.txt"]));

        Assert.Equal("A selected value contains invalid characters.", ex.Message);
        Assert.Null(ex.InnerException);
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

    [Theory]
    [InlineData("quick", 35, 1)]
    [InlineData("dod", 1, 3)]
    [InlineData("gutmann", 1, 35)]
    [InlineData(null, 35, 3)]
    [InlineData("custom", 99, 35)]
    [InlineData("custom", 0, 3)]
    public void NormalizeSecureDeletePasses_MakesNamedMethodsAuthoritative(string? method, int requestedPasses, int expected)
    {
        int passes = MainWindow.NormalizeSecureDeletePasses(requestedPasses, method);

        Assert.Equal(expected, passes);
    }
}

namespace FileLocker.Tests;

public sealed class BridgeHashManifestTests
{
    [Fact]
    public void ValidateHashManifestBridgePaths_RejectsNullPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateHashManifestBridgePaths(null));

        Assert.Equal("Select at least one file or folder for the hash manifest.", ex.Message);
    }

    [Fact]
    public void ValidateHashManifestBridgePaths_RejectsEmptyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateHashManifestBridgePaths([]));

        Assert.Equal("Select at least one file or folder for the hash manifest.", ex.Message);
    }

    [Fact]
    public void ValidateHashManifestBridgePaths_RejectsBlankOnlyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateHashManifestBridgePaths([" ", "\t"]));

        Assert.Equal("Select at least one file or folder for the hash manifest.", ex.Message);
    }

    [Fact]
    public void ValidateHashManifestBridgePaths_RejectsControlCharacterPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateHashManifestBridgePaths(["C:\\Temp\\bad\0path.txt"]));

        Assert.Equal("A selected value contains invalid characters.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ValidateHashManifestBridgePaths_NormalizesAndDeduplicatesPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "payload.txt");
        string fullPath = Path.GetFullPath(path);

        string[] validated = MainWindow.ValidateHashManifestBridgePaths([
            $"  {path}  ",
            "",
            fullPath
        ]);

        string singlePath = Assert.Single(validated);
        Assert.Equal(fullPath, singlePath);
    }
}

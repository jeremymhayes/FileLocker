namespace FileLocker.Tests;

public sealed class BridgePathValidationTests
{
    [Fact]
    public void RequireExistingPath_RejectsBlankPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath(" "));

        Assert.Equal("A file or folder path is required.", ex.Message);
    }

    [Fact]
    public void RequireExistingPath_RejectsMalformedPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath("C:\\Temp\\bad\0path.txt"));

        Assert.Equal("The selected path is not valid.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void RequireExistingPath_ReportsMissingPath()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "payload.txt");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() =>
            MainWindow.RequireExistingPath(missingPath));

        Assert.Equal("The selected path could not be found.", ex.Message);
        Assert.Equal(Path.GetFullPath(missingPath), ex.FileName);
    }

    [Fact]
    public void RequireExistingPath_ReturnsFullPathForExistingDirectory()
    {
        string directory = Path.GetTempPath();

        string resolved = MainWindow.RequireExistingPath($"  {directory}  ");

        Assert.Equal(Path.GetFullPath(directory), resolved);
    }
}

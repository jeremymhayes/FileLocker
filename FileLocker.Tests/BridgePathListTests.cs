namespace FileLocker.Tests;

public sealed class BridgePathListTests
{
    [Fact]
    public void NormalizeBridgeStringList_TreatsNullAsEmpty()
    {
        string[] normalized = MainWindow.NormalizeBridgeStringList(null);

        Assert.Empty(normalized);
    }

    [Fact]
    public void NormalizeBridgeStringList_TrimsAndFiltersBlankValues()
    {
        string[] normalized = MainWindow.NormalizeBridgeStringList([
            "  Document properties  ",
            "",
            "\t",
            "Camera data"
        ]);

        Assert.Equal([
            "Document properties",
            "Camera data"
        ], normalized);
    }

    [Fact]
    public void NormalizeBridgePathList_TreatsNullAsEmpty()
    {
        string[] normalized = MainWindow.NormalizeBridgePathList(null);

        Assert.Empty(normalized);
    }

    [Fact]
    public void NormalizeBridgePathList_TrimsAndFiltersBlankPaths()
    {
        string[] normalized = MainWindow.NormalizeBridgePathList([
            "  C:\\Temp\\payload.txt  ",
            " ",
            "\t",
            "D:\\Data\\report.pdf"
        ]);

        Assert.Equal([
            "C:\\Temp\\payload.txt",
            "D:\\Data\\report.pdf"
        ], normalized);
    }

    [Fact]
    public void NormalizeBridgePathList_ExpandsAndDeduplicatesFullPaths()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "FileLockerPathListTests");
        string relativePath = Path.Combine(basePath, ".", "payload.txt");
        string fullPath = Path.GetFullPath(relativePath);

        string[] normalized = MainWindow.NormalizeBridgePathList([
            relativePath,
            fullPath
        ], fullPaths: true);

        string singlePath = Assert.Single(normalized);
        Assert.Equal(fullPath, singlePath);
    }
}

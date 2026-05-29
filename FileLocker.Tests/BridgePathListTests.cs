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
    public void NormalizeBridgeStringList_DeduplicatesValuesAfterTrimming()
    {
        string[] normalized = MainWindow.NormalizeBridgeStringList([
            " Camera ",
            "camera",
            "",
            "\t",
            "Location"
        ]);

        Assert.Equal(["Camera", "Location"], normalized);
    }

    [Fact]
    public void NormalizeBridgeStringList_RejectsOversizedValues()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgeStringList([new string('A', MainWindow.MaxBridgeStringValueChars + 1)]));

        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgeStringList_RejectsControlCharacterValues()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgeStringList(["alpha\r\nbeta"]));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgeStringList_RejectsUnicodeFormatCharacterValues()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgeStringList(["alpha\u202Ebeta"]));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgeStringList_RejectsTooManyValues()
    {
        string[] values = Enumerable.Range(0, MainWindow.MaxBridgeStringListItems + 1)
            .Select(index => $"value-{index:D5}")
            .ToArray();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgeStringList(values));

        Assert.Contains("Too many", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void NormalizeBridgePathList_ReportsControlCharacterPathsCleanly()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgePathList(["C:\\Temp\\bad\0path.txt"], fullPaths: true));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgePathList_RejectsRelativeFullPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgePathList(["payload.txt"], fullPaths: true));

        Assert.Contains("fully qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgePathList_RejectsAlternateDataStreamPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeBridgePathList([Path.Combine(Path.GetTempPath(), "payload.txt:stream")], fullPaths: true));

        Assert.Contains("normal files or folders", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgeOperationId_StripsUnsafeCharactersAndCapsLength()
    {
        string normalized = MainWindow.NormalizeBridgeOperationId($"  op\r\n_01<>-{new string('a', 120)}  ");

        Assert.StartsWith("op_01-", normalized);
        Assert.DoesNotContain("<", normalized);
        Assert.DoesNotContain(">", normalized);
        Assert.Equal(80, normalized.Length);
    }

    [Fact]
    public void NormalizeBridgeOperationId_GeneratesValueForBlankOrUnsafeInput()
    {
        string normalized = MainWindow.NormalizeBridgeOperationId(" \r\n<> ");

        Assert.False(string.IsNullOrWhiteSpace(normalized));
        Assert.True(normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
    }
}

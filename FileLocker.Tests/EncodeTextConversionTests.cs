namespace FileLocker.Tests;

public sealed class EncodeTextConversionTests
{
    [Fact]
    public void ConvertEncodeText_RejectsOversizedInput()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ConvertEncodeText(
                new string('A', MainWindow.MaxEncodeTextInputChars + 1),
                MainWindow.EncodeTextMode.Encode,
                "Base64",
                preserveLineBreaks: true));

        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConvertEncodeText_RejectsUnsupportedFormat()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ConvertEncodeText(
                "hello",
                MainWindow.EncodeTextMode.Encode,
                "ROT13",
                preserveLineBreaks: true));

        Assert.Contains("supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConvertEncodeText_AllowsHexPrefixOnlyAtTokenStart()
    {
        string decoded = MainWindow.ConvertEncodeText(
            "0x48 0x69",
            MainWindow.EncodeTextMode.Decode,
            "Hex",
            preserveLineBreaks: true);

        Assert.Equal("Hi", decoded);
    }

    [Fact]
    public void ConvertEncodeText_RejectsEmbeddedHexPrefix()
    {
        FormatException ex = Assert.Throws<FormatException>(() =>
            MainWindow.ConvertEncodeText(
                "120x34",
                MainWindow.EncodeTextMode.Decode,
                "Hex",
                preserveLineBreaks: true));

        Assert.Contains("Hex decode failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeEncodeTextMode_RejectsUnsupportedMode()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeEncodeTextMode("preview"));

        Assert.Contains("encode or decode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeEncodeTextMode_RejectsUnsafeModeText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeEncodeTextMode("encode\u202E"));

        Assert.Contains("encode or decode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeEncodeTextFormat_RejectsOversizedFormatText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeEncodeTextFormat(new string('A', 128)));

        Assert.Contains("supported text conversion format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeEncodeTextFormat_RejectsUnsafeFormatText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.NormalizeEncodeTextFormat("Base64\r\nHex"));

        Assert.Contains("supported text conversion format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

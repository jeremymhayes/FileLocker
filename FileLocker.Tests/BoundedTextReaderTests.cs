namespace FileLocker.Tests;

public sealed class BoundedTextReaderTests
{
    [Fact]
    public async Task ReadToEndAsync_ReturnsTextWithinLimit()
    {
        using var reader = new StringReader("output");

        BoundedTextReadResult result = await BoundedTextReader.ReadToEndAsync(reader, maxChars: 6, TestContext.Current.CancellationToken);

        Assert.Equal("output", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ReadToEndAsync_DrainsAndCapsOversizedText()
    {
        using var reader = new StringReader("abcdef");

        BoundedTextReadResult result = await BoundedTextReader.ReadToEndAsync(reader, maxChars: 3, TestContext.Current.CancellationToken);

        Assert.Equal("abc", result.Text);
        Assert.True(result.Truncated);
        Assert.Equal(-1, reader.Peek());
    }

    [Fact]
    public async Task ReadToEndAsync_AllowsZeroLimit()
    {
        using var reader = new StringReader("a");

        BoundedTextReadResult result = await BoundedTextReader.ReadToEndAsync(reader, maxChars: 0, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ReadToEndAsync_RejectsNegativeLimit()
    {
        using var reader = new StringReader("output");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedTextReader.ReadToEndAsync(reader, maxChars: -1, TestContext.Current.CancellationToken));
    }
}

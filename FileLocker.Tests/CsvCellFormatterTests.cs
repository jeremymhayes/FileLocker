using FileLocker;

namespace FileLocker.Tests;

public sealed class CsvCellFormatterTests
{
    [Theory]
    [InlineData("=Launch", "'=Launch")]
    [InlineData("  @Risk", "'  @Risk")]
    [InlineData("\uFEFF=Launch", "' =Launch")]
    [InlineData("\u200B@Risk", "' @Risk")]
    [InlineData("\0=Launch", "' =Launch")]
    [InlineData("\u202E=Launch", "' =Launch")]
    [InlineData("Normal text", "Normal text")]
    public void Format_EscapesFormulaLikeValues(string value, string expected)
    {
        Assert.Equal(expected, CsvCellFormatter.Format(value));
    }

    [Fact]
    public void Format_ReplacesControlAndFormatCharactersInSafeValues()
    {
        string formatted = CsvCellFormatter.Format("alpha\0beta\u202Egamma");

        Assert.Equal("alpha beta gamma", formatted);
    }

    [Fact]
    public void Format_QuotesAndEscapesDelimitedValues()
    {
        string formatted = CsvCellFormatter.Format("alpha,\"bravo\"");

        Assert.Equal("\"alpha,\"\"bravo\"\"\"", formatted);
    }

    [Fact]
    public void Format_CanAlwaysQuoteSafeValues()
    {
        string formatted = CsvCellFormatter.Format("alpha", alwaysQuote: true);

        Assert.Equal("\"alpha\"", formatted);
    }
}

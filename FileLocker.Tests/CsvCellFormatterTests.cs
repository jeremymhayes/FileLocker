using FileLocker;

namespace FileLocker.Tests;

public sealed class CsvCellFormatterTests
{
    [Theory]
    [InlineData("=Launch", "'=Launch")]
    [InlineData("  @Risk", "'  @Risk")]
    [InlineData("\uFEFF=Launch", "'\uFEFF=Launch")]
    [InlineData("\u200B@Risk", "'\u200B@Risk")]
    [InlineData("Normal text", "Normal text")]
    public void Format_EscapesFormulaLikeValues(string value, string expected)
    {
        Assert.Equal(expected, CsvCellFormatter.Format(value));
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

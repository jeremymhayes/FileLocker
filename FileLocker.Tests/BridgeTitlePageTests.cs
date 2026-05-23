namespace FileLocker.Tests;

public sealed class BridgeTitlePageTests
{
    [Fact]
    public void NormalizeTitlePageName_UsesDashboardForBlankTitle()
    {
        Assert.Equal("Dashboard", MainWindow.NormalizeTitlePageName("   "));
    }

    [Fact]
    public void NormalizeTitlePageName_ReplacesControlCharactersWithSingleSpaces()
    {
        string pageName = MainWindow.NormalizeTitlePageName("Settings\r\nInjected\tTitle");

        Assert.Equal("Settings Injected Title", pageName);
    }

    [Fact]
    public void NormalizeTitlePageName_TruncatesLongTitle()
    {
        string pageName = MainWindow.NormalizeTitlePageName(new string('A', 100));

        Assert.Equal(80, pageName.Length);
        Assert.Equal(new string('A', 80), pageName);
    }
}

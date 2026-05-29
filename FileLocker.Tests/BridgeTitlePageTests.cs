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
    public void NormalizeTitlePageName_ReplacesUnicodeFormatCharactersWithSingleSpaces()
    {
        string pageName = MainWindow.NormalizeTitlePageName("Settings\u202EInjected");

        Assert.Equal("Settings Injected", pageName);
    }

    [Fact]
    public void NormalizeTitlePageName_TruncatesLongTitle()
    {
        string pageName = MainWindow.NormalizeTitlePageName(new string('A', 100));

        Assert.Equal(80, pageName.Length);
        Assert.Equal(new string('A', 80), pageName);
    }

    [Fact]
    public void NormalizeTitlePageName_BoundsInputBeforeScanning()
    {
        string pageName = MainWindow.NormalizeTitlePageName(new string('B', 10_000));

        Assert.Equal(new string('B', 80), pageName);
    }

    [Fact]
    public void NormalizeRestartTargetPage_AcceptsKnownPageKeys()
    {
        string targetPage = MainWindow.NormalizeRestartTargetPage("#startup-manager");

        Assert.Equal("startup-manager", targetPage);
    }

    [Fact]
    public void NormalizeRestartTargetPage_RejectsControlCharacters()
    {
        string targetPage = MainWindow.NormalizeRestartTargetPage("startup-manager\r\nregistry-fixer");

        Assert.Equal(string.Empty, targetPage);
    }

    [Fact]
    public void NormalizeRestartTargetPage_RejectsUnicodeFormatCharacters()
    {
        string targetPage = MainWindow.NormalizeRestartTargetPage("startup\u202E-manager");

        Assert.Equal(string.Empty, targetPage);
    }

    [Fact]
    public void NormalizeRestartTargetPage_BoundsInputBeforeNormalization()
    {
        string targetPage = MainWindow.NormalizeRestartTargetPage(new string('A', 10_000) + "startup-manager");

        Assert.Equal(string.Empty, targetPage);
    }
}

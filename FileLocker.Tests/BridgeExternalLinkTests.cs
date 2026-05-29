namespace FileLocker.Tests;

public sealed class BridgeExternalLinkTests
{
    [Fact]
    public void RequireExternalHttpsUrl_AcceptsHttpsUrl()
    {
        string url = MainWindow.RequireExternalHttpsUrl("https://github.com/jeremymhayes/FileLocker");

        Assert.Equal("https://github.com/jeremymhayes/FileLocker", url);
    }

    [Fact]
    public void RequireExternalHttpsUrl_TrimsBeforeValidation()
    {
        string url = MainWindow.RequireExternalHttpsUrl("  https://github.com/jeremymhayes/FileLocker  ");

        Assert.Equal("https://github.com/jeremymhayes/FileLocker", url);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsHttpUrl()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl("http://github.com/jeremymhayes/FileLocker"));

        Assert.Equal("Only HTTPS links can be opened.", ex.Message);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsCredentialedHttpsUrl()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl("https://user:token@example.com/release"));

        Assert.Equal("HTTPS links with embedded credentials cannot be opened.", ex.Message);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsRelativeUrl()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl("/settings"));

        Assert.Equal("Only HTTPS links can be opened.", ex.Message);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsControlCharacters()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl("https://github.com/jeremymhayes/FileLocker\r\nhttps://example.test"));

        Assert.Equal("Only HTTPS links can be opened.", ex.Message);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsUnicodeFormatCharacters()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl("https://github.com/jeremymhayes/FileLocker\u202E"));

        Assert.Equal("Only HTTPS links can be opened.", ex.Message);
    }

    [Fact]
    public void RequireExternalHttpsUrl_RejectsOversizedUrl()
    {
        string url = $"https://github.com/jeremymhayes/FileLocker/{new string('a', 2048)}";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.RequireExternalHttpsUrl(url));

        Assert.Equal("Only HTTPS links can be opened.", ex.Message);
    }
}

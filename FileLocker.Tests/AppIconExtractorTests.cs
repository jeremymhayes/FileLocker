namespace FileLocker.Tests;

public sealed class AppIconExtractorTests
{
    [Fact]
    public void ParseDisplayIcon_ExpandsEnvironmentVariablesAndReadsResourceIndex()
    {
        string variableName = $"FILELOCKER_ICON_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, @"C:\FileLockerIconTest");

        try
        {
            string? path = AppIconExtractor.ParseDisplayIcon($@"@%{variableName}%\Vendor\App.exe,-12", out int index);

            Assert.Equal(@"C:\FileLockerIconTest\Vendor\App.exe", path);
            Assert.Equal(-12, index);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void ParseDisplayIcon_PreservesQuotedPathsWithCommas()
    {
        string? path = AppIconExtractor.ParseDisplayIcon(@"""C:\Program Files\Vendor, Inc\App.exe"",0", out int index);

        Assert.Equal(@"C:\Program Files\Vendor, Inc\App.exe", path);
        Assert.Equal(0, index);
    }

    [Theory]
    [InlineData("@")]
    [InlineData("@   ")]
    [InlineData(@"""  "",0")]
    public void ParseDisplayIcon_ReturnsNullWhenNoPathRemains(string displayIcon)
    {
        string? path = AppIconExtractor.ParseDisplayIcon(displayIcon, out int index);

        Assert.Null(path);
        Assert.Equal(0, index);
    }

    [Fact]
    public void ParseDisplayIcon_ReturnsNullForAlternateDataStreamPath()
    {
        string? path = AppIconExtractor.ParseDisplayIcon(@"C:\Program Files\Vendor\App.exe:stream,0", out int index);

        Assert.Null(path);
        Assert.Equal(0, index);
    }

    [Fact]
    public void ParseDisplayIcon_ReturnsNullForRelativePath()
    {
        string? path = AppIconExtractor.ParseDisplayIcon("App.exe,0", out int index);

        Assert.Null(path);
        Assert.Equal(0, index);
    }

    [Fact]
    public void ParseDisplayIcon_ReturnsNullForUnicodeFormatPath()
    {
        string? path = AppIconExtractor.ParseDisplayIcon(
            @"C:\Program Files\Vendor\App" + "\u202E" + ".exe,0",
            out int index);

        Assert.Null(path);
        Assert.Equal(0, index);
    }

    [Fact]
    public void ExtractExecutableFromCommand_HandlesUnquotedPathWithSpaces()
    {
        string? path = AppIconExtractor.ExtractExecutableFromCommand(@"C:\Program Files\Vendor\App\app.exe /remove");

        Assert.Equal(@"C:\Program Files\Vendor\App\app.exe", path);
    }

    [Fact]
    public void ExtractExecutableFromCommand_RejectsGenericUninstallerStub()
    {
        string? path = AppIconExtractor.ExtractExecutableFromCommand(@"""C:\Program Files\Vendor\unins000.exe""");

        Assert.Null(path);
    }

    [Fact]
    public void ExtractExecutableFromCommand_RejectsAlternateDataStreamPath()
    {
        string? path = AppIconExtractor.ExtractExecutableFromCommand(@"""C:\Program Files\Vendor\App.exe:stream""");

        Assert.Null(path);
    }

    [Fact]
    public void ExtractExecutableFromCommand_RejectsRelativePath()
    {
        string? path = AppIconExtractor.ExtractExecutableFromCommand("App.exe /remove");

        Assert.Null(path);
    }

    [Fact]
    public void ExtractExecutableFromCommand_RejectsUnicodeFormatPath()
    {
        string? path = AppIconExtractor.ExtractExecutableFromCommand(
            @"""C:\Program Files\Vendor\App" + "\u202E" + @".exe""");

        Assert.Null(path);
    }

    [Fact]
    public void TryGetIconDataUri_RejectsOversizedIcoFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string iconPath = Path.Combine(root, "app.ico");

        try
        {
            Directory.CreateDirectory(root);
            using (FileStream stream = File.Create(iconPath))
            {
                stream.SetLength(4 * 1024 * 1024 + 1);
            }

            string? dataUri = AppIconExtractor.TryGetIconDataUri(iconPath, string.Empty, string.Empty);

            Assert.Null(dataUri);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

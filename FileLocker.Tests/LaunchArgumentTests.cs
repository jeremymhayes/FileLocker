namespace FileLocker.Tests;

public sealed class LaunchArgumentTests
{
    [Fact]
    public void ParseLaunchArguments_TrimsDeduplicatesAndKeepsSafePageAction()
    {
        LaunchArguments parsed = App.ParseLaunchArguments([
            "FileLocker.exe",
            "  C:\\Temp\\demo.locked  ",
            "c:\\temp\\DEMO.locked",
            "--PAGE=decrypt"
        ]);

        string path = Assert.Single(parsed.Paths);
        Assert.Equal("C:\\Temp\\demo.locked", path);
        Assert.Equal("--page=decrypt", parsed.Action);
    }

    [Fact]
    public void ParseLaunchArguments_IgnoresUnsafeOrOversizedInputs()
    {
        string oversizedPath = new('p', MainWindow.MaxBridgeStringValueChars + 1);
        string oversizedAction = "--page=" + new string('a', 129);

        LaunchArguments parsed = App.ParseLaunchArguments([
            "FileLocker.exe",
            oversizedPath,
            "--page=../settings",
            oversizedAction,
            "D:\\Data\\payload.txt"
        ]);

        string path = Assert.Single(parsed.Paths);
        Assert.Equal("D:\\Data\\payload.txt", path);
        Assert.Null(parsed.Action);
    }

    [Fact]
    public void ParseLaunchArguments_KeepsLastValidActionWhenLaterFlagsAreUnsupported()
    {
        LaunchArguments parsed = App.ParseLaunchArguments([
            "FileLocker.exe",
            "--page=encrypt",
            "--unsupported"
        ]);

        Assert.Equal("--page=encrypt", parsed.Action);
    }

    [Fact]
    public void ParseLaunchArguments_AllowsRecycleBinShredAction()
    {
        LaunchArguments parsed = App.ParseLaunchArguments([
            "FileLocker.exe",
            "--recycle-bin-shred"
        ]);

        Assert.Equal("--recycle-bin-shred", parsed.Action);
    }

    [Fact]
    public void ShouldPromptForAdministratorOnLaunch_ReturnsTrueForStandardUser()
    {
        Assert.True(App.ShouldPromptForAdministratorOnLaunch(isAdministrator: false));
    }

    [Fact]
    public void ShouldPromptForAdministratorOnLaunch_ReturnsFalseForAdministrator()
    {
        Assert.False(App.ShouldPromptForAdministratorOnLaunch(isAdministrator: true));
    }

    [Fact]
    public void BuildElevatedRelaunchArguments_QuotesOriginalArguments()
    {
        string arguments = App.BuildElevatedRelaunchArguments([
            "FileLocker.exe",
            "C:\\Temp\\plain.txt",
            "C:\\Temp\\space name.txt",
            "--page=partition-cleaner"
        ]);

        Assert.Equal("\"C:\\Temp\\plain.txt\" \"C:\\Temp\\space name.txt\" \"--page=partition-cleaner\"", arguments);
    }

    [Fact]
    public void ParseLaunchArguments_CapsPathCountAfterDeduplication()
    {
        string[] args = Enumerable.Range(0, MainWindow.MaxBridgeStringListItems + 25)
            .Select(index => $"C:\\Temp\\payload-{index:D5}.locked")
            .Prepend("FileLocker.exe")
            .ToArray();

        LaunchArguments parsed = App.ParseLaunchArguments(args);

        Assert.Equal(MainWindow.MaxBridgeStringListItems, parsed.Paths.Count);
    }
}

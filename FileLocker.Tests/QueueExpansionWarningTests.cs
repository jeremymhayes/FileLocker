namespace FileLocker.Tests;

public sealed class QueueExpansionWarningTests
{
    [Fact]
    public void NormalizeQueueWarning_RedactsAndCapsWarnings()
    {
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string warning = MainWindow.NormalizeQueueWarning($"{profilePath}\\Documents\\secret.txt {new string('A', 4096)}");

        Assert.True(warning.Length <= MainWindow.MaxQueueExpansionWarningChars);
        Assert.DoesNotContain(profilePath, warning);
        Assert.Contains("%USERPROFILE%", warning);
        Assert.EndsWith("Warning truncated.", warning);
    }

    [Fact]
    public void AddQueueWarning_CapsWarningCount()
    {
        var warnings = new List<string>();

        for (int index = 0; index < MainWindow.MaxQueueExpansionWarnings + 10; index++)
        {
            MainWindow.AddQueueWarning(warnings, $"Warning {index}");
        }

        Assert.Equal(MainWindow.MaxQueueExpansionWarnings + 1, warnings.Count);
        Assert.Equal("Additional folder scan warnings were omitted.", warnings[^1]);
    }
}

using FileLocker;

namespace FileLocker.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactPath_KeepsOnlyLeafName()
    {
        string redacted = SensitiveDataRedactor.RedactPath(@"C:\Users\tester\Secrets\plan.txt");

        Assert.Equal(Path.Combine("[redacted]", "plan.txt"), redacted);
    }

    [Fact]
    public void RedactPath_HidesRootOnlyPath()
    {
        string redacted = SensitiveDataRedactor.RedactPath(@"D:\");

        Assert.Equal("[redacted]", redacted);
    }

    [Fact]
    public void RedactPath_ToleratesControlCharacters()
    {
        string redacted = SensitiveDataRedactor.RedactPath(@"D:\Vault\bad" + "\0" + "secret.txt");

        Assert.DoesNotContain('\0', redacted);
        Assert.StartsWith("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactPath_ToleratesUnicodeFormatCharacters()
    {
        string redacted = SensitiveDataRedactor.RedactPath(@"D:\Vault\bad" + "\u202E" + "secret.txt");

        Assert.DoesNotContain('\u202E', redacted);
        Assert.StartsWith("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactMessage_RedactsQuotedAbsolutePaths()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open 'D:\Vault\secret.txt'.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), redacted);
    }

    [Fact]
    public void RedactMessage_RedactsMalformedQuotedAbsolutePaths()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open 'D:\Vault\bad" + "\0" + "secret.txt'.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.DoesNotContain('\0', redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void RedactMessage_ReplacesUnicodeFormatCharactersInNonPathText()
    {
        string redacted = SensitiveDataRedactor.RedactMessage("Operation\u202Efailed");

        Assert.Equal("Operation failed", redacted);
    }

    [Fact]
    public void RedactMessage_ReplacesControlCharactersInNonPathText()
    {
        string redacted = SensitiveDataRedactor.RedactMessage("Operation\r\nfailed");

        Assert.Equal("Operation  failed", redacted);
    }

    [Fact]
    public void RedactMessage_BoundsOversizedMessages()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(new string('A', SensitiveDataRedactor.MaxRedactedMessageChars + 1024));

        Assert.True(redacted.Length <= SensitiveDataRedactor.MaxRedactedMessageChars);
        Assert.EndsWith("Message truncated.", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsQuotedAbsolutePathsWithUnicodeFormatCharacters()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open 'D:\Vault\bad" + "\u202E" + "secret.txt'.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.DoesNotContain('\u202E', redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUnquotedAbsolutePaths()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open D:\Vault\secret.txt.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), redacted);
    }

    [Fact]
    public void RedactMessage_RedactsMalformedUnquotedAbsolutePaths()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open D:\Vault\bad" + "\0" + "secret.txt.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.DoesNotContain('\0', redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUnquotedDriveRoot()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not access D:\.");

        Assert.DoesNotContain(@"D:\", redacted);
        Assert.Contains("[redacted].", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUnquotedUncShareRoot()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not access \\server\share\.");

        Assert.DoesNotContain(@"\\server\share", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted].", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUnquotedAbsolutePathsWithSpaces()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open D:\Vault Folder\secret file.txt.");

        Assert.DoesNotContain(@"D:\Vault Folder", redacted);
        Assert.Contains(Path.Combine("[redacted]", "secret file.txt"), redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUnquotedAbsoluteDirectoryPathsWithSpacedLeaf()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open D:\Vault Folder\Secret Folder.");

        Assert.DoesNotContain(@"D:\Vault Folder", redacted);
        Assert.Contains(Path.Combine("[redacted]", "Secret Folder"), redacted);
    }

    [Fact]
    public void RedactMessage_PreservesSentenceAfterUnquotedAbsolutePath()
    {
        string redacted = SensitiveDataRedactor.RedactMessage(@"Could not open D:\Vault\secret.txt because access was denied.");

        Assert.DoesNotContain(@"D:\Vault", redacted);
        Assert.Contains(Path.Combine("[redacted]", "secret.txt"), redacted);
        Assert.Contains("because access was denied.", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsKnownUserRoots()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string message = Path.Combine(userProfile, "Documents", "secret.txt");

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain(userProfile, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Documents", redacted);
        Assert.Contains("%USERPROFILE%", redacted);
        Assert.Contains("secret.txt", redacted);
    }

    [Fact]
    public void RedactMessage_DoesNotTreatSimilarUserProfilePrefixAsKnownRoot()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string siblingProfilePath = userProfile + "y" + Path.DirectorySeparatorChar + "notes.txt";

        string redacted = SensitiveDataRedactor.RedactMessage(siblingProfilePath);

        Assert.DoesNotContain("%USERPROFILE%", redacted);
        Assert.Contains(Path.Combine("[redacted]", "notes.txt"), redacted);
    }

    [Fact]
    public void RedactMessage_PrefersMoreSpecificLocalAppDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string message = Path.Combine(localAppData, "FileLocker", "Updater", "installer.download");

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain(localAppData, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfile, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileLocker", redacted);
        Assert.DoesNotContain("Updater", redacted);
        Assert.Contains("%LOCALAPPDATA%", redacted);
        Assert.Contains("installer.download", redacted);
    }

    [Fact]
    public void RedactMessage_PrefersMoreSpecificRoamingAppDataRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string message = Path.Combine(appData, "Vendor", "App", "settings.json");

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain(appData, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfile, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vendor", redacted);
        Assert.DoesNotContain("App", redacted);
        Assert.Contains("%APPDATA%", redacted);
        Assert.Contains("settings.json", redacted);
    }

    [Fact]
    public void RedactMessage_RedactsUserProfileEnvironmentTokenPaths()
    {
        string message = @"Could not open %USERPROFILE%\Documents\Secrets\plan.txt.";

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain("Documents", redacted);
        Assert.DoesNotContain("Secrets", redacted);
        Assert.Contains(Path.Combine("%USERPROFILE%", "plan.txt"), redacted);
    }

    [Fact]
    public void RedactMessage_RedactsLocalAppDataEnvironmentTokenPaths()
    {
        string message = @"Could not open %LOCALAPPDATA%\FileLocker\Updater\installer.download.";

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain("FileLocker", redacted);
        Assert.DoesNotContain("Updater", redacted);
        Assert.Contains(Path.Combine("%LOCALAPPDATA%", "installer.download"), redacted);
    }

    [Fact]
    public void RedactMessage_RedactsAppDataEnvironmentTokenPaths()
    {
        string message = @"Could not open %APPDATA%\Vendor\App\settings.json.";

        string redacted = SensitiveDataRedactor.RedactMessage(message);

        Assert.DoesNotContain("Vendor", redacted);
        Assert.DoesNotContain("App", redacted);
        Assert.Contains(Path.Combine("%APPDATA%", "settings.json"), redacted);
    }
}

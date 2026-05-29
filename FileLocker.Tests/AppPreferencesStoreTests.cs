using System.Text;

namespace FileLocker.Tests;

public sealed class AppPreferencesStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReplacesExistingFileAndRemovesTempFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8, TestContext.Current.CancellationToken);

        await AppPreferencesStore.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReplacesReadOnlyExistingFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8, TestContext.Current.CancellationToken);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        await AppPreferencesStore.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.False((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
    }

    [Fact]
    public async Task LoadAsync_NormalizesUnsupportedPersistedValues()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "OutputTimestampPolicy": "legacy timestamp mode",
              "CustomEncryptOutputDirectory": null,
              "CustomDecryptOutputDirectory": null,
              "ThemePreference": 99
            }
            """,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        AppPreferences preferences = await AppPreferencesStore.LoadAsync(_rootPath);

        Assert.Equal("Current time", preferences.OutputTimestampPolicy);
        Assert.Equal(string.Empty, preferences.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, preferences.CustomDecryptOutputDirectory);
        Assert.Equal(ThemePreference.Dark, preferences.ThemePreference);
    }

    [Fact]
    public async Task LoadAsync_AcceptsStringThemePreference()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "OutputTimestampPolicy": "Randomize",
              "ThemePreference": "Light"
            }
            """,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        AppPreferences preferences = await AppPreferencesStore.LoadAsync(_rootPath);

        Assert.Equal("Randomize", preferences.OutputTimestampPolicy);
        Assert.Equal(ThemePreference.Light, preferences.ThemePreference);
    }

    [Fact]
    public async Task LoadAsync_ReturnsDefaultsForOversizedPreferencesFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            new string(' ', 256 * 1024 + 1),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        AppPreferences preferences = await AppPreferencesStore.LoadAsync(_rootPath);

        Assert.False(preferences.IncognitoMode);
        Assert.Equal("Current time", preferences.OutputTimestampPolicy);
        Assert.Equal(ThemePreference.Dark, preferences.ThemePreference);
    }

    [Fact]
    public async Task LoadAsync_MigratesNumericLegacyHistoryPrivacyOff()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "HistoryPrivacyMode": 0
            }
            """,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        AppPreferences preferences = await AppPreferencesStore.LoadAsync(_rootPath);

        Assert.True(preferences.IncognitoMode);
        Assert.Equal(HistoryPrivacyMode.Off, preferences.HistoryPrivacyMode);
    }

    [Fact]
    public async Task SaveAsync_WritesReadableThemePreference()
    {
        var preferences = new AppPreferences
        {
            ThemePreference = ThemePreference.System
        };

        await AppPreferencesStore.SaveAsync(_rootPath, preferences);

        string json = await File.ReadAllTextAsync(
            Path.Combine(_rootPath, "preferences.json"),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        Assert.Contains("\"ThemePreference\": \"System\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadAsync_RejectsBlankAppDataDirectory(string appDataDirectory)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.LoadAsync(appDataDirectory));
    }

    [Fact]
    public async Task LoadAsync_RejectsRelativeAppDataDirectory()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.LoadAsync("FileLockerPreferences"));

        Assert.Equal("appDataDirectory", ex.ParamName);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnicodeFormatAppDataDirectory()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.LoadAsync(Path.Combine(_rootPath, "Prefs" + "\u202E")));

        Assert.Equal("appDataDirectory", ex.ParamName);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePreferences_CanonicalizesCaseInsensitiveTimestampPolicy()
    {
        var preferences = new AppPreferences
        {
            OutputTimestampPolicy = "randomize",
            ThemePreference = ThemePreference.Light
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.Equal("Randomize", normalized.OutputTimestampPolicy);
        Assert.Equal(ThemePreference.Light, normalized.ThemePreference);
    }

    [Theory]
    [InlineData(null, "Current time")]
    [InlineData("", "Current time")]
    [InlineData("legacy mode", "Current time")]
    [InlineData(" preserve source timestamps ", "Preserve source timestamps")]
    public void NormalizeOutputTimestampPolicy_ReturnsCanonicalPolicy(string? policy, string expected)
    {
        string normalized = AppPreferencesStore.NormalizeOutputTimestampPolicy(policy);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizePreferences_TrimsCustomOutputDirectories()
    {
        var preferences = new AppPreferences
        {
            CustomEncryptOutputDirectory = "  C:\\Encrypted  ",
            CustomDecryptOutputDirectory = "\tD:\\Restored\t"
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.Equal("C:\\Encrypted", normalized.CustomEncryptOutputDirectory);
        Assert.Equal("D:\\Restored", normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_CanonicalizesCustomOutputDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string encryptDirectory = Path.Combine(root, "Encrypt", "..", "Locked");
        string decryptDirectory = Path.Combine(root, "Decrypt", "..", "Restored");
        var preferences = new AppPreferences
        {
            CustomEncryptOutputDirectory = encryptDirectory,
            CustomDecryptOutputDirectory = decryptDirectory
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "Locked")), normalized.CustomEncryptOutputDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "Restored")), normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_ClearsInvalidCustomOutputDirectories()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "C:\\Bad\0Path",
            UseCustomDecryptOutputDirectory = true,
            CustomDecryptOutputDirectory = new string('D', AppPreferencesStore.MaxPreferenceDirectoryChars + 1)
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_ClearsCustomOutputDirectoriesWithControlCharacters()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "C:\\Encrypted\r\nInjected",
            UseCustomDecryptOutputDirectory = true,
            CustomDecryptOutputDirectory = "D:\\Restored\tInjected"
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_ClearsCustomOutputDirectoriesWithUnicodeFormatCharacters()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "C:\\Encrypted\u202EInjected",
            UseCustomDecryptOutputDirectory = true,
            CustomDecryptOutputDirectory = "D:\\Restored\u202EInjected"
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_ClearsCustomOutputDirectoriesWithAlternateDataStreams()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "C:\\Encrypted:stream",
            UseCustomDecryptOutputDirectory = true,
            CustomDecryptOutputDirectory = "D:\\Restored:stream"
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_ClearsRelativeCustomOutputDirectories()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "Encrypted",
            UseCustomDecryptOutputDirectory = true,
            CustomDecryptOutputDirectory = "Restored"
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomDecryptOutputDirectory);
    }

    [Fact]
    public void NormalizePreferences_DisablesBlankCustomEncryptOutputDirectory()
    {
        var preferences = new AppPreferences
        {
            UseCustomEncryptOutputDirectory = true,
            CustomEncryptOutputDirectory = "   "
        };

        AppPreferences normalized = AppPreferencesStore.NormalizePreferences(preferences);

        Assert.False(normalized.UseCustomEncryptOutputDirectory);
        Assert.Equal(string.Empty, normalized.CustomEncryptOutputDirectory);
    }

    [Fact]
    public async Task SaveAsync_RejectsNullPreferences()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AppPreferencesStore.SaveAsync(_rootPath, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAsync_RejectsBlankAppDataDirectory(string appDataDirectory)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.SaveAsync(appDataDirectory, new AppPreferences()));
    }

    [Fact]
    public async Task SaveAsync_RejectsAlternateDataStreamAppDataDirectory()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.SaveAsync(Path.Combine(_rootPath, "Prefs:stream"), new AppPreferences()));

        Assert.Equal("appDataDirectory", ex.ParamName);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RejectsRelativeAppDataDirectory()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            AppPreferencesStore.SaveAsync("FileLockerPreferences", new AppPreferences()));

        Assert.Equal("appDataDirectory", ex.ParamName);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePreferences_RejectsNullPreferences()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AppPreferencesStore.NormalizePreferences(null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            foreach (string file in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

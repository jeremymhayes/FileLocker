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

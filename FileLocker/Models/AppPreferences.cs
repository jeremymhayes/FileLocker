using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileLocker;

public enum UserExperienceLevel
{
    Beginner,
    Intermediate,
    Advanced
}

public enum HistoryPrivacyMode
{
    Off,
    Redacted,
    Full
}

public enum ThemePreference
{
    System,
    Dark,
    Light
}

public sealed class AppPreferences
{
    public bool HasSelectedExperienceLevel { get; set; }

    public UserExperienceLevel ExperienceLevel { get; set; } = UserExperienceLevel.Beginner;

    public HistoryPrivacyMode HistoryPrivacyMode { get; set; } = HistoryPrivacyMode.Redacted;

    public bool IncludeFullPathsInExports { get; set; }

    public string OutputTimestampPolicy { get; set; } = "Current time";

    public bool UseCustomEncryptOutputDirectory { get; set; }

    public string CustomEncryptOutputDirectory { get; set; } = string.Empty;

    public bool UseCustomDecryptOutputDirectory { get; set; } = true;

    public string CustomDecryptOutputDirectory { get; set; } = string.Empty;

    public ThemePreference ThemePreference { get; set; } = ThemePreference.Dark;
}

internal static class AppPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<AppPreferences> LoadAsync(string appDataDirectory)
    {
        string path = GetPreferencesPath(appDataDirectory);
        if (!File.Exists(path))
        {
            return new AppPreferences();
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions) ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    internal static async Task SaveAsync(string appDataDirectory, AppPreferences preferences)
    {
        Directory.CreateDirectory(appDataDirectory);
        string json = JsonSerializer.Serialize(preferences, JsonOptions);
        await File.WriteAllTextAsync(GetPreferencesPath(appDataDirectory), json, Encoding.UTF8);
    }

    internal static byte[] ProtectForCurrentUser(byte[] bytes)
    {
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    internal static byte[] UnprotectForCurrentUser(byte[] bytes)
    {
        return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    private static string GetPreferencesPath(string appDataDirectory)
    {
        return Path.Combine(appDataDirectory, "preferences.json");
    }
}

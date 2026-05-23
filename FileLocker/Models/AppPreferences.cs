using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public bool IncognitoMode { get; set; }

    [JsonIgnore]
    public bool HasSelectedExperienceLevel { get; set; } = true;

    [JsonIgnore]
    public UserExperienceLevel ExperienceLevel { get; set; } = UserExperienceLevel.Advanced;

    [JsonIgnore]
    public HistoryPrivacyMode HistoryPrivacyMode
    {
        get => IncognitoMode ? HistoryPrivacyMode.Off : HistoryPrivacyMode.Redacted;
        set => IncognitoMode = value == HistoryPrivacyMode.Off;
    }

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
    private static readonly string[] ValidOutputTimestampPolicies =
    [
        "Current time",
        "Preserve source timestamps",
        "Randomize"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<ThemePreference>()
        }
    };

    internal static async Task<AppPreferences> LoadAsync(string appDataDirectory)
    {
        string directory = NormalizeAppDataDirectory(appDataDirectory);
        string path = GetPreferencesPath(directory);
        if (!File.Exists(path))
        {
            return new AppPreferences();
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            AppPreferences preferences = JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions) ?? new AppPreferences();
            return NormalizePreferences(ApplyLegacyMigration(preferences, json));
        }
        catch
        {
            return new AppPreferences();
        }
    }

    internal static async Task SaveAsync(string appDataDirectory, AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        string directory = NormalizeAppDataDirectory(appDataDirectory);
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(NormalizePreferences(preferences), JsonOptions);
        await WriteAllTextAtomicallyAsync(GetPreferencesPath(directory), json, Encoding.UTF8);
    }

    internal static byte[] ProtectForCurrentUser(byte[] bytes)
    {
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    internal static byte[] UnprotectForCurrentUser(byte[] bytes)
    {
        return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    internal static async Task WriteAllTextAtomicallyAsync(string path, string contents, Encoding encoding)
    {
        await FileWriteService.WriteAllTextAtomicallyAsync(path, contents, encoding);
    }

    internal static async Task WriteAllBytesAtomicallyAsync(string path, byte[] bytes)
    {
        await FileWriteService.WriteAllBytesAtomicallyAsync(path, bytes);
    }

    private static string GetPreferencesPath(string appDataDirectory) => Path.Combine(appDataDirectory, "preferences.json");

    private static string NormalizeAppDataDirectory(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        return Path.GetFullPath(appDataDirectory.Trim());
    }

    internal static AppPreferences NormalizePreferences(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!ValidOutputTimestampPolicies.Contains(preferences.OutputTimestampPolicy, StringComparer.OrdinalIgnoreCase))
        {
            preferences.OutputTimestampPolicy = "Current time";
        }
        else
        {
            preferences.OutputTimestampPolicy = ValidOutputTimestampPolicies
                .First(policy => string.Equals(policy, preferences.OutputTimestampPolicy, StringComparison.OrdinalIgnoreCase));
        }

        preferences.CustomEncryptOutputDirectory ??= string.Empty;
        preferences.CustomDecryptOutputDirectory ??= string.Empty;

        if (!Enum.IsDefined(preferences.ThemePreference))
        {
            preferences.ThemePreference = ThemePreference.Dark;
        }

        return preferences;
    }

    private static AppPreferences ApplyLegacyMigration(AppPreferences preferences, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            bool hasIncognitoMode = TryGetProperty(root, nameof(AppPreferences.IncognitoMode), out _);
            if (!hasIncognitoMode &&
                TryGetProperty(root, nameof(AppPreferences.HistoryPrivacyMode), out JsonElement historyMode) &&
                string.Equals(historyMode.GetString(), nameof(HistoryPrivacyMode.Off), StringComparison.OrdinalIgnoreCase))
            {
                preferences.IncognitoMode = true;
            }
        }
        catch
        {
            return preferences;
        }

        return preferences;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

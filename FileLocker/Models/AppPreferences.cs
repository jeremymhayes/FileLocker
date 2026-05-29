using System;
using System.IO;
using System.Globalization;
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

    public string OutputTimestampPolicy { get; set; } = AppPreferencesStore.CurrentTimeTimestampPolicy;

    public bool UseCustomEncryptOutputDirectory { get; set; }

    public string CustomEncryptOutputDirectory { get; set; } = string.Empty;

    public bool UseCustomDecryptOutputDirectory { get; set; } = true;

    public string CustomDecryptOutputDirectory { get; set; } = string.Empty;

    public ThemePreference ThemePreference { get; set; } = ThemePreference.Dark;
}

internal static class AppPreferencesStore
{
    internal const string CurrentTimeTimestampPolicy = "Current time";
    internal const string PreserveSourceTimestampsPolicy = "Preserve source timestamps";
    internal const string RandomizeTimestampPolicy = "Randomize";
    internal const long MaxPreferencesJsonBytes = 256 * 1024;
    internal const int MaxPreferenceDirectoryChars = 4096;

    private static readonly string[] ValidOutputTimestampPolicies =
    [
        CurrentTimeTimestampPolicy,
        PreserveSourceTimestampsPolicy,
        RandomizeTimestampPolicy
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
            string json = await BoundedFileReader.ReadAllUtf8TextAsync(path, MaxPreferencesJsonBytes);
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
        string trimmed = appDataDirectory.Trim();
        if (trimmed.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
            !Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("App data directory is invalid.", nameof(appDataDirectory));
        }

        try
        {
            string fullPath = Path.GetFullPath(trimmed);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("App data directory is invalid.", nameof(appDataDirectory));
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("App data directory is invalid.", nameof(appDataDirectory), ex);
        }
    }

    internal static AppPreferences NormalizePreferences(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        preferences.OutputTimestampPolicy = NormalizeOutputTimestampPolicy(preferences.OutputTimestampPolicy);

        preferences.CustomEncryptOutputDirectory = NormalizePreferenceDirectory(preferences.CustomEncryptOutputDirectory);
        preferences.CustomDecryptOutputDirectory = NormalizePreferenceDirectory(preferences.CustomDecryptOutputDirectory);
        if (preferences.UseCustomEncryptOutputDirectory && string.IsNullOrWhiteSpace(preferences.CustomEncryptOutputDirectory))
        {
            preferences.UseCustomEncryptOutputDirectory = false;
        }

        if (!Enum.IsDefined(preferences.ThemePreference))
        {
            preferences.ThemePreference = ThemePreference.Dark;
        }

        return preferences;
    }

    internal static string NormalizePreferenceDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        string trimmed = directory.Trim();
        if (trimmed.Length > MaxPreferenceDirectoryChars)
        {
            return string.Empty;
        }

        if (trimmed.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return string.Empty;
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return string.Empty;
            }

            string fullPath = Path.GetFullPath(trimmed);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    internal static string NormalizeOutputTimestampPolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return CurrentTimeTimestampPolicy;
        }

        return ValidOutputTimestampPolicies.FirstOrDefault(
                validPolicy => string.Equals(validPolicy, policy.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? CurrentTimeTimestampPolicy;
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
                IsLegacyHistoryPrivacyOff(historyMode))
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

    private static bool IsLegacyHistoryPrivacyOff(JsonElement historyMode)
    {
        return historyMode.ValueKind switch
        {
            JsonValueKind.String => string.Equals(historyMode.GetString(), nameof(HistoryPrivacyMode.Off), StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => historyMode.TryGetInt32(out int value) && value == (int)HistoryPrivacyMode.Off,
            _ => false
        };
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

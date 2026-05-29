using FileLocker;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Konscious.Security.Cryptography;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;


namespace FileLocker
{
    public sealed partial class MainWindow : Window
    {
        private void InitializeUiState(FrameworkElement root)
        {
            FileListBox.ItemsSource = FileList;
            PreflightListView.ItemsSource = _preflightItems;
            RecentJobsListView.ItemsSource = _jobHistoryItems;
            HashRecentHashesListView.ItemsSource = RecentHashItems;

            var passwordStrength = CalculatePasswordStrength(string.Empty);
            PasswordStrengthBar.Value = passwordStrength.Score;
            PasswordStrengthText.Text = passwordStrength.Feedback;
            PasswordStrengthBar.Foreground = new SolidColorBrush(passwordStrength.BarColor);

            RefreshProfileCombo();
            RefreshHistoryItems();
            _ = LoadProfilesAsync();
            _ = LoadHistoryAsync();
            UpdateThemeToggleVisual();
            AnimateDropPanel(false);
            DropLabel.Text = DefaultDropLabelText;
            DropLabel.FontWeight = FontWeights.SemiBold;
            ApplyPreferencesToControls();
            SafetyOption_Toggled(RemoveOriginalsToggle, new RoutedEventArgs());
            ConfigureModeOptions();
            RefreshQueueSummary();
            RefreshPreflightPreview();
            UpdateStatusLabel();
            _isUiReady = true;
            ApplyThemePreference(_themePreference, persist: false, syncControls: true);
            ApplyExperienceMode(_currentExperienceLevel, persist: false, showAdvancedWarning: false);
            ApplyInspectorView("Setup");
            UpdateProfilePresentation(FindProfile(ProfileCombo.SelectedItem as string));
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            UpdatePayloadInspectorPreview();
            ApplyResponsiveWorkspaceLayout(WorkspaceSplitGrid.ActualWidth);
            ApplyHeroLayout(DropPanelContentGrid.ActualWidth);
            UpdateAboutMenuInfo();
            UpdateFirstLaunchExperience();
            UpdateHeaderContext();
            InitializeHashFilesView();
            InitializeDashboardShell();
        }

        private void ApplyLaunchArguments(IReadOnlyList<string>? launchPaths, string? launchAction)
        {
            if (launchPaths == null || launchPaths.Count == 0)
            {
                return;
            }

            AddFilesToList(launchPaths.ToArray());
            NavigateToSection(AppSection.EncryptFiles, announce: false);

            string actionMessage = launchAction switch
            {
                "--decrypt" => "Explorer launch queued items for decryption. Review settings and press Decrypt.",
                "--verify" => "Explorer launch queued items for verify-only inspection. Review settings and press Verify.",
                "--rotate" => "Explorer launch queued items for key rotation. Review settings and press Rotate Keys.",
                _ => "Explorer launch queued items for encryption. Review settings and press Encrypt."
            };

            SetStatus(actionMessage);
        }

        private static string GetAppDataDirectory()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileLocker");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string GetProfilesPath() => Path.Combine(GetAppDataDirectory(), "profiles.json");
        private static string GetProtectedProfilesPath() => Path.Combine(GetAppDataDirectory(), "profiles.protected");

        private static string GetHistoryPath() => Path.Combine(GetAppDataDirectory(), "history.json");

        private static string GetProtectedHistoryPath() => Path.Combine(GetAppDataDirectory(), "history.protected");

        private static IEnumerable<EncryptionProfile> GetBuiltInProfiles()
        {
            return
            [
                new EncryptionProfile
                {
                    Name = "Recommended",
                    Description = $"Balanced default. {EncryptionAlgorithmCatalog.Aes256Gcm}, verify writes, keep originals, and avoid destructive cleanup.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm),
                    CompressFiles = true,
                    ScrambleNames = false,
                    UseSteganography = false,
                    RandomizeMetadata = false,
                    RemoveOriginalsAfterSuccess = false,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                },
                new EncryptionProfile
                {
                    Name = "Private Archive",
                    Description = "Good for long-term private storage. Scrambles names and randomizes metadata while keeping source files.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm),
                    CompressFiles = true,
                    ScrambleNames = true,
                    UseSteganography = false,
                    RandomizeMetadata = true,
                    RemoveOriginalsAfterSuccess = false,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                },
                new EncryptionProfile
                {
                    Name = "Fast Local Lock",
                    Description = "Optimized for fast software encryption on already-compressed media. Keeps originals and skips compression.",
                    Algorithm = EncryptionAlgorithmCatalog.ChaCha20Poly1305,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.ChaCha20Poly1305),
                    CompressFiles = false,
                    ScrambleNames = false,
                    UseSteganography = false,
                    RandomizeMetadata = false,
                    RemoveOriginalsAfterSuccess = false,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                },
                new EncryptionProfile
                {
                    Name = "Transfer Copy",
                    Description = "Creates an encrypted payload and removes the source only after a verified successful write.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm),
                    CompressFiles = true,
                    ScrambleNames = false,
                    UseSteganography = false,
                    RandomizeMetadata = false,
                    RemoveOriginalsAfterSuccess = true,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                },
                new EncryptionProfile
                {
                    Name = "Shred After Lock",
                    Description = $"Most aggressive cleanup. Uses {EncryptionAlgorithmCatalog.Aes256GcmSiv}, verifies output, then securely deletes originals after success.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256GcmSiv,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256GcmSiv),
                    CompressFiles = true,
                    ScrambleNames = true,
                    UseSteganography = false,
                    RandomizeMetadata = true,
                    RemoveOriginalsAfterSuccess = true,
                    SecureDeleteOriginals = true,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                },
                new EncryptionProfile
                {
                    Name = "Stealth PNG",
                    Description = $"Wraps an {EncryptionAlgorithmCatalog.Aes256Gcm} payload in a PNG container for less conspicuous file handling.",
                    Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
                    KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm),
                    CompressFiles = true,
                    ScrambleNames = false,
                    UseSteganography = true,
                    RandomizeMetadata = true,
                    RemoveOriginalsAfterSuccess = false,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    IsBuiltIn = true
                }
            ];
        }

        private IEnumerable<EncryptionProfile> GetAvailableProfilesForCurrentExperience()
        {
            return GetBuiltInProfiles()
                .Concat(_customProfiles)
                .Where(ProfileAllowedForCurrentExperience);
        }

        private bool ProfileAllowedForCurrentExperience(EncryptionProfile profile)
        {
            if (!EncryptionAlgorithmCatalog.TryGetDefinition(profile.Algorithm, out EncryptionAlgorithmDefinition? definition) ||
                !PayloadChunkedService.CanEncryptNewPayloadOnThisRuntime(definition) ||
                (profile.UseSteganography && !string.Equals(definition.Id, EncryptionAlgorithmCatalog.Aes256Gcm, StringComparison.Ordinal)))
            {
                return false;
            }

            return _currentExperienceLevel switch
            {
                UserExperienceLevel.Beginner => string.Equals(profile.Name, "Recommended", StringComparison.OrdinalIgnoreCase),
                UserExperienceLevel.Intermediate => !profile.UseSteganography &&
                                                   !profile.ScrambleNames &&
                                                   !profile.RandomizeMetadata &&
                                                   !profile.SecureDeleteOriginals &&
                                                   string.IsNullOrWhiteSpace(profile.KeyfilePath),
                _ => true
            };
        }

        private async Task LoadProfilesAsync()
        {
            _customProfiles.Clear();
            string protectedPath = GetProtectedProfilesPath();
            string path = GetProfilesPath();
            if (File.Exists(protectedPath))
            {
                try
                {
                    byte[] protectedBytes = await ReadStoredJsonBytesAsync(protectedPath);
                    var loaded = DeserializeProtectedJsonForCurrentUser<List<EncryptionProfile>>(protectedBytes);
                    if (loaded != null)
                    {
                        _customProfiles.AddRange(SanitizeCustomProfiles(loaded));
                    }
                }
                catch
                {
                    // Ignore malformed profile file and continue with built-in defaults.
                }
            }
            else if (File.Exists(path))
            {
                try
                {
                    string json = await ReadStoredJsonTextAsync(path);
                    var loaded = JsonSerializer.Deserialize<List<EncryptionProfile>>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _customProfiles.AddRange(SanitizeCustomProfiles(loaded));
                    }
                }
                catch
                {
                    // Ignore malformed profile file and continue with built-in defaults.
                }
            }

            RefreshProfileCombo();
        }

        private void SaveProfiles()
        {
            _ = SaveProfilesSafelyAsync();
        }

        private async Task SaveProfilesAsync()
        {
            List<EncryptionProfile> profiles = SanitizeCustomProfiles(_customProfiles).ToList();
            byte[] protectedBytes = ProtectJsonForCurrentUser(profiles);
            await AppPreferencesStore.WriteAllBytesAtomicallyAsync(GetProtectedProfilesPath(), protectedBytes);
            TryDeleteFile(GetProfilesPath());
        }

        private static IReadOnlyList<EncryptionProfile> SanitizeCustomProfiles(IEnumerable<EncryptionProfile>? profiles)
        {
            var sanitizedProfiles = new List<EncryptionProfile>();

            foreach (EncryptionProfile profile in (profiles ?? Enumerable.Empty<EncryptionProfile>()).OfType<EncryptionProfile>())
            {
                if (profile.IsBuiltIn)
                {
                    continue;
                }

                string name = NormalizeProfileName(profile.Name);
                if (string.IsNullOrWhiteSpace(name) ||
                    IsBuiltInProfileName(name) ||
                    !EncryptionAlgorithmCatalog.TryGetDefinition(profile.Algorithm, out EncryptionAlgorithmDefinition? definition) ||
                    !PayloadChunkedService.CanEncryptNewPayloadOnThisRuntime(definition))
                {
                    continue;
                }

                var sanitizedProfile = new EncryptionProfile
                {
                    Name = name,
                    Description = profile.Description ?? string.Empty,
                    Algorithm = definition.DisplayName,
                    KeySizeBits = definition.KeySizeBits,
                    CompressFiles = profile.CompressFiles,
                    ScrambleNames = profile.ScrambleNames,
                    UseSteganography = profile.UseSteganography && definition.CanUsePngCarrier,
                    RandomizeMetadata = profile.RandomizeMetadata,
                    RemoveOriginalsAfterSuccess = profile.RemoveOriginalsAfterSuccess,
                    SecureDeleteOriginals = profile.RemoveOriginalsAfterSuccess && profile.SecureDeleteOriginals,
                    VerifyAfterWrite = profile.VerifyAfterWrite,
                    BackupFolderPath = profile.BackupFolderPath?.Trim() ?? string.Empty,
                    KeyfilePath = profile.KeyfilePath?.Trim() ?? string.Empty,
                    IsBuiltIn = false
                };

                int existingIndex = sanitizedProfiles.FindIndex(existing =>
                    string.Equals(existing.Name, sanitizedProfile.Name, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    sanitizedProfiles[existingIndex] = sanitizedProfile;
                }
                else
                {
                    sanitizedProfiles.Add(sanitizedProfile);
                }
            }

            return sanitizedProfiles;
        }

        private async Task SaveProfilesSafelyAsync()
        {
            try
            {
                await SaveProfilesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Unable to save profiles: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
            }
        }

        private void RefreshProfileCombo()
        {
            _isApplyingProfile = true;
            try
            {
                string? currentSelection = ProfileCombo.SelectedItem as string;
                ProfileCombo.Items.Clear();

                foreach (var profile in GetAvailableProfilesForCurrentExperience())
                {
                    ProfileCombo.Items.Add(profile.Name);
                }

                string targetSelection = string.IsNullOrWhiteSpace(currentSelection) ? "Recommended" : currentSelection;
                ProfileCombo.SelectedItem = ProfileCombo.Items.OfType<string>().FirstOrDefault(item =>
                    string.Equals(item, targetSelection, StringComparison.OrdinalIgnoreCase))
                    ?? "Recommended";
            }
            finally
            {
                _isApplyingProfile = false;
            }

            UpdateProfilePresentation(FindProfile(ProfileCombo.SelectedItem as string));
        }

        private EncryptionProfile? FindProfile(string? name)
        {
            return GetAvailableProfilesForCurrentExperience()
                .FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildCustomProfileDescription(EncryptionProfile profile)
        {
            string algorithm = EncryptionAlgorithmCatalog.TryNormalize(profile.Algorithm, out string normalizedAlgorithm)
                ? normalizedAlgorithm
                : profile.Algorithm;
            int keySizeBits = EncryptionAlgorithmCatalog.CanEncryptNewPayload(algorithm)
                ? EncryptionAlgorithmCatalog.GetKeySizeBits(algorithm)
                : profile.KeySizeBits;
            var parts = new List<string>
            {
                OperationHistoryAlgorithm.Format(algorithm, keySizeBits)
            };

            if (profile.CompressFiles) parts.Add("compression");
            if (profile.ScrambleNames) parts.Add("scrambled names");
            if (profile.UseSteganography) parts.Add("PNG container");
            if (profile.RandomizeMetadata) parts.Add("randomized metadata");
            if (profile.RemoveOriginalsAfterSuccess)
            {
                parts.Add(profile.SecureDeleteOriginals ? "secure source removal" : "source removal");
            }
            else
            {
                parts.Add("keeps originals");
            }

            return string.Join(" • ", parts);
        }

        private void UpdateProfilePresentation(EncryptionProfile? profile)
        {
            string profileName = profile?.Name ?? "Recommended";
            ProfileDescriptionText.Text = profile == null
                ? "Balanced default for most files."
                : string.IsNullOrWhiteSpace(profile.Description)
                    ? BuildCustomProfileDescription(profile)
                    : profile.Description;

            SaveProfileButton.Content = profile != null && !profile.IsBuiltIn
                ? "Update Profile"
                : "Save As Profile";
            UpdateAboutMenuInfo();
        }

        private async Task LoadHistoryAsync()
        {
            _operationHistory.Clear();
            _jobHistoryItems.Clear();

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Off)
            {
                RefreshHistoryItems();
                return;
            }

            string protectedPath = GetProtectedHistoryPath();
            string redactedPath = GetHistoryPath();
            bool loadedHistory = false;

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Full && File.Exists(protectedPath))
            {
                try
                {
                    byte[] protectedBytes = await ReadStoredJsonBytesAsync(protectedPath);
                    var loaded = DeserializeProtectedJsonForCurrentUser<List<OperationHistoryEntry>>(protectedBytes);
                    if (loaded != null)
                    {
                        _operationHistory.AddRange(OperationHistorySanitizer.CloneEntries(
                            loaded
                                .OfType<OperationHistoryEntry>()
                                .OrderByDescending(entry => entry.TimestampUtc)
                                .Take(MaxHistoryEntries),
                            includeFullPaths: true));
                        loadedHistory = true;
                    }
                }
                catch
                {
                    // Ignore malformed history and continue with an empty view.
                }
            }

            if (!loadedHistory && File.Exists(redactedPath))
            {
                try
                {
                    string json = await ReadStoredJsonTextAsync(redactedPath);
                    var loaded = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _operationHistory.AddRange(OperationHistorySanitizer.CloneEntries(
                            loaded
                                .OfType<OperationHistoryEntry>()
                                .OrderByDescending(entry => entry.TimestampUtc)
                                .Take(MaxHistoryEntries),
                            includeFullPaths: true));
                    }
                }
                catch
                {
                    // Ignore malformed history and continue with an empty view.
                }
            }

            RefreshHistoryItems();
        }

        private void SaveHistory()
        {
            _ = SaveHistorySafelyAsync();
        }

        private async Task SaveHistoryAsync()
        {
            string redactedPath = GetHistoryPath();
            string protectedPath = GetProtectedHistoryPath();

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Off)
            {
                TryDeleteFile(redactedPath);
                TryDeleteFile(protectedPath);
                return;
            }

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Full)
            {
                byte[] protectedBytes = ProtectJsonForCurrentUser(
                    CloneHistoryEntries(_operationHistory.Take(MaxHistoryEntries).ToList(), includeFullPaths: true));
                await AppPreferencesStore.WriteAllBytesAtomicallyAsync(protectedPath, protectedBytes);
                TryDeleteFile(redactedPath);
                return;
            }

            string redactedJson = JsonSerializer.Serialize(
                CloneHistoryEntries(_operationHistory.Take(MaxHistoryEntries).ToList(), includeFullPaths: false),
                JsonOptions);
            await AppPreferencesStore.WriteAllTextAtomicallyAsync(redactedPath, redactedJson, Encoding.UTF8);
            TryDeleteFile(protectedPath);
        }

        private async Task SaveHistorySafelyAsync()
        {
            try
            {
                await SaveHistoryAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Unable to save operation history: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
            }
        }

        private static List<OperationHistoryEntry> CloneHistoryEntries(List<OperationHistoryEntry>? entries, bool includeFullPaths)
        {
            return OperationHistorySanitizer.CloneEntries(entries, includeFullPaths);
        }

        private static void TryDeleteFile(string path)
        {
            FileCleanupService.TryDeleteFile(path, out _);
        }

        private void RefreshHistoryItems()
        {
            _jobHistoryItems.Clear();
            foreach (var entry in _operationHistory.Where(ShouldShowHistoryEntry).Take(MaxHistoryEntries))
            {
                string metrics = BuildHistoryMetricsSummary(entry);
                string algorithm = OperationHistoryAlgorithm.Format(entry.Algorithm, entry.KeySizeBits);
                _jobHistoryItems.Add(new JobHistoryItem
                {
                    Id = entry.Id,
                    Title = $"{entry.Operation} • {entry.TimestampUtc.ToLocalTime():g}",
                    Subtitle = $"{algorithm} • Profile: {entry.ProfileName}{metrics}",
                    ResultSummary = entry.Cancelled
                        ? $"Cancelled after {entry.SuccessCount} success(es)"
                        : $"{entry.SuccessCount} success • {entry.FailureCount} failed"
                });
            }

            if (_jobHistoryItems.Count > 0)
            {
                RecentJobsListView.SelectedIndex = 0;
                RecentJobsEmptyText.Visibility = Visibility.Collapsed;
                RecentJobsListView.Visibility = Visibility.Visible;
            }
            else
            {
                RecentJobsListView.SelectedIndex = -1;
                RecentJobsEmptyText.Visibility = Visibility.Visible;
                RecentJobsListView.Visibility = Visibility.Collapsed;
            }

            if (_isUiReady)
            {
                RefreshDashboardData();
                RefreshHashFilesRecentHashes();
            }
        }

        private bool ShouldShowHistoryEntry(OperationHistoryEntry entry)
        {
            if (!string.Equals(_historyOperationFilter, "All operations", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entry.Operation, _historyOperationFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return _historyStatusFilter switch
            {
                "Successful" => entry.SuccessCount > 0 && entry.FailureCount == 0 && !entry.Cancelled,
                "Failed" => entry.FailureCount > 0,
                "Cancelled" => entry.Cancelled,
                _ => true
            };
        }

        private static string BuildHistoryMetricsSummary(OperationHistoryEntry entry)
        {
            var parts = new List<string>();
            if (OperationHistorySanitizer.NormalizeNonNegativeMetric(entry.TotalOutputSizeBytes) is long outputSizeBytes)
            {
                parts.Add($"output {FormatDashboardFileSize(outputSizeBytes)}");
            }

            if (OperationHistorySanitizer.NormalizeNonNegativeMetric(entry.TotalStorageSavedBytes) is long savedBytes && savedBytes > 0)
            {
                parts.Add($"saved {FormatDashboardFileSize(savedBytes)}");
            }
            else if (OperationHistorySanitizer.NormalizeNonNegativeMetric(entry.TotalStorageAddedBytes) is long addedBytes && addedBytes > 0)
            {
                parts.Add($"compression increased {FormatDashboardFileSize(addedBytes)}");
            }

            if (entry.CompressionRequestedCount > 0)
            {
                parts.Add($"{entry.CompressionAppliedCount}/{entry.CompressionRequestedCount} compressed");
            }

            return parts.Count == 0 ? string.Empty : $" • {string.Join(" • ", parts)}";
        }

        private void UpdateThemeToggleVisual()
        {
            ThemeToggleLabel.Text = isDarkTheme ? "Dark" : "Light";
            ThemeToggleButton.IsOn = isDarkTheme;
            ToolTipService.SetToolTip(
                ThemeToggleButton,
                isDarkTheme ? "Switch to light mode" : "Switch to dark mode");
        }

        private void ApplyPreferencesToControls()
        {
            _isApplyingExperienceMode = true;
            try
            {
                ExperienceModeCombo.SelectedIndex = _preferences.ExperienceLevel switch
                {
                    UserExperienceLevel.Intermediate => 1,
                    UserExperienceLevel.Advanced => 2,
                    _ => 0
                };
            }
            finally
            {
                _isApplyingExperienceMode = false;
            }

            HistoryPrivacyCombo.SelectedIndex = _preferences.HistoryPrivacyMode switch
            {
                HistoryPrivacyMode.Off => 0,
                HistoryPrivacyMode.Full => 2,
                _ => 1
            };

            FullPathExportsToggle.IsOn = _preferences.IncludeFullPathsInExports;

            string timestampPolicy = AppPreferencesStore.NormalizeOutputTimestampPolicy(_preferences.OutputTimestampPolicy);

            foreach (ComboBoxItem item in OutputTimestampPolicyCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content as string, timestampPolicy, StringComparison.OrdinalIgnoreCase))
                {
                    OutputTimestampPolicyCombo.SelectedItem = item;
                    break;
                }
            }

            EncryptOutputFolderBox.Text = _preferences.CustomEncryptOutputDirectory ?? string.Empty;
            OutputCustomLocationRadio.IsChecked = _preferences.UseCustomEncryptOutputDirectory;
            OutputSameLocationRadio.IsChecked = !_preferences.UseCustomEncryptOutputDirectory;
            UpdateEncryptDestinationUi(persistPreferences: false);
        }

        private void PersistPreferences()
        {
            _ = PersistPreferencesSafelyAsync();
        }

        private async Task PersistPreferencesAsync()
        {
            await AppPreferencesStore.SaveAsync(GetAppDataDirectory(), _preferences);
        }

        private async Task PersistPreferencesSafelyAsync()
        {
            try
            {
                await PersistPreferencesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Unable to save preferences: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
            }
        }

        private static string GetExperienceLevelDisplay(UserExperienceLevel level)
        {
            return level switch
            {
                UserExperienceLevel.Intermediate => "Intermediate",
                UserExperienceLevel.Advanced => "Advanced",
                _ => "Beginner"
            };
        }

        private static UserExperienceLevel ParseExperienceLevel(string? text)
        {
            return text?.Trim().ToLowerInvariant() switch
            {
                "intermediate" => UserExperienceLevel.Intermediate,
                "advanced" => UserExperienceLevel.Advanced,
                _ => UserExperienceLevel.Beginner
            };
        }

        private void UpdateFirstLaunchExperience()
        {
            FirstLaunchOverlay.Visibility = _preferences.HasSelectedExperienceLevel
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ApplyExperienceMode(UserExperienceLevel level, bool persist, bool showAdvancedWarning)
        {
            _isApplyingExperienceMode = true;
            try
            {
                _currentExperienceLevel = level;
                _preferences.ExperienceLevel = level;
                if (persist)
                {
                    _preferences.HasSelectedExperienceLevel = true;
                    PersistPreferences();
                }

                string display = GetExperienceLevelDisplay(level);
                CurrentExperienceLevelText.Text = display;
                ExperienceModeCombo.SelectedIndex = level switch
                {
                    UserExperienceLevel.Intermediate => 1,
                    UserExperienceLevel.Advanced => 2,
                    _ => 0
                };

                InspectorHeaderText.Text = level switch
                {
                    UserExperienceLevel.Beginner => "Beginner mode keeps the workspace simple and safe by default.",
                    UserExperienceLevel.Intermediate => "Intermediate mode adds useful controls without exposing low-level security knobs.",
                    _ => "Advanced mode exposes payload, key, and format controls for power users."
                };

                AdvancedModeInfoBar.IsOpen = showAdvancedWarning && level == UserExperienceLevel.Advanced;

                EnforceExperienceModeConstraints();
                RefreshProfileCombo();
                ApplyExperienceModeVisibility();
                UpdateDropHint();
                UpdateRunSummaryBanner();
                UpdateSafetyBanner();
                UpdatePayloadInspectorPreview();
                RefreshPreflightPreview();
                UpdateStatusLabel();
                UpdateFirstLaunchExperience();
                UpdateAboutMenuInfo();
                UpdateHeaderContext();
            }
            finally
            {
                _isApplyingExperienceMode = false;
            }
        }

        private void EnforceExperienceModeConstraints()
        {
            SetComboSelection(OperationModeCombo, "Encrypt / Decrypt");
            SetComboSelection(AlgorithmCombo, EncryptionAlgorithmCatalog.Aes256Gcm);
            SetComboSelection(
                KeySizeCombo,
                EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.Aes256Gcm).ToString(CultureInfo.InvariantCulture));

            if (_currentExperienceLevel == UserExperienceLevel.Beginner)
            {
                RemoveOriginalsToggle.IsOn = false;
                SecureDeleteOriginalsToggle.IsOn = false;
                VerifyAfterWriteToggle.IsOn = true;
                CompressModeToggle.IsOn = true;
                ScrambleNamesToggle.IsOn = false;
                SteganographyToggle.IsOn = false;
                PackageFoldersToggle.IsOn = false;
                MetadataRandomizeToggle.IsOn = false;
                KeyfilePathBox.Text = string.Empty;
                RecoveryKeyBox.Text = string.Empty;
                BackupFolderBox.Text = string.Empty;
                ProfileCombo.SelectedItem = "Recommended";
            }
            else if (_currentExperienceLevel == UserExperienceLevel.Intermediate)
            {
                SecureDeleteOriginalsToggle.IsOn = false;
                ScrambleNamesToggle.IsOn = false;
                SteganographyToggle.IsOn = false;
                PackageFoldersToggle.IsOn = false;
                MetadataRandomizeToggle.IsOn = false;
                KeyfilePathBox.Text = string.Empty;
                RecoveryKeyBox.Text = string.Empty;
            }

            if (RemoveOriginalsToggle.IsOn && !VerifyAfterWriteToggle.IsOn)
            {
                VerifyAfterWriteToggle.IsOn = true;
            }
        }

        private void ApplyExperienceModeVisibility()
        {
            bool isBeginner = _currentExperienceLevel == UserExperienceLevel.Beginner;
            bool isIntermediate = _currentExperienceLevel == UserExperienceLevel.Intermediate;
            bool isAdvanced = _currentExperienceLevel == UserExperienceLevel.Advanced;

            CredentialsSection.Visibility = Visibility.Visible;
            CredentialsSection.IsExpanded = true;
            ProfileSection.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            SaveProfileButton.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            KeyfilePanel.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            SecurityAdvancedPanel.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            OutputSafetySection.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            AdvancedHandlingSection.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;

            CompressOption.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            RemoveOriginalsOption.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            VerifyAfterWriteOption.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            SecureDeleteOption.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            BackupFolderOption.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            OutputTimestampPolicyCombo.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            ScrambleNamesOption.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            SteganographyOption.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            PackageFoldersOption.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            MetadataExpander.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;

            InspectButton.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            RotateKeysButton.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            AdvancedModeHelpButton.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            InspectorTabView.Visibility = Visibility.Visible;
            SetupTabItem.Visibility = Visibility.Visible;
            ChecksTabItem.Visibility = isBeginner ? Visibility.Collapsed : Visibility.Visible;
            JobsTabItem.Visibility = Visibility.Visible;

            if (ProfileSection.Visibility == Visibility.Visible)
            {
                ProfileSection.IsExpanded = !isAdvanced;
            }

            if (OutputSafetySection.Visibility == Visibility.Visible)
            {
                OutputSafetySection.IsExpanded = !isIntermediate && isAdvanced;
            }

            if (AdvancedHandlingSection.Visibility == Visibility.Visible)
            {
                AdvancedHandlingSection.IsExpanded = false;
            }

            if (isBeginner)
            {
                ApplyInspectorView("Setup");
                InspectorChecksPanel.Visibility = Visibility.Collapsed;
                InspectorJobsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateDropHint()
        {
            if (FileList.Count == 0)
            {
                DropHintText.Text = "Queue is empty. Browse files or folders to begin.";
                return;
            }

            string destinationSummary = BuildRunSummaryOutputLocation() switch
            {
                "custom folder" => "Output will be written to your custom folder.",
                "custom folder not set" => "Set a custom output folder before you run Encrypt.",
                _ => "Output will be written next to the source files."
            };

            DropHintText.Text = $"{FileList.Count} item(s) queued. {destinationSummary}";
        }

        private void WorkspaceSplitGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveWorkspaceLayout(e.NewSize.Width);
        }

        private void ApplyResponsiveWorkspaceLayout(double width)
        {
            bool compact = width < 1180;

            Grid.SetRow(QueuePane, 0);
            Grid.SetColumn(QueuePane, 0);
            Grid.SetColumnSpan(QueuePane, compact ? 3 : 1);

            Grid.SetRow(WorkspaceDivider, compact ? 1 : 0);
            Grid.SetColumn(WorkspaceDivider, compact ? 0 : 1);
            Grid.SetColumnSpan(WorkspaceDivider, compact ? 3 : 1);
            WorkspaceDivider.Width = compact ? double.NaN : 1;
            WorkspaceDivider.Height = compact ? 1 : double.NaN;
            WorkspaceDivider.Margin = compact ? new Thickness(28, 0, 28, 0) : new Thickness(0);

            Grid.SetRow(InspectorPane, compact ? 2 : 0);
            Grid.SetColumn(InspectorPane, compact ? 0 : 2);
            Grid.SetColumnSpan(InspectorPane, compact ? 3 : 1);
            InspectorPane.CornerRadius = compact ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0);

            ApplyQueueActionLayout(QueuePane.ActualWidth > 0 ? QueuePane.ActualWidth : width);
            ApplyQueueMetricLayout(QueuePane.ActualWidth > 0 ? QueuePane.ActualWidth : width);
        }

        private void QueuePane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyQueueActionLayout(e.NewSize.Width);
            ApplyQueueMetricLayout(e.NewSize.Width);
        }

        private void DropPanelContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyHeroLayout(e.NewSize.Width);
        }

        private void ApplyHeroLayout(double width)
        {
            if (QuickStartPanel.Visibility != Visibility.Visible &&
                DropFeatureGrid.Visibility != Visibility.Visible)
            {
                return;
            }

            bool stackQuickStart = width < 980;
            Grid.SetRow(QuickStartPanel, stackQuickStart ? 1 : 0);
            Grid.SetColumn(QuickStartPanel, stackQuickStart ? 0 : 1);
            Grid.SetColumnSpan(QuickStartPanel, stackQuickStart ? 2 : 1);
            QuickStartPanel.Margin = stackQuickStart ? new Thickness(0, 8, 0, 0) : new Thickness(0);

            bool stackFeaturePanels = width < 760;
            if (stackFeaturePanels)
            {
                Grid.SetColumn(DropVisibilityPanel, 0);
                Grid.SetColumnSpan(DropVisibilityPanel, 3);
                Grid.SetRow(DropVisibilityPanel, 0);

                Grid.SetColumn(DropReviewPanel, 0);
                Grid.SetColumnSpan(DropReviewPanel, 3);
                Grid.SetRow(DropReviewPanel, 1);

                Grid.SetColumn(DropBatchPanel, 0);
                Grid.SetColumnSpan(DropBatchPanel, 3);
                Grid.SetRow(DropBatchPanel, 2);
            }
            else
            {
                Grid.SetColumn(DropVisibilityPanel, 0);
                Grid.SetColumnSpan(DropVisibilityPanel, 1);
                Grid.SetRow(DropVisibilityPanel, 0);

                Grid.SetColumn(DropReviewPanel, 1);
                Grid.SetColumnSpan(DropReviewPanel, 1);
                Grid.SetRow(DropReviewPanel, 0);

                Grid.SetColumn(DropBatchPanel, 2);
                Grid.SetColumnSpan(DropBatchPanel, 1);
                Grid.SetRow(DropBatchPanel, 0);
            }
        }

        private void ApplyQueueMetricLayout(double width)
        {
            if (width < 620)
            {
                Grid.SetColumn(QueueFileMetricPanel, 0);
                Grid.SetColumnSpan(QueueFileMetricPanel, 3);
                Grid.SetRow(QueueFileMetricPanel, 0);

                Grid.SetColumn(QueueRootMetricPanel, 0);
                Grid.SetColumnSpan(QueueRootMetricPanel, 3);
                Grid.SetRow(QueueRootMetricPanel, 1);

                Grid.SetColumn(QueueSizeMetricPanel, 0);
                Grid.SetColumnSpan(QueueSizeMetricPanel, 3);
                Grid.SetRow(QueueSizeMetricPanel, 2);
            }
            else if (width < 860)
            {
                Grid.SetColumn(QueueFileMetricPanel, 0);
                Grid.SetColumnSpan(QueueFileMetricPanel, 1);
                Grid.SetRow(QueueFileMetricPanel, 0);

                Grid.SetColumn(QueueRootMetricPanel, 1);
                Grid.SetColumnSpan(QueueRootMetricPanel, 2);
                Grid.SetRow(QueueRootMetricPanel, 0);

                Grid.SetColumn(QueueSizeMetricPanel, 0);
                Grid.SetColumnSpan(QueueSizeMetricPanel, 3);
                Grid.SetRow(QueueSizeMetricPanel, 1);
            }
            else
            {
                Grid.SetColumn(QueueFileMetricPanel, 0);
                Grid.SetColumnSpan(QueueFileMetricPanel, 1);
                Grid.SetRow(QueueFileMetricPanel, 0);

                Grid.SetColumn(QueueRootMetricPanel, 1);
                Grid.SetColumnSpan(QueueRootMetricPanel, 1);
                Grid.SetRow(QueueRootMetricPanel, 0);

                Grid.SetColumn(QueueSizeMetricPanel, 2);
                Grid.SetColumnSpan(QueueSizeMetricPanel, 1);
                Grid.SetRow(QueueSizeMetricPanel, 0);
            }
        }

        private void ApplyQueueActionLayout(double width)
        {
            if (width < 620)
            {
                ConfigureQueueActionButton(EncryptButton, 0, 0, 5);
                ConfigureQueueActionButton(DecryptButton, 1, 0, 5);
                ConfigureQueueActionButton(InspectButton, 2, 0, 5);
                ConfigureQueueActionButton(RotateKeysButton, 3, 0, 5);
                ConfigureQueueActionButton(CancelRunButton, 4, 0, 5);
                CancelRunButton.Width = double.NaN;
            }
            else if (width < 930)
            {
                ConfigureQueueActionButton(EncryptButton, 0, 0, 2);
                ConfigureQueueActionButton(DecryptButton, 0, 2, 2);
                ConfigureQueueActionButton(CancelRunButton, 0, 4, 1);
                ConfigureQueueActionButton(InspectButton, 1, 0, 2);
                ConfigureQueueActionButton(RotateKeysButton, 1, 2, 3);
                CancelRunButton.Width = 110;
            }
            else
            {
                ConfigureQueueActionButton(EncryptButton, 0, 0, 1);
                ConfigureQueueActionButton(DecryptButton, 0, 1, 1);
                ConfigureQueueActionButton(InspectButton, 0, 2, 1);
                ConfigureQueueActionButton(RotateKeysButton, 0, 3, 1);
                ConfigureQueueActionButton(CancelRunButton, 0, 4, 1);
                CancelRunButton.Width = 110;
            }
        }

        private static void ConfigureQueueActionButton(Button button, int row, int column, int columnSpan)
        {
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            Grid.SetColumnSpan(button, columnSpan);
        }

        private void UpdateSafetyBanner()
        {
            var warnings = new List<string>();

            if (RemoveOriginalsToggle.IsOn && string.IsNullOrWhiteSpace(BackupFolderBox.Text))
            {
                warnings.Add("This configuration may cause permanent data loss. We recommend enabling a backup before deleting originals.");
            }

            if (!string.IsNullOrWhiteSpace(PasswordBox.Password) &&
                CalculatePasswordStrength(PasswordBox.Password).Score < StrongPasswordMinimumScore)
            {
                warnings.Add("Use a stronger password with length, upper/lowercase letters, numbers, and symbols before encrypting.");
            }

            if (_currentExperienceLevel == UserExperienceLevel.Advanced)
            {
                warnings.Add("Advanced mode exposes settings that can affect security and data recovery.");
            }

            if (warnings.Count == 0)
            {
                SafetyInfoBar.IsOpen = false;
                SafetyInfoBar.Message = string.Empty;
                return;
            }

            SafetyInfoBar.Severity = warnings.Count > 1 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            SafetyInfoBar.Message = string.Join(" ", warnings);
            SafetyInfoBar.IsOpen = true;
        }

        private void UpdatePayloadInspectorPreview()
        {
            if (_currentExperienceLevel != UserExperienceLevel.Advanced)
            {
                PayloadInspectorSection.Visibility = Visibility.Collapsed;
                return;
            }

            QueuedFileItem? candidate = FileList.FirstOrDefault(item => File.Exists(item.SourcePath));
            if (candidate == null)
            {
                PayloadInspectorSection.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                string candidatePath = RequireExistingFile(candidate.SourcePath);
                if (IsPayloadV3File(candidatePath))
                {
                    using FileStream stream = new(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    PayloadHeader header = PayloadChunkedService.InspectHeader(stream);
                    PayloadInspectorSection.Visibility = Visibility.Visible;
                    PayloadInspectorText.Text =
                        $"File: {Path.GetFileName(candidatePath)}\n" +
                        $"Format version: {header.Version}\n" +
                        $"Algorithm: {EncryptionAlgorithmCatalog.GetDisplayName(header.AlgorithmId)}\n" +
                        $"KDF: Argon2id ({header.ArgonIterations} iterations, {header.ArgonMemoryKb / 1024.0:0.#} MB, parallelism {header.ArgonParallelism})\n" +
                        $"Chunk size: {FormatFileSize(header.ChunkSize)}\n" +
                        $"Key slots: {header.Slots.Count}\n" +
                        $"Flags: 0x{header.Flags:X2}";
                    return;
                }

                bool hasStegoPayload = ContainsStegoPayload(candidatePath);
                if (candidatePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase) || hasStegoPayload)
                {
                    PayloadInspectorSection.Visibility = Visibility.Visible;
                        PayloadInspectorText.Text =
                        $"File: {Path.GetFileName(candidatePath)}\n" +
                        $"Format version: older v2\n" +
                        $"Algorithm: {EncryptionAlgorithmCatalog.Aes256Gcm}\n" +
                        $"KDF: Argon2id with older payload support\n" +
                        $"Flags: {(hasStegoPayload ? "PNG carrier" : "standard locked payload")}\n" +
                        "Transparency note: older payloads expose fewer header details than v3.";
                    return;
                }
            }
            catch (Exception ex)
            {
                PayloadInspectorSection.Visibility = Visibility.Visible;
                PayloadInspectorText.Text = $"Inspector could not parse the queued payload yet: {GetFriendlyExceptionMessage(ex, "Payload inspection failed.")}";
                return;
            }

            PayloadInspectorSection.Visibility = Visibility.Collapsed;
        }

        private void UpdateRunSummaryBanner()
        {
            string algorithm = EncryptionAlgorithmCatalog.TryNormalize(GetComboContent(AlgorithmCombo), out string normalizedAlgorithm)
                ? normalizedAlgorithm
                : EncryptionAlgorithmCatalog.Aes256Gcm;
            int keySize = EncryptionAlgorithmCatalog.GetKeySizeBits(algorithm);
            bool hasKeyfile = !string.IsNullOrWhiteSpace(KeyfilePathBox.Text);
            bool hasRecoveryKey = !string.IsNullOrWhiteSpace(RecoveryKeyBox.Text);
            string outputTimestampPolicy = AppPreferencesStore.NormalizeOutputTimestampPolicy(
                (OutputTimestampPolicyCombo.SelectedItem as ComboBoxItem)?.Content as string);
            string outputLocationSummary = BuildRunSummaryOutputLocation();

            var parts = new List<string>
            {
                OperationHistoryAlgorithm.Format(algorithm, keySize),
                $"output: {outputLocationSummary}",
                VerifyAfterWriteToggle.IsOn ? "verify writes on" : "verify writes off",
                RemoveOriginalsToggle.IsOn ? "remove originals after success" : "keep originals"
            };

            if (hasKeyfile)
            {
                parts.Add("keyfile");
            }

            if (hasRecoveryKey)
            {
                parts.Add("recovery key");
            }

            if (CompressModeToggle.IsOn)
            {
                parts.Add("compression");
            }

            if (IsScrambleNamesEnabled)
            {
                parts.Add("scrambled names");
            }

            if (IsSteganographyEnabled)
            {
                parts.Add("PNG carrier");
            }

            if (!string.IsNullOrWhiteSpace(BackupFolderBox.Text))
            {
                parts.Add("backup folder");
            }

            if (!string.Equals(outputTimestampPolicy, AppPreferencesStore.CurrentTimeTimestampPolicy, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"timestamps: {outputTimestampPolicy.ToLowerInvariant()}");
            }

            string riskText = string.Empty;
            if (RemoveOriginalsToggle.IsOn && string.IsNullOrWhiteSpace(BackupFolderBox.Text))
            {
                riskText = "Risk: originals will be deleted and no backup folder is configured.";
            }
            else if (RemoveOriginalsToggle.IsOn)
            {
                riskText = "Risk: originals will be deleted after the run succeeds.";
            }

            RunSummaryInfoBar.Severity = string.IsNullOrWhiteSpace(riskText)
                ? hasKeyfile || hasRecoveryKey ? InfoBarSeverity.Warning : InfoBarSeverity.Informational
                : InfoBarSeverity.Error;
            RunSummaryText.Text = string.Join(" • ", parts);
            RunSummaryRiskText.Text = riskText;
            RunSummaryRiskText.Visibility = string.IsNullOrWhiteSpace(riskText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private string BuildRunSummaryOutputLocation()
        {
            if (OutputCustomLocationRadio.IsChecked == true)
            {
                return string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text)
                    ? "custom folder not set"
                    : FileList.Any(item => item.SourceRootIsFolder)
                        ? "custom folder with preserved folder layout"
                        : "custom folder";
            }

            return FileList.Any(item => item.SourceRootIsFolder)
                ? "inside source folder tree"
                : "same folder as source";
        }

        private void UpdateHeaderContext()
        {
            if (HeaderContextText == null)
            {
                return;
            }

            string queueSummary = FileList.Count == 0
                ? "queue empty"
                : $"{FileList.Count} item(s) queued";

            HeaderContextText.Text = $"{GetExperienceLevelDisplay(_currentExperienceLevel)} mode • {queueSummary}";
        }

        private void UpdateEncryptDestinationUi(bool persistPreferences = true)
        {
            bool useCustom = OutputCustomLocationRadio.IsChecked == true;
            bool hasFolderSelections = FileList.Any(item => item.SourceRootIsFolder);
            EncryptCustomDestinationPanel.Visibility = useCustom ? Visibility.Visible : Visibility.Collapsed;
            EncryptDestinationSummaryText.Text = useCustom
                ? string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text)
                    ? "Choose a folder for new locked files."
                    : hasFolderSelections
                        ? $"New locked files will be written under {EncryptOutputFolderBox.Text.Trim()} and keep the source folder layout."
                        : $"New locked files will be written to {EncryptOutputFolderBox.Text.Trim()}."
                : hasFolderSelections
                    ? "Locked files will be written back into the selected folder tree."
                    : "Locked files will be written next to their source files.";

            if (persistPreferences && _isUiReady)
            {
                _preferences.UseCustomEncryptOutputDirectory = useCustom;
                _preferences.CustomEncryptOutputDirectory = EncryptOutputFolderBox.Text?.Trim() ?? string.Empty;
                PersistPreferences();
            }

            if (_isUiReady)
            {
                UpdateRunSummaryBanner();
                RefreshPreflightPreview();
                UpdateDropHint();
                if (!_isSyncingEncryptFilesUi)
                {
                    SynchronizeEncryptOptionsFromWorkflow();
                }

                RefreshEncryptFilesState();
            }
        }

        private void ApplyInspectorView(string view)
        {
            bool showSetup = string.Equals(view, "Setup", StringComparison.OrdinalIgnoreCase);
            bool showChecks = string.Equals(view, "Checks", StringComparison.OrdinalIgnoreCase);
            bool showJobs = string.Equals(view, "Jobs", StringComparison.OrdinalIgnoreCase);

            InspectorSetupPanel.Visibility = showSetup ? Visibility.Visible : Visibility.Collapsed;
            InspectorChecksPanel.Visibility = showChecks ? Visibility.Visible : Visibility.Collapsed;
            InspectorJobsPanel.Visibility = showJobs ? Visibility.Visible : Visibility.Collapsed;

            int index = showSetup ? 0 : showChecks ? 1 : 2;
            if (InspectorTabView.SelectedIndex != index)
            {
                InspectorTabView.SelectedIndex = index;
            }
        }

        private static string GetReportsDirectory()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "FileLocker Reports");
            Directory.CreateDirectory(path);
            return path;
        }

        private void UpdateAboutMenuInfo()
        {
            string version = UpdateService.GetCurrentVersionLabel();

            AboutVersionMenuItem.Text = $"Version {version}";
            AboutRecentOperationMenuItem.Text = GetRecentOperationMenuText();
            AboutUpdateStatusMenuItem.Text = _aboutUpdateStatusText;

            if (AboutViewVersionText != null)
            {
                AboutViewVersionText.Text = $"Version {version}";
            }

            if (AboutViewUpdateText != null)
            {
                AboutViewUpdateText.Text = _aboutUpdateStatusText;
            }
        }

        private string GetRecentOperationMenuText()
        {
            OperationHistoryEntry? latest = _operationHistory
                .OrderByDescending(entry => entry.TimestampUtc)
                .FirstOrDefault();
            if (latest == null)
            {
                return "Recent operation: none yet";
            }

            return $"Recent operation: {latest.Operation} ({latest.SuccessCount} ok, {latest.FailureCount} failed)";
        }

        private void ThemeToggleButton_Toggled(object sender, RoutedEventArgs e)
        {
            if (ThemeToggleButton.IsOn == isDarkTheme)
            {
                return;
            }

            ApplyThemePreference(
                ThemeToggleButton.IsOn ? ThemePreference.Dark : ThemePreference.Light,
                persist: true,
                syncControls: true);
        }


    }
}


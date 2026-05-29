using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private bool _isLoadingSettings;
        private string _selectedAccentOption = "Blue";

        private void InitializeSettingsView()
        {
            LoadSettingsPreferences();
            UpdateAccentSelectionVisual();
            UpdateSettingsUpdateStatusText();
            AboutSettingsVersionText.Text = $"Version {UpdateService.GetCurrentVersionLabel()}";
        }

        private void LoadSettingsPreferences()
        {
            _isLoadingSettings = true;
            try
            {
                SetThemePreferenceComboSelection(_themePreference);

                UseVisualEffectsToggle.IsOn = true;
                CompactModeToggle.IsOn = false;

                SecurityAlgorithmCombo.SelectedIndex = 0;
                SecurityModeCombo.SelectedIndex = 0;
                RequirePasswordConfirmationToggle.IsOn = true;
                ClearSensitiveFieldsToggle.IsOn = true;
                ShowSecurityWarningsToggle.IsOn = true;

                SaveOutputNextToSourceToggle.IsOn = !_preferences.UseCustomEncryptOutputDirectory;
                SettingsOutputLocationBox.Text = string.IsNullOrWhiteSpace(_preferences.CustomEncryptOutputDirectory)
                    ? GetDefaultSettingsOutputLocation()
                    : _preferences.CustomEncryptOutputDirectory;
                DefaultCompressBeforeEncryptionToggle.IsOn = CompressModeToggle.IsOn;
                DefaultDeleteOriginalsToggle.IsOn = RemoveOriginalsToggle.IsOn;
                PreserveFolderStructureToggle.IsOn = true;

                ClearRecentFilesOnExitToggle.IsOn = false;
                StoreLocalHistoryToggle.IsOn = _preferences.HistoryPrivacyMode != HistoryPrivacyMode.Off;

                AutoCheckUpdatesToggle.IsOn = _updateSettings.AutoCheckEnabled;
                AboutSettingsVersionText.Text = $"Version {UpdateService.GetCurrentVersionLabel()}";
            }
            finally
            {
                _isLoadingSettings = false;
            }

            UpdateSettingsOutputLocationUi();
            UpdateSettingsUpdateStatusText();
        }

        private void ApplyThemePreference(ThemePreference preference, bool persist, bool syncControls)
        {
            _themePreference = preference;
            _preferences.ThemePreference = preference;

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = preference switch
                {
                    ThemePreference.Dark => ElementTheme.Dark,
                    ThemePreference.Light => ElementTheme.Light,
                    _ => ElementTheme.Default
                };

                root.UpdateLayout();
                isDarkTheme = root.ActualTheme != ElementTheme.Light;
            }

            UpdateThemeToggleVisual();
            ApplyWindowTitleBarColors();

            if (syncControls && ThemePreferenceCombo != null)
            {
                bool previous = _isLoadingSettings;
                _isLoadingSettings = true;
                try
                {
                    SetThemePreferenceComboSelection(preference);
                }
                finally
                {
                    _isLoadingSettings = previous;
                }
            }

            if (persist && _isUiReady)
            {
                PersistPreferences();
            }
        }

        private void SetThemePreferenceComboSelection(ThemePreference preference)
        {
            ThemePreferenceCombo.SelectedIndex = preference switch
            {
                ThemePreference.System => 0,
                ThemePreference.Light => 2,
                _ => 1
            };
        }

        private static string GetDefaultSettingsOutputLocation()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "FileLocker");
        }

        private void UpdateSettingsOutputLocationUi()
        {
            bool saveNextToSource = SaveOutputNextToSourceToggle.IsOn;
            SettingsOutputLocationBox.IsEnabled = !saveNextToSource;
            BrowseSettingsOutputLocationButton.IsEnabled = !saveNextToSource;
            FileHandlingHelperText.Text = saveNextToSource
                ? "Output will be written next to the source files. Other file-handling defaults are preview controls for now."
                : "Custom output location is saved. Compression, delete-originals, and folder-structure defaults are preview controls for now.";
        }

        private void UpdateSettingsUpdateStatusText()
        {
            if (UpdateStatusText == null)
            {
                return;
            }

            UpdateStatusText.Text = _aboutUpdateStatusText;
        }

        private void UpdateAccentSelectionVisual()
        {
            UpdateAccentButton(AccentBlueButton, "Blue");
            UpdateAccentButton(AccentCyanButton, "Cyan");
            UpdateAccentButton(AccentMintButton, "Mint");
            UpdateAccentButton(AccentPurpleButton, "Purple");
            UpdateAccentButton(AccentOrangeButton, "Orange");
            UpdateAccentButton(AccentRedButton, "Red");
        }

        private void UpdateAccentButton(Button button, string accentName)
        {
            bool selected = string.Equals(_selectedAccentOption, accentName, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = selected
                ? GetBrushResource("BrightBlueBrush")
                : GetBrushResource("AppBorderBrush");
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            AutomationProperties.SetName(button, $"{accentName} accent color");
            AutomationProperties.SetHelpText(button, selected ? "Selected accent color" : "Preview accent color");
        }

        private void ThemePreferenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady)
            {
                return;
            }

            ThemePreference selectedPreference = ThemePreferenceCombo.SelectedIndex switch
            {
                0 => ThemePreference.System,
                2 => ThemePreference.Light,
                _ => ThemePreference.Dark
            };

            ApplyThemePreference(selectedPreference, persist: true, syncControls: false);
        }

        private void AccentColorButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string accentName)
            {
                return;
            }

            _selectedAccentOption = accentName;
            UpdateAccentSelectionVisual();
            AppearanceAccentHelperText.Text = $"{accentName} is selected as a preview accent. App-wide accent changes are not available yet.";
        }

        private async void VisualOnlyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady)
            {
                return;
            }

            if (sender is ToggleSwitch { Name: "DefaultDeleteOriginalsToggle", IsOn: true } deleteOriginalsToggle)
            {
                bool proceed = await ShowConfirmDialogAsync(
                    "This is a preview default for destructive cleanup. Per-run deletion still requires confirmation before FileLocker starts processing.",
                    "Preview Delete Originals Default?");
                if (!proceed)
                {
                    deleteOriginalsToggle.IsOn = false;
                    return;
                }
            }

            if ((sender as FrameworkElement)?.Name is string name)
            {
                SetStatus($"{name} is a preview setting. It will not change saved preferences yet.");
            }
        }

        private void SaveOutputNextToSourceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady)
            {
                return;
            }

            bool saveNextToSource = SaveOutputNextToSourceToggle.IsOn;
            _preferences.UseCustomEncryptOutputDirectory = !saveNextToSource;
            _preferences.CustomEncryptOutputDirectory = saveNextToSource
                ? string.Empty
                : SettingsOutputLocationBox.Text?.Trim() ?? string.Empty;

            EncryptOutputFolderBox.Text = _preferences.CustomEncryptOutputDirectory;
            OutputSameLocationRadio.IsChecked = saveNextToSource;
            OutputCustomLocationRadio.IsChecked = !saveNextToSource;
            UpdateEncryptDestinationUi(persistPreferences: false);
            UpdateSettingsOutputLocationUi();
            PersistPreferences();
        }

        private void SettingsOutputLocationBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady || SaveOutputNextToSourceToggle.IsOn)
            {
                return;
            }

            _preferences.CustomEncryptOutputDirectory = SettingsOutputLocationBox.Text?.Trim() ?? string.Empty;
            EncryptOutputFolderBox.Text = _preferences.CustomEncryptOutputDirectory;
            UpdateEncryptDestinationUi(persistPreferences: false);
            PersistPreferences();
        }

        private async void BrowseSettingsOutputLocationButton_Click(object sender, RoutedEventArgs e)
        {
            string? selectedPath = await BrowseForFolderPathAsync();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            SaveOutputNextToSourceToggle.IsOn = false;
            SettingsOutputLocationBox.Text = selectedPath;
        }

        private async Task<string?> BrowseForFolderPathAsync()
        {
            try
            {
                FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to browse output folder: {GetFriendlyExceptionMessage(ex, "Folder picker failed.")}");
                return null;
            }
        }

        private void StoreLocalHistoryToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady)
            {
                return;
            }

            _preferences.HistoryPrivacyMode = StoreLocalHistoryToggle.IsOn
                ? HistoryPrivacyMode.Redacted
                : HistoryPrivacyMode.Off;

            PersistPreferences();
            SaveHistory();
            RefreshHistoryItems();
            RefreshDashboardData();
        }

        private async void ClearRecentActivityButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationHistory.Count == 0)
            {
                await ShowInfoDialogAsync("There is no saved recent activity to clear.", "Recent Activity");
                return;
            }

            bool proceed = await ShowConfirmDialogAsync(
                "Clear the locally stored operation history?",
                "Clear Recent Activity");
            if (!proceed)
            {
                return;
            }

            _operationHistory.Clear();
            SaveHistory();
            RefreshHistoryItems();
            RefreshDashboardData();
            SetStatus("Recent activity cleared.");
        }

        private async void ClearTemporaryFilesButton_Click(object sender, RoutedEventArgs e)
        {
            Control? sourceControl = sender as Control;
            bool proceed = await ShowConfirmDialogAsync(
                "Clear temporary updater files stored by FileLocker on this device?",
                "Clear Temporary Files");
            if (!proceed)
            {
                return;
            }

            string updaterDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileLocker",
                "Updater");

            if (sourceControl != null)
            {
                sourceControl.IsEnabled = false;
            }

            FileCleanupSummary cleanup;
            try
            {
                SetStatus("Clearing temporary updater files...");
                cleanup = await Task.Run(() => FileCleanupService.DeleteTemporaryFilesUnderDirectory(updaterDirectory));
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to clear temporary files: {GetFriendlyExceptionMessage(ex, "Cleanup failed.")}");
                return;
            }
            finally
            {
                if (sourceControl != null)
                {
                    sourceControl.IsEnabled = true;
                }
            }

            string message = cleanup.DeletedFiles == 0
                ? "No temporary updater files were found."
                : $"Cleared {cleanup.DeletedFiles} temporary updater file(s).";
            if (cleanup.FailedFiles > 0)
            {
                message += $" {cleanup.FailedFiles} file(s) could not be removed and can be retried later.";
            }

            PrivacyHelperText.Text = $"{message} FileLocker still works entirely locally on your device.";
            await ShowInfoDialogAsync(message, "Temporary Files");
        }

        private async void AutoCheckUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || !_isUiReady)
            {
                return;
            }

            bool previousValue = _updateSettings.AutoCheckEnabled;
            bool requestedValue = AutoCheckUpdatesToggle.IsOn;
            _updateSettings.AutoCheckEnabled = requestedValue;
            try
            {
                UpdateService.SaveSettings(_updateSettings);
                SetAboutUpdateStatusText(requestedValue
                    ? "Updates: automatic checks enabled"
                    : "Updates: automatic checks disabled");
            }
            catch (Exception ex)
            {
                _updateSettings.AutoCheckEnabled = previousValue;
                _isLoadingSettings = true;
                try
                {
                    AutoCheckUpdatesToggle.IsOn = previousValue;
                }
                finally
                {
                    _isLoadingSettings = false;
                }

                SetAboutUpdateStatusText("Updates: setting was not saved");
                await ShowErrorDialogAsync($"Unable to save update settings: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
            }
        }

        private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(isManualCheck: true);
        }

        private async void ViewLicenseButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowInfoDialogAsync(
                "No standalone FileLocker license document is bundled with this build. Review the project repository or installer materials before redistributing FileLocker.",
                "View License");
        }

        private async void OpenSourceLicensesButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowInfoDialogAsync(
                "Third-party license notices are not bundled as a separate document in this build. Review the package manifests and published project materials for current dependency license information.",
                "Open Source Licenses");
        }

        private async void ResetSettingsToDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            bool proceed = await ShowConfirmDialogAsync(
                "Reset FileLocker preferences to their defaults? This will not delete your files.",
                "Reset Settings");
            if (!proceed)
            {
                return;
            }

            await ResetSettingsToDefaultsAsync();
        }

        private async Task ResetSettingsToDefaultsAsync()
        {
            PreferencesSnapshot previousPreferences = CapturePreferencesSnapshot();
            bool previousAutoCheck = _updateSettings.AutoCheckEnabled;

            _preferences.ThemePreference = ThemePreference.Dark;
            _themePreference = ThemePreference.Dark;
            _preferences.HistoryPrivacyMode = HistoryPrivacyMode.Redacted;
            _preferences.IncludeFullPathsInExports = false;
            _preferences.OutputTimestampPolicy = AppPreferencesStore.CurrentTimeTimestampPolicy;
            _preferences.UseCustomEncryptOutputDirectory = false;
            _preferences.CustomEncryptOutputDirectory = string.Empty;
            _preferences.UseCustomDecryptOutputDirectory = true;
            _preferences.CustomDecryptOutputDirectory = GetDefaultDecryptOutputFolder();

            _updateSettings.AutoCheckEnabled = true;
            try
            {
                UpdateService.SaveSettings(_updateSettings);
                await AppPreferencesStore.SaveAsync(GetAppDataDirectory(), _preferences);
            }
            catch (Exception ex)
            {
                RestorePreferencesSnapshot(previousPreferences);
                _updateSettings.AutoCheckEnabled = previousAutoCheck;
                ApplyPreferencesToControls();
                LoadSettingsPreferences();
                SetStatus("Settings reset was not saved.");
                await ShowErrorDialogAsync($"Unable to reset settings: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
                return;
            }

            ApplyThemePreference(_themePreference, persist: false, syncControls: true);
            ApplyPreferencesToControls();
            LoadSettingsPreferences();
            RefreshHistoryItems();
            SetAboutUpdateStatusText("Updates: automatic checks enabled");
            SetStatus("Settings were reset to defaults.");
        }

        private PreferencesSnapshot CapturePreferencesSnapshot()
        {
            return new PreferencesSnapshot(
                _preferences.IncognitoMode,
                _preferences.HasSelectedExperienceLevel,
                _preferences.ExperienceLevel,
                _preferences.IncludeFullPathsInExports,
                _preferences.OutputTimestampPolicy,
                _preferences.UseCustomEncryptOutputDirectory,
                _preferences.CustomEncryptOutputDirectory,
                _preferences.UseCustomDecryptOutputDirectory,
                _preferences.CustomDecryptOutputDirectory,
                _preferences.ThemePreference);
        }

        private void RestorePreferencesSnapshot(PreferencesSnapshot snapshot)
        {
            _preferences.IncognitoMode = snapshot.IncognitoMode;
            _preferences.HasSelectedExperienceLevel = snapshot.HasSelectedExperienceLevel;
            _preferences.ExperienceLevel = snapshot.ExperienceLevel;
            _preferences.IncludeFullPathsInExports = snapshot.IncludeFullPathsInExports;
            _preferences.OutputTimestampPolicy = snapshot.OutputTimestampPolicy;
            _preferences.UseCustomEncryptOutputDirectory = snapshot.UseCustomEncryptOutputDirectory;
            _preferences.CustomEncryptOutputDirectory = snapshot.CustomEncryptOutputDirectory;
            _preferences.UseCustomDecryptOutputDirectory = snapshot.UseCustomDecryptOutputDirectory;
            _preferences.CustomDecryptOutputDirectory = snapshot.CustomDecryptOutputDirectory;
            _preferences.ThemePreference = snapshot.ThemePreference;
            _themePreference = snapshot.ThemePreference;
        }

        private sealed record PreferencesSnapshot(
            bool IncognitoMode,
            bool HasSelectedExperienceLevel,
            UserExperienceLevel ExperienceLevel,
            bool IncludeFullPathsInExports,
            string OutputTimestampPolicy,
            bool UseCustomEncryptOutputDirectory,
            string CustomEncryptOutputDirectory,
            bool UseCustomDecryptOutputDirectory,
            string CustomDecryptOutputDirectory,
            ThemePreference ThemePreference);
    }
}

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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
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
        // --- Dialog Helpers ---
        private async Task ShowErrorDialogAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = message,
                PrimaryButtonText = "OK"
            };
            await ShowContentDialogAsync(dialog);
        }

        private async Task ShowInfoDialogAsync(string message, string title)
        {
            await ShowInfoDialogAsync((object)message, title);
        }

        private async Task ShowInfoDialogAsync(object content, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "OK"
            };
            await ShowContentDialogAsync(dialog);
        }

        private async Task<bool> ShowConfirmDialogAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "Yes",
                SecondaryButtonText = "No"
            };
            var result = await ShowContentDialogAsync(dialog);
            return result == ContentDialogResult.Primary;
        }

        private Task<bool> ConfirmSourceDeletionAsync(ProcessingIntent intent, int itemCount)
        {
            string sourceDescription = intent == ProcessingIntent.Encrypt
                ? "original source files"
                : "encrypted source files";
            string operationDescription = intent == ProcessingIntent.Encrypt
                ? "encryption"
                : "decryption";

            return ShowConfirmDialogAsync(
                $"You enabled deletion of {sourceDescription} for {itemCount} queued item(s).\n\nFileLocker only deletes a source after that item completes {operationDescription} successfully. Failed, skipped, or cancelled items are kept.\n\nContinue?",
                intent == ProcessingIntent.Encrypt
                    ? "Delete Originals After Encryption?"
                    : "Delete Encrypted Sources After Decryption?");
        }

        private async Task<string?> PromptForTextAsync(string title, string prompt, string defaultValue)
        {
            var inputBox = new TextBox
            {
                Text = defaultValue,
                PlaceholderText = "Name"
            };

            var panel = new StackPanel
            {
                Spacing = 12
            };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(inputBox);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Cancel"
            };

            var result = await ShowContentDialogAsync(dialog);
            return result == ContentDialogResult.Primary ? inputBox.Text : null;
        }

        private async Task<ContentDialogResult> ShowContentDialogAsync(ContentDialog dialog)
        {
            if (!TryGetDialogXamlRoot(out XamlRoot? xamlRoot))
            {
                return ContentDialogResult.None;
            }

            await _dialogSemaphore.WaitAsync();
            try
            {
                if (!TryGetDialogXamlRoot(out xamlRoot))
                {
                    return ContentDialogResult.None;
                }

                dialog.XamlRoot = xamlRoot;
                return await dialog.ShowAsync();
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"ContentDialog failed to open: {ex.Message}");
                return ContentDialogResult.None;
            }
            catch (OperationCanceledException)
            {
                return ContentDialogResult.None;
            }
            finally
            {
                _dialogSemaphore.Release();
            }
        }

        private bool TryGetDialogXamlRoot(out XamlRoot? xamlRoot)
        {
            xamlRoot = null;
            if (_isWindowClosed || Content is not FrameworkElement root)
            {
                return false;
            }

            xamlRoot = root.XamlRoot;
            return xamlRoot != null;
        }

        private async Task StartAutomaticUpdateCheckAsync()
        {
            if (_hasStartedAutomaticUpdateCheck)
            {
                return;
            }

            _hasStartedAutomaticUpdateCheck = true;

            if (!UpdateService.ShouldPerformAutomaticCheck(_updateSettings, DateTimeOffset.UtcNow))
            {
                SetAboutUpdateStatusText("Updates: automatic checks enabled");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            await CheckForUpdatesAsync(isManualCheck: false);
        }

        private async Task CheckForUpdatesAsync(bool isManualCheck)
        {
            if (_isCheckingForUpdates || _isDownloadingUpdate)
            {
                return;
            }

            try
            {
                _isCheckingForUpdates = true;
                SetAboutUpdateStatusText("Updates: checking...");

                UpdateCheckResult result = await UpdateService.CheckForUpdatesAsync(CancellationToken.None);
                _updateSettings.LastCheckedUtc = DateTimeOffset.UtcNow;
                UpdateService.SaveSettings(_updateSettings);

                if (!result.IsUpdateAvailable || result.Release == null)
                {
                    _updateSettings.SkippedVersion = null;
                    UpdateService.SaveSettings(_updateSettings);
                    SetAboutUpdateStatusText(result.StatusMessage);

                    if (isManualCheck)
                    {
                        await ShowInfoDialogAsync(result.StatusMessage, "Updates");
                    }

                    return;
                }

                if (string.Equals(_updateSettings.SkippedVersion, result.Release.DisplayVersion, StringComparison.OrdinalIgnoreCase))
                {
                    SetAboutUpdateStatusText($"Update available: {result.Release.DisplayVersion} (skipped)");
                    if (!isManualCheck)
                    {
                        return;
                    }
                }
                else
                {
                    SetAboutUpdateStatusText($"Update available: {result.Release.DisplayVersion}");
                }

                await PromptToInstallUpdateAsync(result.Release, isManualCheck);
            }
            catch (Exception ex)
            {
                SetAboutUpdateStatusText("Updates: check failed");
                if (isManualCheck)
                {
                    await ShowErrorDialogAsync($"Unable to check for updates:\n{ex.Message}");
                }
            }
            finally
            {
                _isCheckingForUpdates = false;
            }
        }

        private async Task PromptToInstallUpdateAsync(UpdateReleaseInfo release, bool isManualCheck)
        {
            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 520
            };

            panel.Children.Add(new TextBlock
            {
                Text = $"FileLocker {release.DisplayVersion} is available. You are currently running {UpdateService.GetCurrentVersionLabel()}.",
                TextWrapping = TextWrapping.WrapWholeWords
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Release notes",
                FontWeight = FontWeights.SemiBold
            });

            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(release.Notes)
                        ? "No release notes were provided for this release."
                        : release.Notes,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.WrapWholeWords
                }
            });

            var dialog = new ContentDialog
            {
                Title = "Update Available",
                Content = panel,
                PrimaryButtonText = "Download && Install",
                SecondaryButtonText = "View Release",
                CloseButtonText = isManualCheck ? "Not Now" : "Skip This Version",
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult result = await ShowContentDialogAsync(dialog);

            if (result == ContentDialogResult.Primary)
            {
                await DownloadAndInstallUpdateAsync(release);
                return;
            }

            if (result == ContentDialogResult.Secondary)
            {
                OpenWithShell(release.HtmlUrl);
                return;
            }

            if (!isManualCheck)
            {
                _updateSettings.SkippedVersion = release.DisplayVersion;
                UpdateService.SaveSettings(_updateSettings);
                SetAboutUpdateStatusText($"Update available: {release.DisplayVersion} (skipped)");
            }
        }

        private async Task DownloadAndInstallUpdateAsync(UpdateReleaseInfo release)
        {
            try
            {
                _isDownloadingUpdate = true;
                SetAboutUpdateStatusText($"Updates: downloading {release.DisplayVersion}...");
                SetStatus($"Downloading FileLocker {release.DisplayVersion} update...");

                string installerPath = await UpdateService.DownloadInstallerAsync(release, CancellationToken.None);

                _updateSettings.SkippedVersion = null;
                UpdateService.SaveSettings(_updateSettings);

                SetAboutUpdateStatusText($"Updates: ready to install {release.DisplayVersion}");
                SetStatus($"Launching FileLocker {release.DisplayVersion} installer...");
                LaunchInstallerAndExit(installerPath);
            }
            catch (Exception ex)
            {
                SetAboutUpdateStatusText("Updates: download failed");
                await ShowErrorDialogAsync($"Unable to download the update:\n{ex.Message}");
            }
            finally
            {
                _isDownloadingUpdate = false;
            }
        }

        private void LaunchInstallerAndExit(string installerPath)
        {
            string escapedInstallerPath = installerPath.Replace("\"", "\"\"", StringComparison.Ordinal);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{escapedInstallerPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            Close();
        }

        private void SetAboutUpdateStatusText(string text)
        {
            _aboutUpdateStatusText = text;
            AboutUpdateStatusMenuItem.Text = text;
            if (UpdateStatusText != null)
            {
                UpdateSettingsUpdateStatusText();
            }
        }

        private FileOpenPicker CreateOpenFilePicker(PickerLocationId startLocation, params string[] filters)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = startLocation
            };

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            IEnumerable<string> effectiveFilters = filters.Length == 0 ? new[] { "*" } : filters;
            foreach (string filter in effectiveFilters)
            {
                picker.FileTypeFilter.Add(filter);
            }

            return picker;
        }

        private FolderPicker CreateFolderPicker(PickerLocationId startLocation)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = startLocation
            };

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            return picker;
        }

        private FileSavePicker CreateSaveFilePicker(PickerLocationId startLocation, string suggestedFileName)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = startLocation,
                SuggestedFileName = suggestedFileName
            };

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            return picker;
        }

        private async void BrowseKeyfileButton_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = CreateOpenFilePicker(PickerLocationId.DocumentsLibrary, "*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                KeyfilePathBox.Text = file.Path;
                SetStatus($"Keyfile selected: {Path.GetFileName(file.Path)}");
            }
        }

        private async void BrowseBackupFolderButton_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                BackupFolderBox.Text = folder.Path;
                SetStatus($"Backup folder selected: {folder.Name}");
            }
        }

        private async Task<bool> EnsureEncryptOutputDestinationAsync()
        {
            if (OutputCustomLocationRadio.IsChecked != true)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text))
            {
                return true;
            }

            FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return false;
            }

            EncryptOutputFolderBox.Text = folder.Path;
            SetStatus($"Encrypt output folder selected: {folder.Name}");
            return true;
        }

        private async void BrowseEncryptOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await EnsureEncryptOutputDestinationAsync();
        }

        private void KeyfilePathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void RecoveryKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void BackupFolderBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void EncryptOutputOption_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady) return;
            UpdateEncryptDestinationUi();
        }

        private void EncryptOutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady) return;
            UpdateEncryptDestinationUi();
        }

        private void DropPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimateDropPanel(true);

            if (FileList.Count == 0)
            {
                DropHintText.Text = "Drop files or folders anywhere in this area.";
            }
        }

        private void DropPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimateDropPanel(false);
            DropLabel.Text = DefaultDropLabelText;
            DropLabel.FontWeight = FontWeights.SemiBold;
            UpdateDropHint();
        }

        private void SafetyOption_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            SecureDeleteOriginalsToggle.IsEnabled = RemoveOriginalsToggle.IsOn;
            if (!RemoveOriginalsToggle.IsOn)
            {
                SecureDeleteOriginalsToggle.IsOn = false;
            }

            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void OutputTimestampPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            _preferences.OutputTimestampPolicy = (OutputTimestampPolicyCombo.SelectedItem as ComboBoxItem)?.Content as string
                ?? "Current time";
            PersistPreferences();
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
        }

        private void HistoryPrivacyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            _preferences.HistoryPrivacyMode = HistoryPrivacyCombo.SelectedIndex switch
            {
                0 => HistoryPrivacyMode.Off,
                2 => HistoryPrivacyMode.Full,
                _ => HistoryPrivacyMode.Redacted
            };

            PersistPreferences();
            SaveHistory();
            RefreshHistoryItems();
        }

        private void HistoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            _historyOperationFilter = GetSelectedComboText(HistoryOperationFilterCombo, "All operations");
            _historyStatusFilter = GetSelectedComboText(HistoryStatusFilterCombo, "All statuses");
            RefreshHistoryItems();
        }

        private static string GetSelectedComboText(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
        }

        private void FullPathExportsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            _preferences.IncludeFullPathsInExports = FullPathExportsToggle.IsOn;
            PersistPreferences();
        }

        private void GenerateRecoveryKeyButton_Click(object sender, RoutedEventArgs e)
        {
            RecoveryKeyBox.Text = GenerateRecoveryKey();
            SetStatus("Recovery key generated. Store it somewhere separate from the encrypted file.");
        }

        private static string GenerateRecoveryKey()
        {
            byte[] keyBytes = new byte[24];
            RandomNumberGenerator.Fill(keyBytes);
            string hex = Convert.ToHexString(keyBytes);
            var parts = new List<string>();
            for (int i = 0; i < hex.Length; i += 6)
            {
                parts.Add(hex.Substring(i, Math.Min(6, hex.Length - i)));
            }

            return string.Join("-", parts);
        }

        private async void RegisterExplorerIntegrationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await RunExplorerIntegrationScriptAsync(unregister: false);
                await ShowInfoDialogAsync("Explorer actions were enabled for the current user.", "Explorer Integration");
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to enable Explorer actions: {ex.Message}");
            }
        }

        private async void RemoveExplorerIntegrationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await RunExplorerIntegrationScriptAsync(unregister: true);
                await ShowInfoDialogAsync("Explorer actions were removed for the current user.", "Explorer Integration");
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to remove Explorer actions: {ex.Message}");
            }
        }

        private async Task RunExplorerIntegrationScriptAsync(bool unregister)
        {
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException("The current executable path could not be determined.");
            }

            string scriptPath = Path.Combine(AppContext.BaseDirectory, "Register-ExplorerIntegration.ps1");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Register-ExplorerIntegration.ps1");
            }

            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(GetAppDataDirectory(), "..", "..", "FileLocker", "Register-ExplorerIntegration.ps1");
                scriptPath = Path.GetFullPath(scriptPath);
            }

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("Register-ExplorerIntegration.ps1 could not be found.", scriptPath);
            }

            string arguments = unregister
                ? $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -ExecutablePath \"{executablePath}\" -Unregister"
                : $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -ExecutablePath \"{executablePath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start PowerShell.");
            string stderr = await process.StandardError.ReadToEndAsync();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            }
        }

        private void CancelRunButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                _processingCancellation?.Cancel();
                SetStatus("Cancellation requested. The current item will finish before the queue stops.");
            }
        }

        private async void ExportMarkdownButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OperationHistoryEntry entry = GetSelectedHistoryEntry();
                string? path = await SaveReportWithPickerAsync(entry, "md", BuildMarkdownReport(entry, _preferences.IncludeFullPathsInExports));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    await ShowInfoDialogAsync($"Markdown receipt saved to:\n{path}", "Receipt Exported");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to export Markdown receipt: {GetFriendlyExceptionMessage(ex, "Export failed.")}");
            }
        }

        private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OperationHistoryEntry entry = GetSelectedHistoryEntry();
                string? path = await SaveReportWithPickerAsync(entry, "csv", BuildCsvReport(entry, _preferences.IncludeFullPathsInExports));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    await ShowInfoDialogAsync($"CSV receipt saved to:\n{path}", "Receipt Exported");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to export CSV receipt: {GetFriendlyExceptionMessage(ex, "Export failed.")}");
            }
        }

        private OperationHistoryEntry GetSelectedHistoryEntry()
        {
            string? selectedId = (RecentJobsListView.SelectedItem as JobHistoryItem)?.Id
                ?? _jobHistoryItems.FirstOrDefault()?.Id;

            if (string.IsNullOrWhiteSpace(selectedId))
            {
                throw new InvalidOperationException("There is no job history to export yet.");
            }

            return _operationHistory.First(history => history.Id == selectedId);
        }

        private async Task<string?> SaveReportWithPickerAsync(OperationHistoryEntry entry, string format, string contents)
        {
            string safeOperation = entry.Operation.Replace(" ", "_", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
            string fileName = $"{safeOperation}_{entry.TimestampUtc:yyyyMMdd_HHmmss}.{format}";
            FileSavePicker picker = CreateSaveFilePicker(PickerLocationId.DocumentsLibrary, Path.GetFileNameWithoutExtension(fileName));
            picker.FileTypeChoices.Add(
                string.Equals(format, "md", StringComparison.OrdinalIgnoreCase) ? "Markdown Receipt" : "CSV Receipt",
                [$".{format}"]);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return null;
            }

            File.WriteAllText(file.Path, contents);
            return file.Path;
        }

        private static string BuildMarkdownReport(OperationHistoryEntry entry, bool includeFullPaths)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"# FileLocker {entry.Operation} Operation Receipt");
            builder.AppendLine();
            builder.AppendLine($"- Timestamp: {entry.TimestampUtc.ToLocalTime():f}");
            builder.AppendLine($"- Profile: {entry.ProfileName}");
            builder.AppendLine($"- Algorithm: {entry.Algorithm} ({entry.KeySizeBits}-bit)");
            builder.AppendLine($"- Keyfile used: {(entry.UsedKeyfile ? "Yes" : "No")}");
            builder.AppendLine($"- Originals removed: {(entry.RemoveOriginalsAfterSuccess ? "Yes" : "No")}");
            builder.AppendLine($"- Secure delete: {(entry.SecureDeleteOriginals ? "Yes" : "No")}");
            builder.AppendLine($"- Verify after write: {(entry.VerifyAfterWrite ? "Yes" : "No")}");
            builder.AppendLine($"- Backup folder: {(string.IsNullOrWhiteSpace(entry.BackupFolderPath) ? "Not configured" : RenderReportPath(entry.BackupFolderPath, includeFullPaths, "Not configured"))}");
            AppendReceiptMetric(builder, "Original bytes", FormatReportSize(entry.TotalOriginalSizeBytes));
            AppendReceiptMetric(builder, "Output bytes", FormatReportSize(entry.TotalOutputSizeBytes));
            AppendReceiptMetric(builder, "Compression savings", FormatReportSize(entry.TotalStorageSavedBytes));
            AppendReceiptMetric(builder, "Compression increase", FormatReportSize(entry.TotalStorageAddedBytes));
            AppendReceiptMetric(builder, "Elapsed", FormatReportElapsed(entry.ElapsedMilliseconds));
            AppendReceiptMetric(builder, "Compression", entry.CompressionRequestedCount > 0
                ? $"{entry.CompressionAppliedCount}/{entry.CompressionRequestedCount} item(s) compressed"
                : string.Empty);
            AppendReceiptMetric(builder, "Failure categories", entry.FailureCategorySummary);
            builder.AppendLine();
            builder.AppendLine("| Source | Output | Status | Message | Hash | Original | Output | Compression Delta | Compression | Failure |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

            foreach (var result in entry.Results)
            {
                builder.AppendLine(
                    $"| {EscapeMarkdown(RenderReportPath(result.SourcePath, includeFullPaths))} " +
                    $"| {EscapeMarkdown(RenderReportPath(result.OutputPath, includeFullPaths, "-"))} " +
                    $"| {EscapeMarkdown(result.Status)} " +
                    $"| {EscapeMarkdown(RenderReportMessage(result.Message, includeFullPaths))} " +
                    $"| {EscapeMarkdown(result.HashValue ?? "-")} " +
                    $"| {EscapeMarkdown(FormatReportSize(result.OriginalSizeBytes))} " +
                    $"| {EscapeMarkdown(FormatReportSize(result.OutputSizeBytes))} " +
                    $"| {EscapeMarkdown(FormatReportDelta(result))} " +
                    $"| {EscapeMarkdown(FormatCompressionReceipt(result))} " +
                    $"| {EscapeMarkdown(result.FailureCategory ?? "-")} |");
            }

            return builder.ToString();
        }

        private static string BuildCsvReport(OperationHistoryEntry entry, bool includeFullPaths)
        {
            var builder = new StringBuilder();
            builder.AppendLine("SourcePath,OutputPath,Status,Message,HashValue,BackupPath,OriginalRetained,OutputVerified,OriginalBytes,OutputBytes,CompressionSavedBytes,CompressionAddedBytes,CompressionRequested,CompressionApplied,CompressionReason,CompressedBytes,ElapsedMilliseconds,FailureCategory");

            foreach (var result in entry.Results)
            {
                builder.AppendLine(string.Join(",",
                    EscapeCsv(RenderReportPath(result.SourcePath, includeFullPaths)),
                    EscapeCsv(RenderReportPath(result.OutputPath, includeFullPaths)),
                    EscapeCsv(result.Status),
                    EscapeCsv(RenderReportMessage(result.Message, includeFullPaths)),
                    EscapeCsv(result.HashValue ?? string.Empty),
                    EscapeCsv(RenderReportPath(result.BackupPath, includeFullPaths)),
                    result.OriginalRetained ? "true" : "false",
                    result.OutputVerified ? "true" : "false",
                    result.OriginalSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    result.OutputSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    result.NetStorageSavedBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    result.NetStorageAddedBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    result.CompressionRequested ? "true" : "false",
                    result.CompressionApplied ? "true" : "false",
                    EscapeCsv(result.CompressionReason ?? string.Empty),
                    result.CompressedSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    result.ElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    EscapeCsv(result.FailureCategory ?? string.Empty)));
            }

            return builder.ToString();
        }

        private static void AppendReceiptMetric(StringBuilder builder, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.AppendLine($"- {label}: {value}");
            }
        }

        private static string RenderReportMessage(string? message, bool includeFullPaths)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "-";
            }

            return includeFullPaths ? message : SensitiveDataRedactor.RedactMessage(message);
        }

        private static string FormatReportSize(long? bytes)
        {
            return bytes.HasValue ? FormatDashboardFileSize(bytes.Value) : string.Empty;
        }

        private static string FormatReportElapsed(long? elapsedMilliseconds)
        {
            if (!elapsedMilliseconds.HasValue)
            {
                return string.Empty;
            }

            return elapsedMilliseconds.Value < 1000
                ? $"{elapsedMilliseconds.Value} ms"
                : $"{TimeSpan.FromMilliseconds(elapsedMilliseconds.Value).TotalSeconds:0.0} s";
        }

        private static string FormatReportDelta(FileOperationResult result)
        {
            if (result.NetStorageSavedBytes is > 0)
            {
                return $"Saved {FormatDashboardFileSize(result.NetStorageSavedBytes.Value)}";
            }

            if (result.NetStorageAddedBytes is > 0)
            {
                return $"Increased {FormatDashboardFileSize(result.NetStorageAddedBytes.Value)}";
            }

            return "-";
        }

        private static string FormatCompressionReceipt(FileOperationResult result)
        {
            if (!result.CompressionRequested)
            {
                return "-";
            }

            string state = result.CompressionApplied ? "Applied" : "Skipped";
            return string.IsNullOrWhiteSpace(result.CompressionReason)
                ? state
                : $"{state}: {result.CompressionReason}";
        }

        private static string RenderReportPath(string? path, bool includeFullPaths, string emptyFallback = "")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return emptyFallback;
            }

            return includeFullPaths ? path : RedactPath(path);
        }

        private static string EscapeMarkdown(string text)
        {
            return text.Replace("|", "\\|", StringComparison.Ordinal);
        }

        private static string EscapeCsv(string text)
        {
            if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            {
                return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            }

            return text;
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            UpdateAboutMenuInfo();
            FlyoutBase.ShowAttachedFlyout(AboutButton);
            await Task.CompletedTask;
        }

        private async void ShowTutorialButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowTutorialDialogAsync(includeGlossary: true);
        }

        private async void ShowTutorialMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowTutorialDialogAsync(includeGlossary: true);
        }

        private async void ShowGlossaryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowTutorialDialogAsync(includeGlossary: true, glossaryOnly: true);
        }

        private async Task ShowTutorialDialogAsync(bool includeGlossary, bool glossaryOnly = false)
        {
            var content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 680
            };

            if (!glossaryOnly)
            {
                content.Children.Add(CreateHelpSection(
                    "Encrypting files",
                    "Select files or folders, choose where output should go, enter and confirm a strong password, then review the operation summary before starting encryption."));

                content.Children.Add(CreateHelpSection(
                    "Decrypting files",
                    "Open Decrypt Files, add supported .locked files, enter the original password used during encryption, and choose the output location before starting."));

                content.Children.Add(CreateHelpSection(
                    "Hashing files",
                    "Use Hash Files to generate SHA-256 checksums and compare them with an expected hash. Whitespace and case differences are normalized for verification."));

                content.Children.Add(CreateHelpSection(
                    "Password safety",
                    "FileLocker cannot recover lost or incorrect passwords. Passwords are not saved in preferences, history, logs, or recent files."));
            }

            if (includeGlossary)
            {
                content.Children.Add(CreateHelpSection(
                    "Local-first privacy",
                    "File operations run on this device. Activity history, if enabled, is stored locally and can be cleared from Settings."));

                content.Children.Add(CreateHelpSection(
                    "What FileLocker can recover",
                    "FileLocker can decrypt only valid FileLocker encrypted files when the original password is supplied. It cannot recover forgotten passwords or repair corrupted encrypted payloads."));

                content.Children.Add(CreateHelpSection(
                    "Storage Impact",
                    "Compression savings are measured before encryption, so the dashboard reports the payload space compression actually saved or increased."));

                content.Children.Add(CreateHelpSection(
                    "Destructive options",
                    "Deleting originals or encrypted sources is off by default, requires confirmation, and only runs after the matching operation succeeds."));
            }

            var dialog = new ContentDialog
            {
                Title = glossaryOnly ? "Security Glossary" : "Quick Start Guide",
                Content = new ScrollViewer
                {
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content
                },
                PrimaryButtonText = "Close"
            };

            await ShowContentDialogAsync(dialog);
        }

        private static Border CreateHelpSection(string title, string body)
        {
            var section = new StackPanel
            {
                Spacing = 6
            };

            section.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrushResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            section.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = GetBrushResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            return new Border
            {
                Background = GetBrushResource("InputSurfaceBrush"),
                BorderBrush = GetBrushResource("AppBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Child = section
            };
        }

        private static string GetFriendlyExceptionMessage(Exception ex, string fallback)
        {
            Exception? current = ex;
            while (current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                {
                    return SensitiveDataRedactor.RedactMessage(current.Message);
                }

                current = current.InnerException;
            }

            return fallback;
        }

        private void ShowAdvancedModeWarningButton_Click(object sender, RoutedEventArgs e)
        {
            AdvancedModeInfoBar.IsOpen = true;
            ApplyInspectorView("Setup");
        }

        private async void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(isManualCheck: true);
        }

        private void OpenGitHubMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenWithShell(UpdateService.GitHubRepositoryUrl);
        }

        private void OpenReportsFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenWithShell(GetReportsDirectory());
        }

        private void OpenInstallFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string? processPath = Environment.ProcessPath;
            string installDirectory = !string.IsNullOrWhiteSpace(processPath)
                ? Path.GetDirectoryName(processPath) ?? GetAppDataDirectory()
                : GetAppDataDirectory();
            OpenWithShell(installDirectory);
        }

        private void OpenUpdateDownloadsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileLocker",
                "Updater",
                "Downloads");
            Directory.CreateDirectory(path);
            OpenWithShell(path);
        }

        private void OpenAppDataFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenWithShell(GetAppDataDirectory());
        }

        private static void OpenWithShell(string target)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }

        // Advanced options event handlers
        private void CompressModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggleSwitch)
            {
                IsCompressModeEnabled = toggleSwitch.IsOn;
            }

            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void ScrambleNamesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggleSwitch)
            {
                IsScrambleNamesEnabled = toggleSwitch.IsOn;
            }

            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void SteganographyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggleSwitch)
            {
                IsSteganographyEnabled = toggleSwitch.IsOn;
            }

            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }

        private void PackageFoldersToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggleSwitch)
            {
                IsPackageFoldersEnabled = toggleSwitch.IsOn;
            }

            if (!_isUiReady) return;
            if (!_isSyncingEncryptFilesUi)
            {
                SynchronizeEncryptOptionsFromWorkflow();
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            RefreshEncryptFilesState();
        }
        // At class level:
        private static readonly uint[] Crc32Table = CreateCrc32Table();

        private static uint[] CreateCrc32Table()
        {
            const uint Polynomial = 0xEDB88320u;
            var table = new uint[256];

            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ Polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }

            return table;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint result = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                result = (result >> 8) ^ Crc32Table[(result ^ b) & 0xFF];
            }
            return ~result;
        }
    }
}


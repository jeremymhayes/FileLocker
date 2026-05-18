using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private bool _isSyncingDecryptFilesUi;

        private void InitializeDecryptFilesView()
        {
            DecryptSelectedFilesListView.ItemsSource = DecryptSelectedFiles;

            _isSyncingDecryptFilesUi = true;
            try
            {
                DecryptOutputLocationBox.Text = string.IsNullOrWhiteSpace(_preferences.CustomDecryptOutputDirectory)
                    ? GetDefaultDecryptOutputFolder()
                    : _preferences.CustomDecryptOutputDirectory;
                DecryptSaveNextToEncryptedToggle.IsOn = !_preferences.UseCustomDecryptOutputDirectory;
                DecryptRestoreOriginalFilenamesToggle.IsOn = true;
                DecryptPreserveFolderStructureToggle.IsOn = true;
                DecryptDeleteEncryptedAfterSuccessToggle.IsOn = false;
            }
            finally
            {
                _isSyncingDecryptFilesUi = false;
            }

            UpdateDecryptDestinationUi(persistPreferences: false);
            RefreshDecryptFilesState();
        }

        private void PrepareDecryptFilesSection()
        {
            RefreshDecryptFilesState();
        }

        private void RefreshDecryptFilesState()
        {
            if (DecryptSelectedFilesTitleText == null)
            {
                return;
            }

            int fileCount = DecryptSelectedFiles.Count;
            long totalSize = DecryptSelectedFiles.Sum(item => item.SizeBytes);
            bool hasPassword = !string.IsNullOrWhiteSpace(DecryptPasswordBox.Password);

            foreach (DecryptSelectedItemViewModel item in DecryptSelectedFiles)
            {
                if (!item.IsSupportedEncryptedFile ||
                    string.Equals(item.Status, "Processing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (hasPassword)
                {
                    item.SetReady();
                }
                else
                {
                    item.SetPasswordRequired();
                }
            }

            DecryptSelectedFilesTitleText.Text = $"Selected Encrypted Files ({fileCount})";
            DecryptSummaryFilesText.Text = fileCount.ToString(CultureInfo.InvariantCulture);
            DecryptSummarySizeText.Text = FormatFileSize(totalSize);
            DecryptSummaryOutputText.Text = DecryptSaveNextToEncryptedToggle.IsOn
                ? "Next to encrypted files"
                : string.IsNullOrWhiteSpace(DecryptOutputLocationBox.Text)
                    ? "Choose output folder"
                    : Path.GetFileName(DecryptOutputLocationBox.Text.Trim());
            DecryptSummaryModeText.Text = "AES-256-GCM";

            DecryptSelectedFilesEmptyState.Visibility = fileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            DecryptSelectedFilesListView.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;

            (string statusText, Brush statusBrush, string statusGlyph) = GetDecryptOperationStatus();
            DecryptSummaryStatusText.Text = statusText;
            DecryptSummaryStatusText.Foreground = statusBrush;
            DecryptSummaryStatusIcon.Foreground = statusBrush;
            DecryptSummaryStatusIcon.Glyph = statusGlyph;

            bool canStart = CanStartDecryption();
            StartDecryptionButton.IsEnabled = canStart;
            DecryptClearSelectionButton.IsEnabled = fileCount > 0 && !_isProcessing;
            DecryptPanelClearSelectionButton.IsEnabled = fileCount > 0 && !_isProcessing;
            DecryptPanelClearSelectionButton.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            DecryptOutputLocationBox.IsEnabled = !_isProcessing && !DecryptSaveNextToEncryptedToggle.IsOn;
            DecryptOutputBrowseButton.IsEnabled = !_isProcessing && !DecryptSaveNextToEncryptedToggle.IsOn;
        }

        private (string StatusText, Brush StatusBrush, string StatusGlyph) GetDecryptOperationStatus()
        {
            Brush neutral = GetBrushResource("TextSecondaryBrush");
            Brush success = GetBrushResource("SuccessBrush");
            Brush danger = GetBrushResource("DangerBrush");
            Brush warning = GetBrushResource("WarningBrush");
            Brush accent = GetBrushResource("BrightBlueBrush");

            if (_isProcessing)
            {
                return ("Decrypting", accent, "\uE768");
            }

            if (DecryptSelectedFiles.Count == 0)
            {
                return ("Waiting for files", neutral, "\uE9CE");
            }

            if (DecryptSelectedFiles.All(item => string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase)))
            {
                return ("Complete", success, "\uE73E");
            }

            if (DecryptSelectedFiles.Any(item => !item.IsSupportedEncryptedFile) ||
                DecryptSelectedFiles.Any(item => string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return ("Failed", danger, "\uE783");
            }

            if (string.IsNullOrWhiteSpace(DecryptPasswordBox.Password))
            {
                return ("Waiting for password", warning, "\uE72E");
            }

            if (!IsDecryptOutputValid())
            {
                return ("Choose output folder", neutral, "\uE8B7");
            }

            return ("Ready to decrypt", success, "\uE73E");
        }

        private bool CanStartDecryption()
        {
            return !_isProcessing &&
                DecryptSelectedFiles.Any(item => item.CanDecrypt) &&
                DecryptSelectedFiles.All(item => item.IsSupportedEncryptedFile) &&
                !string.IsNullOrWhiteSpace(DecryptPasswordBox.Password) &&
                IsDecryptOutputValid();
        }

        private bool IsDecryptOutputValid()
        {
            if (DecryptSaveNextToEncryptedToggle.IsOn)
            {
                return true;
            }

            string path = DecryptOutputLocationBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                return !string.IsNullOrWhiteSpace(Path.GetPathRoot(fullPath));
            }
            catch
            {
                return false;
            }
        }

        private async void DecryptBrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = CreateOpenFilePicker(PickerLocationId.DocumentsLibrary, ENCRYPTED_EXTENSION, ".png");
            picker.FileTypeFilter.Add("*");

            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0)
            {
                return;
            }

            AddDecryptPathsToSelection(files.Select(file => file.Path));
        }

        private async void DecryptBrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            AddDecryptPathsToSelection([folder.Path]);
        }

        private async void DecryptDropPanel_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            DataPackageView dataView = e.DataView;
            var deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<IStorageItem> items = await dataView.GetStorageItemsAsync();
                DecryptDropLabel.Text = items.Count > 0
                    ? $"Release {items.Count} item(s) to inspect"
                    : "Release to add encrypted files";
                DecryptDropHintText.Text = "FileLocker will queue supported encrypted files only.";
                SetDecryptDropVisual(true);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void DecryptDropPanel_Drop(object sender, DragEventArgs e)
        {
            SetDecryptDropVisual(false);
            ResetDecryptDropText();

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<IStorageItem> storageItems = await e.DataView.GetStorageItemsAsync();
                AddDecryptPathsToSelection(storageItems.Select(item => item.Path));
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void DecryptDropPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetDecryptDropVisual(true);
        }

        private void DecryptDropPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            SetDecryptDropVisual(false);
            ResetDecryptDropText();
        }

        private void SetDecryptDropVisual(bool active)
        {
            DecryptDropPanel.Background = active
                ? GetBrushResource("DropPanelActiveBrush")
                : GetBrushResource("HeroSurfaceBrush");
            DecryptDropPanel.BorderBrush = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            DecryptDropPanelDashBorder.Stroke = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            DecryptDropIconTile.Background = active
                ? GetBrushResource("PrimaryActionBrush")
                : GetBrushResource("AccentSoftBrush");
        }

        private void ResetDecryptDropText()
        {
            DecryptDropLabel.Text = "Drag & drop encrypted files to decrypt";
            DecryptDropHintText.Text = "Supports FileLocker encrypted files and batch decryption";
        }

        private void AddDecryptPathsToSelection(IEnumerable<string> rawPaths)
        {
            int addedCount = 0;
            int duplicateCount = 0;
            int unsupportedCount = 0;
            int warningCount = 0;

            foreach (string rawPath in rawPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                string path = rawPath.Trim();
                if (File.Exists(path))
                {
                    AddDecryptFile(path, path, sourceRootIsFolder: false, includeUnsupportedRow: true, ref addedCount, ref duplicateCount, ref unsupportedCount);
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    warningCount++;
                    continue;
                }

                var warnings = new List<string>();
                foreach (ExpandedQueueFile expandedFile in EnumerateFolderFiles(path, warnings))
                {
                    AddDecryptFile(
                        expandedFile.Path,
                        expandedFile.RootPath,
                        expandedFile.RootIsFolder,
                        includeUnsupportedRow: false,
                        ref addedCount,
                        ref duplicateCount,
                        ref unsupportedCount);
                }

                warningCount += warnings.Count;
            }

            if (addedCount > 0 || duplicateCount > 0 || unsupportedCount > 0 || warningCount > 0)
            {
                string summary = $"Added {addedCount} encrypted file(s).";
                if (duplicateCount > 0)
                {
                    summary += $" Skipped {duplicateCount} duplicate(s).";
                }

                if (unsupportedCount > 0)
                {
                    summary += $" {unsupportedCount} unsupported item(s) were not queued for decryption.";
                }

                if (warningCount > 0)
                {
                    summary += $" {warningCount} folder item(s) could not be fully inspected.";
                }

                InfoBarSeverity severity = unsupportedCount > 0 || warningCount > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                ShowDecryptInfoBar(summary, severity);
                SetStatus(summary);
            }

            RefreshDecryptFilesState();
        }

        private void AddDecryptFile(
            string filePath,
            string sourceRootPath,
            bool sourceRootIsFolder,
            bool includeUnsupportedRow,
            ref int addedCount,
            ref int duplicateCount,
            ref int unsupportedCount)
        {
            bool supported = IsSupportedFileLockerEncryptedFile(filePath, out string detail);
            if (!supported && !includeUnsupportedRow)
            {
                unsupportedCount++;
                return;
            }

            if (!_decryptQueuedPaths.Add(filePath))
            {
                duplicateCount++;
                return;
            }

            long sizeBytes = 0;
            try
            {
                sizeBytes = new FileInfo(filePath).Length;
            }
            catch
            {
                supported = false;
                detail = "File size could not be read.";
            }

            string status = supported
                ? string.IsNullOrWhiteSpace(DecryptPasswordBox.Password) ? "Password required" : "Ready"
                : "Unsupported";
            string itemDetail = supported
                ? "FileLocker encrypted file ready for decryption."
                : detail;

            DecryptSelectedFiles.Add(new DecryptSelectedItemViewModel(
                filePath,
                sourceRootPath,
                sourceRootIsFolder,
                sizeBytes,
                supported,
                status,
                itemDetail));

            if (supported)
            {
                addedCount++;
            }
            else
            {
                unsupportedCount++;
            }
        }

        private static bool IsSupportedFileLockerEncryptedFile(string filePath, out string detail)
        {
            detail = "FileLocker encrypted file.";
            try
            {
                if (!File.Exists(filePath))
                {
                    detail = "Missing file.";
                    return false;
                }

                if (filePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase))
                {
                    using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (PayloadChunkedService.LooksLikePayloadV3(stream))
                    {
                        return true;
                    }

                    stream.Position = 0;
                    int version = stream.ReadByte();
                    if (version == FORMAT_VERSION)
                    {
                        return true;
                    }

                    detail = "Not a FileLocker encrypted file.";
                    return false;
                }

                if (filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    TryExtractStegoPayload(filePath) != null)
                {
                    detail = "FileLocker PNG payload.";
                    return true;
                }

                detail = "Unsupported file type. Select FileLocker .locked files or FileLocker PNG payloads.";
                return false;
            }
            catch (Exception ex)
            {
                detail = GetFriendlyExceptionMessage(ex, "Unable to inspect encrypted file.");
                return false;
            }
        }

        private void DecryptPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            RefreshDecryptFilesState();
        }

        private async void DecryptOutputBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            _isSyncingDecryptFilesUi = true;
            try
            {
                DecryptSaveNextToEncryptedToggle.IsOn = false;
                DecryptOutputLocationBox.Text = folder.Path;
            }
            finally
            {
                _isSyncingDecryptFilesUi = false;
            }

            UpdateDecryptDestinationUi();
            SetStatus($"Decrypt output folder selected: {folder.Name}");
        }

        private void DecryptOutputLocationBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady || _isSyncingDecryptFilesUi)
            {
                return;
            }

            if (!DecryptSaveNextToEncryptedToggle.IsOn)
            {
                _preferences.CustomDecryptOutputDirectory = DecryptOutputLocationBox.Text?.Trim() ?? string.Empty;
                PersistPreferences();
            }

            RefreshDecryptFilesState();
        }

        private void DecryptSaveNextToEncryptedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _isSyncingDecryptFilesUi)
            {
                return;
            }

            if (!DecryptSaveNextToEncryptedToggle.IsOn && string.IsNullOrWhiteSpace(DecryptOutputLocationBox.Text))
            {
                DecryptOutputLocationBox.Text = GetDefaultDecryptOutputFolder();
            }

            UpdateDecryptDestinationUi();
        }

        private void DecryptOptionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _isSyncingDecryptFilesUi)
            {
                return;
            }

            RefreshDecryptFilesState();
        }

        private void UpdateDecryptDestinationUi(bool persistPreferences = true)
        {
            bool saveNextToEncrypted = DecryptSaveNextToEncryptedToggle.IsOn;
            DecryptOutputLocationBox.IsEnabled = !saveNextToEncrypted && !_isProcessing;
            DecryptOutputBrowseButton.IsEnabled = !saveNextToEncrypted && !_isProcessing;

            if (persistPreferences)
            {
                _preferences.UseCustomDecryptOutputDirectory = !saveNextToEncrypted;
                if (!saveNextToEncrypted)
                {
                    _preferences.CustomDecryptOutputDirectory = DecryptOutputLocationBox.Text?.Trim() ?? string.Empty;
                }

                PersistPreferences();
            }

            RefreshDecryptFilesState();
        }

        private void DecryptSelectedFileRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current decryption run to finish before removing selected files.");
                return;
            }

            if ((sender as FrameworkElement)?.Tag is not DecryptSelectedItemViewModel item)
            {
                return;
            }

            DecryptSelectedFiles.Remove(item);
            _decryptQueuedPaths.Remove(item.FullPath);
            RefreshDecryptFilesState();
        }

        private void DecryptClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current decryption run to finish before clearing the selection.");
                return;
            }

            DecryptSelectedFiles.Clear();
            _decryptQueuedPaths.Clear();
            DecryptPasswordBox.Password = string.Empty;
            HideDecryptInfoBar();
            RefreshDecryptFilesState();
            SetStatus("Decrypt selection cleared.");
        }

        private async void StartDecryptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateDecryptFilesForm(showDialog: true))
            {
                RefreshDecryptFilesState();
                return;
            }

            if (!await EnsureDecryptOutputDestinationAsync())
            {
                RefreshDecryptFilesState();
                return;
            }

            if (DecryptDeleteEncryptedAfterSuccessToggle.IsOn)
            {
                int decryptableCount = DecryptSelectedFiles.Count(item => item.CanDecrypt);
                if (!await ConfirmSourceDeletionAsync(ProcessingIntent.Decrypt, decryptableCount))
                {
                    SetStatus("Decryption cancelled before deleting encrypted sources was confirmed.");
                    RefreshDecryptFilesState();
                    return;
                }
            }

            await ProcessDecryptFilesAsync();
        }

        private bool ValidateDecryptFilesForm(bool showDialog)
        {
            string? message = null;
            if (DecryptSelectedFiles.Count == 0)
            {
                message = "Please select FileLocker encrypted files to decrypt.";
            }
            else if (DecryptSelectedFiles.Any(item => !item.IsSupportedEncryptedFile))
            {
                message = "Remove unsupported files before starting decryption.";
            }
            else if (string.IsNullOrWhiteSpace(DecryptPasswordBox.Password))
            {
                message = "Please enter the password used when these files were encrypted.";
            }
            else if (!IsDecryptOutputValid())
            {
                message = "Choose a valid output folder or save output next to encrypted files.";
            }

            if (message == null)
            {
                return true;
            }

            if (showDialog)
            {
                _ = ShowErrorDialogAsync(message);
            }

            return false;
        }

        private async Task<bool> EnsureDecryptOutputDestinationAsync()
        {
            if (DecryptSaveNextToEncryptedToggle.IsOn)
            {
                return true;
            }

            string outputPath = DecryptOutputLocationBox.Text?.Trim() ?? string.Empty;
            try
            {
                Directory.CreateDirectory(outputPath);
                return true;
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(GetFriendlyExceptionMessage(ex, "Unable to prepare the decryption output folder."));
                return false;
            }
        }

        private async Task ProcessDecryptFilesAsync()
        {
            ProcessingRunOptions? runOptions = null;
            try
            {
                _isProcessing = true;
                SetUIEnabled(false);
                HideDecryptInfoBar();
                RefreshDecryptFilesState();
                _processingCancellation = new CancellationTokenSource();
                CancelRunButton.IsEnabled = true;

                string password = DecryptPasswordBox.Password;
                runOptions = CaptureDecryptProcessingRunOptions();
                List<DecryptSelectedItemViewModel> workItems = DecryptSelectedFiles
                    .Where(item => item.CanDecrypt)
                    .ToList();

                int processed = 0;
                bool cancelled = false;
                var results = new List<FileOperationResult>();

                for (int index = 0; index < workItems.Count; index++)
                {
                    DecryptSelectedItemViewModel item = workItems[index];
                    if (_processingCancellation.IsCancellationRequested)
                    {
                        cancelled = true;
                        foreach (DecryptSelectedItemViewModel pending in workItems.Skip(index))
                        {
                            pending.SetFailed("Cancelled before this file started.");
                        }

                        break;
                    }

                    string? relativeOutputDirectory = GetDecryptRelativeOutputDirectory(item);

                    var itemElapsed = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        if (IsPayloadV3File(item.FullPath) &&
                            !await ConfirmFolderPackageRestoreAsync(item.FullPath, password, runOptions, relativeOutputDirectory))
                        {
                            item.SetFailed("Restore preview was cancelled.");
                            continue;
                        }

                        item.SetProcessing();
                        FileOperationResult result = await Task.Run(() => DecryptFileAdvanced(
                            item.FullPath,
                            password,
                            runOptions,
                            (percent, status) =>
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    item.UpdateProgress(percent, status);
                                    RefreshDecryptFilesState();
                                });
                            },
                            relativeOutputDirectory));

                        result.ElapsedMilliseconds ??= itemElapsed.ElapsedMilliseconds;
                        results.Add(result);
                        processed++;
                        item.SetCompleted(result.OutputPath == null
                            ? result.Message ?? "Decryption completed."
                            : $"Output written to {result.OutputPath}");
                        SetStatus($"Decrypted {processed}/{workItems.Count} item(s)...");
                    }
                    catch (Exception ex)
                    {
                        string failureMessage = GetFriendlyExceptionMessage(ex, "Unknown error while decrypting.");
                        results.Add(new FileOperationResult
                        {
                            SourcePath = item.FullPath,
                            Status = "Failed",
                            Message = failureMessage,
                            OriginalRetained = true,
                            OutputVerified = false,
                            FailureCategory = OperationFailureClassifier.Classify(ex),
                            ElapsedMilliseconds = itemElapsed.ElapsedMilliseconds
                        });
                        item.SetFailed(failureMessage);
                    }
                }

                if (results.Count > 0)
                {
                    AppendHistory("Decrypt", runOptions, results, cancelled);
                }

                int failedCount = results.Count(result => string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase));
                if (failedCount > 0 || cancelled)
                {
                    string message = cancelled
                        ? $"Decryption stopped. {failedCount} file(s) failed or need attention."
                        : $"{failedCount} file(s) need attention. Check the selected files list.";
                    ShowDecryptInfoBar(message, InfoBarSeverity.Warning);
                    SetStatus(message);
                }
                else
                {
                    string message = $"{processed} encrypted file(s) decrypted successfully.";
                    ShowDecryptInfoBar(message, InfoBarSeverity.Success);
                    SetStatus(message);
                    DecryptPasswordBox.Password = string.Empty;
                }

                RefreshDashboardData();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(GetFriendlyExceptionMessage(ex, "Error while decrypting files."));
            }
            finally
            {
                if (runOptions?.KeyfileBytes is { Length: > 0 } keyfileBytes)
                {
                    CryptographicOperations.ZeroMemory(keyfileBytes);
                }

                _isProcessing = false;
                _processingCancellation?.Dispose();
                _processingCancellation = null;
                CancelRunButton.IsEnabled = false;
                SetUIEnabled(true);
                RefreshDecryptFilesState();
            }
        }

        private ProcessingRunOptions CaptureDecryptProcessingRunOptions()
        {
            string timestampPolicy = string.IsNullOrWhiteSpace(_preferences.OutputTimestampPolicy)
                ? "Current time"
                : _preferences.OutputTimestampPolicy;

            return new ProcessingRunOptions(
                CompressFiles: false,
                ScrambleNames: false,
                UseSteganography: false,
                Algorithm: "AES-GCM",
                Mode: "Decrypt",
                KeySizeBits: 256,
                RemoveOriginalsAfterSuccess: DecryptDeleteEncryptedAfterSuccessToggle.IsOn,
                SecureDeleteOriginals: false,
                VerifyAfterWrite: true,
                UseCustomEncryptOutputDirectory: false,
                EncryptOutputDirectory: string.Empty,
                UseCustomDecryptOutputDirectory: !DecryptSaveNextToEncryptedToggle.IsOn,
                DecryptOutputDirectory: DecryptOutputLocationBox.Text?.Trim() ?? string.Empty,
                RestoreOriginalFilenames: DecryptRestoreOriginalFilenamesToggle.IsOn,
                PreserveFolderStructure: DecryptPreserveFolderStructureToggle.IsOn,
                PackageFolders: false,
                OutputTimestampPolicy: timestampPolicy,
                BackupFolderPath: string.Empty,
                KeyfilePath: null,
                KeyfileBytes: null,
                RecoveryKey: null,
                ProfileName: "Decrypt Files",
                Metadata: new MetadataOverridesSnapshot(string.Empty, string.Empty, false, string.Empty, string.Empty));
        }

        private string? GetDecryptRelativeOutputDirectory(DecryptSelectedItemViewModel item)
        {
            if (!item.SourceRootIsFolder || !DecryptPreserveFolderStructureToggle.IsOn)
            {
                return null;
            }

            try
            {
                string? itemDirectory = Path.GetDirectoryName(item.FullPath);
                if (string.IsNullOrWhiteSpace(itemDirectory))
                {
                    return null;
                }

                string relative = Path.GetRelativePath(item.SourceRootPath, itemDirectory);
                return string.Equals(relative, ".", StringComparison.Ordinal)
                    ? null
                    : relative;
            }
            catch
            {
                return null;
            }
        }

        private void ShowDecryptInfoBar(string message, InfoBarSeverity severity)
        {
            DecryptStatusInfoBar.Severity = severity;
            DecryptStatusInfoBar.Message = message;
            DecryptStatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
        }

        private void HideDecryptInfoBar()
        {
            DecryptStatusInfoBar.Message = string.Empty;
            DecryptStatusInfoBar.IsOpen = false;
        }

        private async void DecryptionGuideHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowInfoDialogAsync(
                "Add FileLocker .locked files or FileLocker PNG payloads, enter the original password, choose where restored files should be written, then start decryption.\n\nWrong passwords and corrupted payloads fail safely without deleting the encrypted source.",
                "Decryption Guide");
        }

        private async void DecryptHistoryHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            List<OperationHistoryEntry> decryptHistory = _operationHistory
                .Where(entry => string.Equals(entry.Operation, "Decrypt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(6)
                .ToList();

            if (decryptHistory.Count == 0)
            {
                await ShowInfoDialogAsync("No local decrypt history has been recorded yet.", "Decrypt History");
                return;
            }

            var builder = new StringBuilder();
            foreach (OperationHistoryEntry entry in decryptHistory)
            {
                builder.AppendLine($"{FormatDashboardTimestamp(entry.TimestampUtc)} - {entry.SuccessCount} completed, {entry.FailureCount} failed");
                foreach (FileOperationResult result in entry.Results.Take(3))
                {
                    builder.AppendLine($"  {Path.GetFileName(result.SourcePath)}: {result.Status}");
                }

                builder.AppendLine();
            }

            await ShowInfoDialogAsync(builder.ToString().Trim(), "Decrypt History");
        }

        private static string GetDefaultDecryptOutputFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "FileLocker", "Decrypted");
        }
    }
}

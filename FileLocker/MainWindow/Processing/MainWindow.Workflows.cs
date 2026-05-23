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
        // --- Drag & Drop ---
        private void DropPanel_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                AnimateDropPanel(true);
                DropLabel.Text = ActiveDropLabelText;
                DropHintText.Text = $"Encrypt will write to {BuildRunSummaryOutputLocation()} and {(RemoveOriginalsToggle.IsOn ? "remove originals after success." : "keep originals.")}";
                DropLabel.FontWeight = FontWeights.Bold;
            }
        }

        private async void DropPanel_Drop(object sender, DragEventArgs e)
        {
            AnimateDropPanel(false);
            DropLabel.Text = DefaultDropLabelText;
            DropLabel.FontWeight = FontWeights.SemiBold;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    await ProcessDroppedFilesAsync(e.DataView);
                }
                finally
                {
                    deferral.Complete();
                }
            }

            UpdateDropHint();
        }

        private async Task ProcessDroppedFilesAsync(DataPackageView dataView)
        {
            try
            {
                var items = await dataView.GetStorageItemsAsync();
                var files = new List<string>();

                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        files.Add(file.Path);
                    }
                    else if (item is StorageFolder folder)
                    {
                        files.Add(folder.Path);
                    }
                }

                if (files.Count > 0)
                {
                    AddFilesToList([.. files]);
                    SetStatus($"Added {files.Count} file(s)");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Error processing dropped files: {GetFriendlyExceptionMessage(ex, "Drop failed.")}");
            }
        }

        private async Task BrowseFiles()
        {
            try
            {
                FileOpenPicker picker = CreateOpenFilePicker(PickerLocationId.DocumentsLibrary, "*");

                var files = await picker.PickMultipleFilesAsync();
                if (files.Count > 0)
                {
                    var filePaths = files.Select(f => f.Path).ToArray();
                    AddFilesToList(filePaths);
                    SetStatus($"Added {filePaths.Length} file(s)");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to browse files: {GetFriendlyExceptionMessage(ex, "File picker failed.")}");
            }
        }

        private async void BrowseFiles_Click(object sender, RoutedEventArgs e)
        {
            await BrowseFiles();
        }

        private async Task BrowseFolder()
        {
            try
            {
                FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    AddFilesToList([folder.Path]);
                    SetStatus($"Added folder: {folder.Name}");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to browse folders: {GetFriendlyExceptionMessage(ex, "Folder picker failed.")}");
            }
        }

        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await BrowseFolder();
        }

        // --- File List and Status Binding ---
        private void AddFilesToList(string[] paths)
        {
            int previousCount = FileList.Count;
            bool folderSelectionAdded = paths.Any(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path.Trim()));
            QueueExpandResult expansion = ExpandQueuePaths(paths);
            int addedCount = 0;
            int duplicateCount = 0;

            foreach (ExpandedQueueFile expandedFile in expansion.Files)
            {
                if (_queuedPaths.Add(expandedFile.Path))
                {
                    FileList.Add(new QueuedFileItem(
                        expandedFile.Path,
                        expandedFile.RootPath,
                        expandedFile.RootIsFolder,
                        expandedFile.SizeBytes));
                    addedCount++;
                }
                else
                {
                    duplicateCount++;
                }
            }

            string? adoptedEncryptOutputDirectory = MaybeAdoptSuggestedEncryptOutputDirectory(folderSelectionAdded, previousCount);
            RefreshQueueSummary();
            RefreshPreflightPreview();
            UpdateStatusLabel();
            UpdateDropHint();
            UpdatePayloadInspectorPreview();
            RefreshEncryptFilesState();

            if (expansion.Warnings.Count > 0)
            {
                string warningSummary = expansion.Warnings.Count == 1
                    ? expansion.Warnings[0]
                    : $"{expansion.Warnings.Count} folder item(s) could not be fully read. Open queue details or preflight for specifics.";
                ShowBatchInfoBar(warningSummary, InfoBarSeverity.Warning);
            }
            else if (addedCount > 0)
            {
                HideBatchInfoBar();
            }

            if (addedCount > 0 || duplicateCount > 0 || expansion.Warnings.Count > 0)
            {
                string summary = duplicateCount > 0
                    ? $"Added {addedCount} item(s). Skipped {duplicateCount} duplicate(s)."
                    : $"Added {addedCount} item(s).";

                if (expansion.Warnings.Count > 0)
                {
                    summary += $" {expansion.Warnings.Count} warning(s) were captured while scanning folders.";
                }

                if (!string.IsNullOrWhiteSpace(adoptedEncryptOutputDirectory))
                {
                    summary += $" Output folder set to {Path.GetFileName(adoptedEncryptOutputDirectory)} to keep locked copies out of the source tree.";
                }

                SetStatus(summary);
            }
        }

        private QueueExpandResult ExpandQueuePaths(IEnumerable<string> paths)
        {
            var result = new QueueExpandResult();

            foreach (string rawPath in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                string path = rawPath.Trim();
                if (File.Exists(path))
                {
                    try
                    {
                        var fileInfo = new FileInfo(path);
                        result.Files.Add(new ExpandedQueueFile(path, path, false, fileInfo.Length));
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Unable to read {Path.GetFileName(path)}: {GetFriendlyExceptionMessage(ex, "File scan failed.")}");
                    }

                    continue;
                }

                if (!Directory.Exists(path))
                {
                    result.Warnings.Add($"Skipped missing path: {path}");
                    continue;
                }

                foreach (ExpandedQueueFile expanded in EnumerateFolderFiles(path, result.Warnings))
                {
                    result.Files.Add(expanded);
                }
            }

            return result;
        }

        private static IEnumerable<ExpandedQueueFile> EnumerateFolderFiles(string rootFolderPath, ICollection<string> warnings)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootFolderPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectory = pendingDirectories.Pop();
                if (IsReparsePointDirectory(currentDirectory, warnings))
                {
                    continue;
                }

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(currentDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Unable to enumerate folders inside {currentDirectory}: {GetFriendlyExceptionMessage(ex, "Folder scan failed.")}");
                    continue;
                }

                foreach (string childDirectory in childDirectories)
                {
                    if (IsReparsePointDirectory(childDirectory, warnings))
                    {
                        continue;
                    }

                    pendingDirectories.Push(childDirectory);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Unable to enumerate files inside {currentDirectory}: {GetFriendlyExceptionMessage(ex, "File scan failed.")}");
                    continue;
                }

                foreach (string file in files)
                {
                    FileInfo fileInfo;
                    try
                    {
                        fileInfo = new FileInfo(file);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Unable to inspect {file}: {GetFriendlyExceptionMessage(ex, "File inspection failed.")}");
                        continue;
                    }

                    yield return new ExpandedQueueFile(file, rootFolderPath, true, fileInfo.Length);
                }
            }
        }

        private static bool IsReparsePointDirectory(string directoryPath, ICollection<string> warnings)
        {
            try
            {
                if ((File.GetAttributes(directoryPath) & System.IO.FileAttributes.ReparsePoint) == System.IO.FileAttributes.ReparsePoint)
                {
                    warnings.Add($"Skipped reparse-point folder: {directoryPath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Unable to inspect folder attributes for {directoryPath}: {GetFriendlyExceptionMessage(ex, "Folder inspection failed.")}");
            }

            return false;
        }

        private void ShowBatchInfoBar(string message, InfoBarSeverity severity)
        {
            BatchStatusInfoBar.Severity = severity;
            BatchStatusInfoBar.Message = message;
            BatchStatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
        }

        private void HideBatchInfoBar()
        {
            BatchStatusInfoBar.IsOpen = false;
            BatchStatusInfoBar.Message = string.Empty;
        }

        private QueuedFileItem? FindQueueItem(string path)
        {
            return FileList.FirstOrDefault(item =>
                string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        }

        private void SetQueueItemStatus(string path, string status, string? detail = null)
        {
            QueuedFileItem? item = FindQueueItem(path);
            if (item == null)
            {
                return;
            }

            switch (status)
            {
                case "Queued":
                    item.SetQueued(detail ?? "Ready to process.");
                    break;
                case "Processing":
                    item.SetProcessing();
                    break;
                case "Completed":
                    item.SetCompleted(detail ?? "Completed successfully.");
                    break;
                case "Verified":
                    item.SetVerified(detail ?? "Verified without writing output.");
                    break;
                case "Cancelled":
                    item.SetCancelled(detail ?? "Run cancelled before this file completed.");
                    break;
                default:
                    item.SetNeedsAttention(detail ?? status);
                    break;
            }

            RefreshDashboardStats();
            RefreshEncryptFilesState();
        }

        private void RefreshQueueSummary()
        {
            int fileCount = FileList.Count;
            int rootCount = FileList
                .Select(item => item.SourceRootPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int folderRootCount = FileList
                .Where(item => item.SourceRootIsFolder)
                .Select(item => item.SourceRootPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            long totalSize = FileList.Sum(item => item.SizeBytes);

            QueueSummaryText.Text = fileCount == 0
                ? "Drop files above or browse to begin."
                : $"{fileCount} file(s) queued from {rootCount} selection(s) • {folderRootCount} folder root(s)";

            QueueFileCountMetricText.Text = fileCount.ToString(CultureInfo.InvariantCulture);
            QueueRootCountMetricText.Text = rootCount.ToString(CultureInfo.InvariantCulture);
            QueueSizeMetricText.Text = FormatFileSize(totalSize);
            QueueEmptyStatePanel.Visibility = fileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueListPanel.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            ClearListButton.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateHeaderContext();
            RefreshDashboardStats();
            RefreshEncryptFilesState();
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private static string BuildCompressionRunSummary(OperationMetricsSummary metrics)
        {
            var parts = new List<string>
            {
                $"{metrics.CompressionAppliedCount}/{metrics.CompressionRequestedCount} file(s) compressed"
            };

            if (metrics.CompressionSkippedCount > 0)
            {
                parts.Add($"{metrics.CompressionSkippedCount} skipped");
            }

            if (metrics.TotalStorageSavedBytes is > 0)
            {
                parts.Add($"compression saved {FormatFileSize(metrics.TotalStorageSavedBytes.Value)}");
            }

            if (metrics.TotalStorageAddedBytes is > 0)
            {
                parts.Add($"compression increased payloads by {FormatFileSize(metrics.TotalStorageAddedBytes.Value)}");
            }

            return $"Encryption complete. Compression summary: {string.Join(", ", parts)}.";
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            FileList.Clear();
            _queuedPaths.Clear();
            HideBatchInfoBar();
            RefreshQueueSummary();
            RefreshPreflightPreview();
            UpdateStatusLabel();
            UpdateDropHint();
            UpdatePayloadInspectorPreview();
        }

        private void SetStatus(string text)
        {
            StatusText = text;
            StatusLabel.Text = text;
        }

        private void UpdateStatusLabel()
        {
            if (FileList.Count == 0)
                SetStatus("Queue is empty. Add files or folders to begin.");
            else
                SetStatus($"Queue ready. Review the destination and choose an action for {FileList.Count} item(s).");
        }

        private async void QueueItemDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not QueuedFileItem item)
            {
                return;
            }

            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 620
            };

            panel.Children.Add(CreateQueueDetailBlock("Source", item.SourcePath));
            panel.Children.Add(CreateQueueDetailBlock("Added from", item.RootSelectionCaption));
            panel.Children.Add(CreateQueueDetailBlock("Status", item.Status));
            panel.Children.Add(CreateQueueDetailBlock("Issue", string.IsNullOrWhiteSpace(item.DetailSummary) ? "No detail available yet." : item.DetailSummary));
            panel.Children.Add(CreateQueueDetailBlock("Predicted output", string.IsNullOrWhiteSpace(item.PredictedOutputPath) ? "Pending preflight" : item.PredictedOutputPath));

            await ShowInfoDialogAsync(panel, "Queue Item Details");
        }

        private static StackPanel CreateQueueDetailBlock(string label, string value)
        {
            var block = new StackPanel
            {
                Spacing = 4
            };

            block.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold
            });

            block.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.WrapWholeWords,
                IsTextSelectionEnabled = true
            });

            return block;
        }

        private void QueueItemOpenLocationButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not QueuedFileItem item)
            {
                return;
            }

            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.SourcePath}\"",
                    UseShellExecute = true
                });

                if (process == null)
                {
                    throw new InvalidOperationException("Windows did not open Explorer.");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Unable to open file location: {GetFriendlyExceptionMessage(ex, "Explorer could not open the location.")}");
            }
        }

        private void QueueItemRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current run to finish before removing queued files.");
                return;
            }

            if ((sender as FrameworkElement)?.Tag is not QueuedFileItem item)
            {
                return;
            }

            FileList.Remove(item);
            _queuedPaths.Remove(item.SourcePath);
            RefreshQueueSummary();
            RefreshPreflightPreview();
            UpdateStatusLabel();
        }

        private void QueueItemRetryButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not QueuedFileItem item)
            {
                return;
            }

            item.SetQueued("Ready to retry.");
            RefreshPreflightPreview();
            SetStatus($"Requeued {Path.GetFileName(item.SourcePath)}.");
        }

        private static bool ShouldProcessQueueItem(QueuedFileItem item, ProcessingIntent intent)
        {
            return intent switch
            {
                ProcessingIntent.Verify => !string.Equals(item.Status, "Verified", StringComparison.OrdinalIgnoreCase),
                _ => !string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase)
            };
        }

        private List<ProcessingWorkItem> BuildProcessingWorkItems(ProcessingIntent intent, ProcessingRunOptions options)
        {
            List<QueuedFileItem> queueItems = FileList
                .Where(item => ShouldProcessQueueItem(item, intent))
                .ToList();

            if (intent != ProcessingIntent.Encrypt || !options.PackageFolders)
            {
                return queueItems.Select(item => new ProcessingWorkItem
                {
                    PrimaryPath = item.SourcePath,
                    QueueItems = [item],
                    EncryptAsFolderPackage = false,
                    FolderRootPath = item.SourceRootIsFolder ? item.SourceRootPath : null
                }).ToList();
            }

            var workItems = new List<ProcessingWorkItem>();
            var groupedFolderRoots = queueItems
                .Where(item => item.SourceRootIsFolder)
                .GroupBy(item => item.SourceRootPath, StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, QueuedFileItem> group in groupedFolderRoots)
            {
                workItems.Add(new ProcessingWorkItem
                {
                    PrimaryPath = group.Key,
                    QueueItems = group.ToList(),
                    EncryptAsFolderPackage = true,
                    FolderRootPath = group.Key
                });
            }

            foreach (QueuedFileItem fileItem in queueItems.Where(item => !item.SourceRootIsFolder))
            {
                workItems.Add(new ProcessingWorkItem
                {
                    PrimaryPath = fileItem.SourcePath,
                    QueueItems = [fileItem],
                    EncryptAsFolderPackage = false,
                    FolderRootPath = null
                });
            }

            return workItems;
        }

        private void SetWorkItemStatus(ProcessingWorkItem workItem, string status, string? detail = null)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetWorkItemStatus(workItem, status, detail));
                return;
            }

            foreach (QueuedFileItem queueItem in workItem.QueueItems)
            {
                SetQueueItemStatus(queueItem.SourcePath, status, detail);
            }
        }

        private void UpdateWorkItemProgress(ProcessingWorkItem workItem, double percent, string? status = null)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => UpdateWorkItemProgress(workItem, percent, status));
                return;
            }

            foreach (QueuedFileItem queueItem in workItem.QueueItems)
            {
                queueItem.UpdateProgress(percent, status);
            }
        }

        private void OperationModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlgorithmHintText == null) return;
            if (_isUpdatingModeOptions) return;
            ConfigureModeOptions();
            RefreshPreflightPreview();
        }

        private void AlgorithmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlgorithmHintText == null) return;
            UpdateKeySizeInteractivity();
            UpdateAlgorithmHelper();
            RefreshPreflightPreview();
        }

        private void KeySizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlgorithmHintText == null) return;
            UpdateAlgorithmHelper();
            RefreshPreflightPreview();
        }

        private void RecommendedModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetComboSelection(OperationModeCombo, "Encrypt / Decrypt");
            if (_isUpdatingModeOptions) return;
            ConfigureModeOptions();
            SetComboSelection(AlgorithmCombo, "AES-GCM");
            SetComboSelection(KeySizeCombo, "256");
            RemoveOriginalsToggle.IsOn = false;
            SecureDeleteOriginalsToggle.IsOn = false;
            VerifyAfterWriteToggle.IsOn = true;
            UpdateAlgorithmHelper();
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            SetStatus("Recommended mode applied: AES-256-GCM");
        }

        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingProfile || !_isUiReady)
            {
                return;
            }

            ApplySelectedProfile();
        }

        private void InspectorTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            string view = InspectorTabView.SelectedIndex switch
            {
                1 => "Checks",
                2 => "Jobs",
                _ => "Setup"
            };

            InspectorSetupPanel.Visibility = view == "Setup" ? Visibility.Visible : Visibility.Collapsed;
            InspectorChecksPanel.Visibility = view == "Checks" ? Visibility.Visible : Visibility.Collapsed;
            InspectorJobsPanel.Visibility = view == "Jobs" ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ExperienceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _isApplyingExperienceMode)
            {
                return;
            }

            UserExperienceLevel requestedLevel = ParseExperienceLevel((ExperienceModeCombo.SelectedItem as ComboBoxItem)?.Content as string);
            if (requestedLevel == _currentExperienceLevel)
            {
                return;
            }

            if (requestedLevel == UserExperienceLevel.Advanced)
            {
                bool proceed = await ShowConfirmDialogAsync(
                    "Advanced mode exposes settings that can affect security and data recovery.\n\nContinue into Advanced mode?",
                    "Advanced Mode Warning");
                if (!proceed)
                {
                    ApplyExperienceMode(_currentExperienceLevel, persist: false, showAdvancedWarning: false);
                    return;
                }
            }

            ApplyExperienceMode(requestedLevel, persist: true, showAdvancedWarning: requestedLevel == UserExperienceLevel.Advanced);
            SetStatus($"Experience level set to {GetExperienceLevelDisplay(requestedLevel)}.");
        }

        private async void ExperienceLevelButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string levelText)
            {
                return;
            }

            UserExperienceLevel requestedLevel = ParseExperienceLevel(levelText);
            if (requestedLevel == UserExperienceLevel.Advanced)
            {
                bool proceed = await ShowConfirmDialogAsync(
                    "Advanced mode exposes settings that can affect security and data recovery.\n\nYou can change this later in Settings.",
                    "Advanced Mode Warning");
                if (!proceed)
                {
                    return;
                }
            }

            ApplyExperienceMode(requestedLevel, persist: true, showAdvancedWarning: requestedLevel == UserExperienceLevel.Advanced);
            SetStatus($"Welcome to {GetExperienceLevelDisplay(requestedLevel)} mode.");
        }

        private void ApplySelectedProfile()
        {
            var profile = FindProfile(ProfileCombo.SelectedItem as string);
            if (profile == null)
            {
                return;
            }

            _isApplyingProfile = true;
            try
            {
                SetComboSelection(OperationModeCombo, "Encrypt / Decrypt");
                ConfigureModeOptions();
                SetComboSelection(AlgorithmCombo, profile.Algorithm);
                SetComboSelection(KeySizeCombo, profile.KeySizeBits.ToString(CultureInfo.InvariantCulture));
                CompressModeToggle.IsOn = profile.CompressFiles;
                ScrambleNamesToggle.IsOn = profile.ScrambleNames;
                SteganographyToggle.IsOn = profile.UseSteganography;
                MetadataRandomizeToggle.IsOn = profile.RandomizeMetadata;
                RemoveOriginalsToggle.IsOn = profile.RemoveOriginalsAfterSuccess;
                SecureDeleteOriginalsToggle.IsOn = profile.SecureDeleteOriginals;
                VerifyAfterWriteToggle.IsOn = profile.VerifyAfterWrite;
                BackupFolderBox.Text = profile.BackupFolderPath;
                KeyfilePathBox.Text = profile.KeyfilePath;
            }
            finally
            {
                _isApplyingProfile = false;
            }

            UpdateAlgorithmHelper();
            UpdateProfilePresentation(profile);
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
            SetStatus($"Profile applied: {profile.Name}");
        }

        private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            string? profileName = await PromptForTextAsync(
                "Save Profile",
                "Enter a name for this profile:",
                ProfileCombo.SelectedItem as string ?? "Custom Profile");

            if (string.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            profileName = NormalizeProfileName(profileName);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return;
            }

            _customProfiles.RemoveAll(profile => string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
            _customProfiles.Add(new EncryptionProfile
            {
                Name = profileName,
                Description = $"Custom profile saved on {DateTime.Now:g}",
                Algorithm = GetComboContent(AlgorithmCombo) ?? "AES-GCM",
                KeySizeBits = ParseKeySizeSelection(),
                CompressFiles = CompressModeToggle.IsOn,
                ScrambleNames = ScrambleNamesToggle.IsOn,
                UseSteganography = SteganographyToggle.IsOn,
                RandomizeMetadata = MetadataRandomizeToggle.IsOn,
                RemoveOriginalsAfterSuccess = RemoveOriginalsToggle.IsOn,
                SecureDeleteOriginals = SecureDeleteOriginalsToggle.IsOn,
                VerifyAfterWrite = VerifyAfterWriteToggle.IsOn,
                BackupFolderPath = BackupFolderBox.Text?.Trim() ?? string.Empty,
                KeyfilePath = KeyfilePathBox.Text?.Trim() ?? string.Empty,
                IsBuiltIn = false
            });

            try
            {
                await SaveProfilesAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to save the profile: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
                SetStatus($"Profile was not saved: {profileName}");
                return;
            }

            RefreshProfileCombo();
            ProfileCombo.SelectedItem = profileName;
            UpdateProfilePresentation(FindProfile(profileName));
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            SetStatus($"Saved profile: {profileName}");
        }

        private static string NormalizeProfileName(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(profileName.Length);
            bool pendingWhitespace = false;
            foreach (char character in profileName.Trim())
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                {
                    pendingWhitespace = true;
                    continue;
                }

                if (pendingWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(character);
                pendingWhitespace = false;
            }

            string normalized = builder.ToString().Trim();
            return normalized.Length > 80 ? normalized[..80] : normalized;
        }

        private void ConfigureModeOptions()
        {
            _isUpdatingModeOptions = true;
            try
            {
                bool isHashMode = (GetComboContent(OperationModeCombo) ?? string.Empty)
                    .Contains("Hash", StringComparison.OrdinalIgnoreCase);
                string? currentAlgorithm = GetComboContent(AlgorithmCombo);

                PopulateComboWithValues(
                    AlgorithmCombo,
                    isHashMode ? HashAlgorithms : EncryptionAlgorithms,
                    string.IsNullOrWhiteSpace(currentAlgorithm)
                        ? isHashMode ? "SHA-256" : "AES-GCM"
                        : currentAlgorithm);

                PopulateKeySizes(isHashMode);

                HashHelperPanel.Visibility = isHashMode ? Visibility.Visible : Visibility.Collapsed;
                EncryptDestinationPanel.Visibility = isHashMode ? Visibility.Collapsed : Visibility.Visible;
                MetadataHelperText.Text = isHashMode
                    ? "Hash mode only uses the helper panel and does not write encrypted files."
                    : "Metadata will be preserved and can be randomized if desired.";

                UpdateKeySizeInteractivity();
                UpdateAlgorithmHelper();
                UpdateRunSummaryBanner();
                UpdateStatusLabel();
            }
            finally
            {
                _isUpdatingModeOptions = false;
            }
        }
        private static void PopulateComboWithValues(ComboBox comboBox, IEnumerable<string> values, string? preferredSelection)
        {
            comboBox.Items.Clear();
            int selectedIndex = 0;
            int index = 0;

            foreach (string value in values)
            {
                comboBox.Items.Add(new ComboBoxItem { Content = value });
                if (!string.IsNullOrWhiteSpace(preferredSelection) &&
                    string.Equals(value, preferredSelection, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index;
                }
                index++;
            }

            comboBox.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, comboBox.Items.Count - 1));
        }

        private void PopulateKeySizes(bool isHashMode)
        {
            KeySizeCombo.Items.Clear();
            int[] sizes = isHashMode ? HashKeySizes : EncryptionKeySizes;
            for (int i = 0; i < sizes.Length; i++)
            {
                KeySizeCombo.Items.Add(new ComboBoxItem { Content = sizes[i].ToString() });
            }

            KeySizeCombo.SelectedIndex = isHashMode ? 0 : 0;
        }

        private void UpdateKeySizeInteractivity()
        {
            string? mode = GetComboContent(OperationModeCombo);
            string? algorithm = GetComboContent(AlgorithmCombo);
            bool isHashMode = mode != null && mode.Contains("Hash", StringComparison.OrdinalIgnoreCase);
            bool usesKeySize = !string.Equals(algorithm, "Base64", StringComparison.OrdinalIgnoreCase);
            KeySizeCombo.IsEnabled = isHashMode && usesKeySize;
            KeySizeCombo.Opacity = KeySizeCombo.IsEnabled ? 1 : 0.6;
        }

        private void UpdateAlgorithmHelper()
        {
            string algorithm = GetComboContent(AlgorithmCombo) ?? "AES-GCM";
            int keySize = ParseKeySizeSelection();
            string mode = GetComboContent(OperationModeCombo) ?? "Encrypt / Decrypt";
            bool isHashMode = mode.Contains("Hash", StringComparison.OrdinalIgnoreCase);

            if (isHashMode)
            {
                string detail = algorithm.StartsWith("SHA", StringComparison.OrdinalIgnoreCase)
                    ? $"{algorithm} ({keySize}-bit digest)"
                    : "Base64 text helper";
                AlgorithmHintText.Text = $"Preset: {detail}";
            }
            else
            {
                AlgorithmHintText.Text = "Preset: AES-256-GCM for file encryption";
            }
        }

        // --- Password Section ---
        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var evaluation = CalculatePasswordStrength(PasswordBox.Password);
            PasswordStrengthBar.Value = evaluation.Score;
            PasswordStrengthText.Text = evaluation.Feedback;
            PasswordStrengthBar.Foreground = new SolidColorBrush(evaluation.BarColor);
            RefreshEncryptFilesState();
        }

        private static PasswordStrengthResult CalculatePasswordStrength(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return new PasswordStrengthResult(0, "Enter a password to begin.", Microsoft.UI.Colors.Gray);
            }

            int score = Math.Min(password.Length * 4, 30);
            bool hasLower = password.Any(char.IsLower);
            bool hasUpper = password.Any(char.IsUpper);
            bool hasDigit = password.Any(char.IsDigit);
            bool hasSpecial = password.Any(ch => !char.IsLetterOrDigit(ch));

            score += hasLower ? 5 : 0;
            score += hasUpper ? 5 : 0;
            score += hasDigit ? 10 : 0;
            score += hasSpecial ? 15 : 0;

            int uniqueChars = password.Distinct().Count();
            score += Math.Min(uniqueChars * 2, 10);

            if (password.Length >= 12 && hasLower && hasUpper && hasDigit && hasSpecial)
            {
                score += 15;
            }

            string lowered = password.ToLowerInvariant();
            string[] commonPasswords = ["password", "123456", "qwerty", "letmein", "welcome", "admin"];
            if (commonPasswords.Any(p => lowered.Contains(p)))
            {
                score = Math.Min(score, 20);
            }

            int finalScore = Math.Clamp(score, 0, 100);

            // New: normalize very strong passwords to 100%
            if (finalScore >= 90)
            {
                finalScore = 100;
            }

            if (finalScore < 35)
            {
                return new PasswordStrengthResult(finalScore, "Weak - use upper, lower, numbers, and symbols", Microsoft.UI.Colors.Red);
            }

            if (finalScore < 70)
            {
                return new PasswordStrengthResult(finalScore, "Fair - add more length for better security", Microsoft.UI.Colors.Orange);
            }

            return new PasswordStrengthResult(finalScore, "Strong - great mix of length and characters", Microsoft.UI.Colors.Green);
        }


        // --- Encrypt/Decrypt ---
        private async void EncryptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current operation to finish.");
                return;
            }

            if (!await ValidateInputAsync(ProcessingIntent.Encrypt)) return;
            if (!await EnsureEncryptOutputDestinationAsync()) return;
            if (RemoveOriginalsToggle.IsOn &&
                !await ConfirmSourceDeletionAsync(ProcessingIntent.Encrypt, FileList.Count))
            {
                SetStatus("Encryption cancelled before deleting originals was confirmed.");
                return;
            }

            await ProcessFilesAsync(ProcessingIntent.Encrypt);
        }

        private async void DecryptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current operation to finish.");
                return;
            }

            if (!await ValidateInputAsync(ProcessingIntent.Decrypt)) return;
            if (RemoveOriginalsToggle.IsOn &&
                !await ConfirmSourceDeletionAsync(ProcessingIntent.Decrypt, FileList.Count))
            {
                SetStatus("Decryption cancelled before deleting encrypted sources was confirmed.");
                return;
            }

            await ProcessFilesAsync(ProcessingIntent.Decrypt);
        }

        private async void InspectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current operation to finish.");
                return;
            }

            if (!await ValidateInputAsync(ProcessingIntent.Verify)) return;
            await ProcessFilesAsync(ProcessingIntent.Verify);
        }

        private async void RotateKeysButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                SetStatus("Wait for the current operation to finish.");
                return;
            }

            if (FileList.Count == 0)
            {
                await ShowErrorDialogAsync("Please queue one or more locked payload files first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(PasswordBox.Password) && string.IsNullOrWhiteSpace(RecoveryKeyBox.Text))
            {
                await ShowErrorDialogAsync("Enter the current password or recovery key before rotating payload keys.");
                return;
            }

            string? newPassword = await PromptForTextAsync(
                "Rotate Keys",
                "Enter the new password that should unlock the selected payload(s):",
                string.Empty);

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                return;
            }

            if (CalculatePasswordStrength(newPassword.Trim()).Score < StrongPasswordMinimumScore)
            {
                await ShowErrorDialogAsync("Use a stronger new password before rotating payload keys.");
                return;
            }

            bool generateNewRecoveryKey = await ShowConfirmDialogAsync(
                "Generate a fresh recovery key for the rotated payload(s)?",
                "Recovery Key");

            string? newRecoveryKey = generateNewRecoveryKey
                ? GenerateRecoveryKey()
                : string.IsNullOrWhiteSpace(RecoveryKeyBox.Text)
                    ? null
                    : RecoveryKeyBox.Text.Trim();

            string currentPassword = PasswordBox.Password;
            string currentKeyfilePath = KeyfilePathBox.Text?.Trim() ?? string.Empty;
            string? currentRecoveryKey = string.IsNullOrWhiteSpace(RecoveryKeyBox.Text)
                ? null
                : RecoveryKeyBox.Text.Trim();
            string newPasswordTrimmed = newPassword.Trim();
            byte[]? keyfileBytes = null;
            int rotatedCount = 0;
            bool rotationCancelled = false;
            try
            {
                _isProcessing = true;
                _processingCancellation = new CancellationTokenSource();
                SetUIEnabled(false);
                CancelRunButton.IsEnabled = true;
                RefreshEncryptFilesState();
                SetStatus("Rotating payload keys...");
                keyfileBytes = await Task.Run(() => ReadKeyfileBytesIfConfigured(currentKeyfilePath), _processingCancellation.Token);

                foreach (QueuedFileItem item in FileList.ToList())
                {
                    if (_processingCancellation.IsCancellationRequested)
                    {
                        rotationCancelled = true;
                        item.SetCancelled("Key rotation cancelled before this payload started.");
                        break;
                    }

                    if (!File.Exists(item.SourcePath) || !IsPayloadV3File(item.SourcePath))
                    {
                        item.SetNeedsAttention("Key rotation is available for version 3 payloads only.");
                        continue;
                    }

                    try
                    {
                        await Task.Run(() => RotatePayloadKeys(
                            item.SourcePath,
                            currentPassword,
                            currentRecoveryKey,
                            keyfileBytes,
                            newPasswordTrimmed,
                            newRecoveryKey,
                            _processingCancellation.Token),
                            _processingCancellation.Token);
                        item.SetCompleted("Key slots rotated without re-encrypting the file contents.");
                        rotatedCount++;
                    }
                    catch (OperationCanceledException) when (_processingCancellation.IsCancellationRequested)
                    {
                        rotationCancelled = true;
                        item.SetCancelled("Key rotation cancelled before this payload completed.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.SetNeedsAttention(GetFriendlyExceptionMessage(ex, "Key rotation failed."));
                    }
                }
            }
            catch (OperationCanceledException) when (_processingCancellation?.IsCancellationRequested == true)
            {
                SetStatus("Key rotation cancelled.");
                return;
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to rotate keys: {GetFriendlyExceptionMessage(ex, "Key rotation failed.")}");
                return;
            }
            finally
            {
                if (keyfileBytes is { Length: > 0 })
                {
                    CryptographicOperations.ZeroMemory(keyfileBytes);
                }

                _isProcessing = false;
                _processingCancellation?.Dispose();
                _processingCancellation = null;
                CancelRunButton.IsEnabled = false;
                SetUIEnabled(true);
                RefreshEncryptFilesState();
            }

            if (rotationCancelled)
            {
                if (generateNewRecoveryKey && !string.IsNullOrWhiteSpace(newRecoveryKey) && rotatedCount > 0)
                {
                    RecoveryKeyBox.Text = newRecoveryKey;
                    await ShowInfoDialogAsync(
                        $"Key rotation was cancelled after rotating {rotatedCount} payload(s).\n\nNew recovery key for rotated payloads:\n{newRecoveryKey}",
                        "Keys Rotated");
                }

                SetStatus($"Key rotation cancelled after rotating {rotatedCount} payload(s).");
                RefreshPreflightPreview();
                return;
            }

            if (generateNewRecoveryKey && !string.IsNullOrWhiteSpace(newRecoveryKey))
            {
                RecoveryKeyBox.Text = newRecoveryKey;
                await ShowInfoDialogAsync(
                    $"Rotated {rotatedCount} payload(s).\n\nNew recovery key:\n{newRecoveryKey}",
                    "Keys Rotated");
            }
            else
            {
                SetStatus($"Rotated keys for {rotatedCount} payload(s).");
            }

            RefreshPreflightPreview();
        }

        private async Task<bool> ValidateInputAsync(ProcessingIntent intent)
        {
            if (FileList.Count == 0)
            {
                await ShowErrorDialogAsync("Please select files to process.");
                return false;
            }

            bool hasPassword = !string.IsNullOrWhiteSpace(PasswordBox.Password);
            bool hasRecoveryKey = !string.IsNullOrWhiteSpace(RecoveryKeyBox.Text);

            if (intent == ProcessingIntent.Encrypt && !hasPassword)
            {
                await ShowErrorDialogAsync("Please enter a password.");
                return false;
            }

            if (intent is ProcessingIntent.Decrypt or ProcessingIntent.Verify && !hasPassword && !hasRecoveryKey)
            {
                await ShowErrorDialogAsync("Enter either the password or the recovery key to unlock the payload.");
                return false;
            }

            if (intent == ProcessingIntent.Encrypt && hasPassword &&
                CalculatePasswordStrength(PasswordBox.Password).Score < StrongPasswordMinimumScore)
            {
                await ShowErrorDialogAsync("Use a stronger password before encrypting. Include length, upper/lowercase letters, numbers, and symbols.");
                return false;
            }

            return true;
        }

        private async Task<bool> ConfirmPreflightAsync(ProcessingIntent intent, ProcessingRunOptions options)
        {
            var issues = BuildPreflightIssues(intent, options);
            DisplayPreflightIssues(issues);

            int errorCount = issues.Count(issue => issue.Severity == PreflightSeverity.Error);
            int warningCount = issues.Count(issue => issue.Severity == PreflightSeverity.Warning);

            if (errorCount > 0)
            {
                string details = string.Join(
                    "\n",
                    issues
                        .Where(issue => issue.Severity == PreflightSeverity.Error)
                        .Take(5)
                        .Select(issue => $"- {issue.Message}"));

                await ShowErrorDialogAsync(
                    $"Preflight found {errorCount} blocking issue(s).\n\n{details}");
                return false;
            }

            if (warningCount > 0)
            {
                string details = string.Join(
                    "\n",
                    issues
                        .Where(issue => issue.Severity == PreflightSeverity.Warning)
                        .Take(5)
                        .Select(issue => $"- {issue.Message}"));

                return await ShowConfirmDialogAsync(
                    $"Preflight found {warningCount} warning(s).\n\n{details}\n\nContinue anyway?",
                    "Preflight Warnings");
            }

            return true;
        }

        internal const long MaxKeyfileBytes = 16L * 1024L * 1024L;

        internal static byte[]? ReadKeyfileBytesIfConfigured(string? keyfilePath)
        {
            if (string.IsNullOrWhiteSpace(keyfilePath))
            {
                return null;
            }

            string trimmed = keyfilePath.Trim();
            if (!File.Exists(trimmed))
            {
                throw new FileNotFoundException("The selected keyfile could not be found.", trimmed);
            }

            var fileInfo = new FileInfo(trimmed);
            if (fileInfo.Length == 0)
            {
                throw new InvalidOperationException("The selected keyfile is empty.");
            }

            if (fileInfo.Length > MaxKeyfileBytes)
            {
                throw new InvalidOperationException($"The selected keyfile is too large. Choose a keyfile up to {MaxKeyfileBytes / 1024 / 1024} MB.");
            }

            return File.ReadAllBytes(trimmed);
        }

        private async void HashRunButton_Click(object sender, RoutedEventArgs e)
        {
            string input = HashInputBox.Text;
            if (string.IsNullOrWhiteSpace(input))
            {
                await ShowErrorDialogAsync("Enter text to hash or encode.");
                return;
            }

            string algorithm = GetComboContent(AlgorithmCombo) ?? "AES-GCM";
            int keySize = ParseKeySizeSelection();
            string password = PasswordBox.Password;
            string keyfilePath = KeyfilePathBox.Text;

            try
            {
                string output = await Task.Run(() =>
                {
                    byte[]? keyfileBytes = ReadKeyfileBytesIfConfigured(keyfilePath);
                    try
                    {
                        return RunHashOrEncode(input, algorithm, keySize, password, keyfileBytes);
                    }
                    finally
                    {
                        if (keyfileBytes is { Length: > 0 })
                        {
                            CryptographicOperations.ZeroMemory(keyfileBytes);
                        }
                    }
                });
                HashOutputBox.Text = output;
                SetStatus($"Generated output using {algorithm} ({keySize}-bit)");
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Failed to generate output: {GetFriendlyExceptionMessage(ex, "Output generation failed.")}");
            }
        }

        private string RunHashOrEncode(string input, string algorithm, int keySize, string password, byte[]? keyfileBytes)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);

            if (algorithm.Contains("SHA", StringComparison.OrdinalIgnoreCase))
            {
                byte[] hash = keySize >= 512 ? SHA512.HashData(inputBytes) : SHA256.HashData(inputBytes);
                return Convert.ToHexString(hash);
            }

            if (algorithm.Contains("Base64", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToBase64String(inputBytes);
            }

            return EncryptTextWithAes(inputBytes, algorithm, keySize, password, keyfileBytes);
        }

        private string EncryptTextWithAes(byte[] inputBytes, string algorithm, int keySize, string password, byte[]? keyfileBytes)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Enter a password for AES-based helpers.");
            }

            if (!algorithm.Contains("GCM", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("FileLocker only exposes authenticated AES-GCM helpers.");
            }

            byte[] salt = GenerateRandomBytes(16);
            int keySizeBytes = Math.Max(16, keySize / 8);
            byte[] key = DeriveKey(password, salt, keyfileBytes, keySizeBytes);
            return EncodeAesGcmPayload(inputBytes, key, salt, keySize);
        }

        private static string EncodeAesGcmPayload(byte[] inputBytes, byte[] key, byte[] salt, int keySize)
        {
            byte[] iv = GenerateRandomBytes(IV_SIZE);
            byte[] ciphertext = new byte[inputBytes.Length];
            byte[] tag = new byte[TAG_SIZE];

            using (var aes = new AesGcm(key, TAG_SIZE))
            {
                aes.Encrypt(iv, inputBytes, ciphertext, tag);
            }

            return EncodeLabeledPayload("AES-GCM", keySize, salt, iv, tag, ciphertext);
        }

        private static string EncodeLabeledPayload(string label, int keySize, byte[] salt, byte[] iv, byte[] tag, byte[] ciphertext)
        {
            using var stream = new MemoryStream();
            WriteLengthPrefixed(stream, salt);
            WriteLengthPrefixed(stream, iv);
            WriteLengthPrefixed(stream, tag);
            WriteLengthPrefixed(stream, ciphertext);

            return $"{label} ({keySize}-bit): {Convert.ToBase64String(stream.ToArray())}";
        }

        private static void WriteLengthPrefixed(Stream stream, byte[] data)
        {
            ushort length = (ushort)data.Length;
            stream.Write(BitConverter.GetBytes(length), 0, sizeof(ushort));
            stream.Write(data, 0, data.Length);
        }

        private void ApplyMetadataOverrides(FileMetadata metadata, string filePath, ProcessingRunOptions options)
        {
            metadata.MetadataLabel = string.IsNullOrWhiteSpace(options.Metadata.Label)
                ? metadata.OriginalFileName
                : options.Metadata.Label;
            metadata.CustomNote = options.Metadata.Notes;
            metadata.Algorithm = options.Algorithm;
            metadata.Mode = options.Mode;
            metadata.KeySizeBits = options.KeySizeBits;

            if (options.Metadata.Randomize)
            {
                var (created, modified) = GenerateRandomizedDates();
                metadata.CreationTime = created;
                metadata.LastWriteTime = modified;
            }
            else
            {
                metadata.CreationTime = ParseDateOrDefault(options.Metadata.CreatedText, File.GetCreationTime(filePath));
                metadata.LastWriteTime = ParseDateOrDefault(options.Metadata.ModifiedText, File.GetLastWriteTime(filePath));
            }
        }

        private (DateTime created, DateTime modified) GenerateRandomizedDates()
        {
            DateTime now = DateTime.UtcNow;
            int backDays = _random.Next(7, 1800);
            DateTime created = now.AddDays(-backDays).AddMinutes(_random.Next(0, 1440));
            DateTime modified = created.AddMinutes(_random.Next(5, 1200));
            return (created, modified);
        }

        private void MetadataRandomizeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (MetadataRandomizeToggle.IsOn)
            {
                ApplyRandomizedMetadataFields();
            }
            else
            {
                MetadataHelperText.Text = "Manual metadata values will be used.";
            }

            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
        }

        private void MetadataRandomizeButton_Click(object sender, RoutedEventArgs e)
        {
            MetadataRandomizeToggle.IsOn = true;
            ApplyRandomizedMetadataFields();
        }

        private void ApplyRandomizedMetadataFields()
        {
            MetadataNameBox.Text = GenerateRandomAlias();
            MetadataNotesBox.Text = $"Randomized note ({DateTime.UtcNow:HH:mm:ss})";
            var (created, modified) = GenerateRandomizedDates();
            MetadataCreatedBox.Text = created.ToString("o", CultureInfo.InvariantCulture);
            MetadataModifiedBox.Text = modified.ToString("o", CultureInfo.InvariantCulture);
            MetadataHelperText.Text = "Randomized metadata will override file timestamps.";
            UpdateRunSummaryBanner();
            UpdateSafetyBanner();
            RefreshPreflightPreview();
        }

        private static string GenerateRandomAlias()
        {
            byte[] aliasBytes = GenerateRandomBytes(6);
            return $"meta-{Convert.ToHexString(aliasBytes).ToLowerInvariant()}";
        }

        private static DateTime ParseDateOrDefault(string? input, DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return fallback;
            }

            if (DateTime.TryParse(
                input,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out DateTime parsedInvariant))
            {
                return parsedInvariant.Kind == DateTimeKind.Utc
                    ? parsedInvariant
                    : parsedInvariant.ToUniversalTime();
            }

            if (DateTime.TryParse(
                input,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out DateTime parsedCurrent))
            {
                return parsedCurrent.ToUniversalTime();
            }

            return fallback;
        }

        private static string? GetComboContent(ComboBox comboBox)
        {
            if (comboBox.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                return text;
            }

            return comboBox.SelectedValue as string;
        }

        private static void SetComboSelection(ComboBox comboBox, string content)
        {
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item &&
                    item.Content is string value &&
                    string.Equals(value, content, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private int ParseKeySizeSelection()
        {
            string? keySizeText = GetComboContent(KeySizeCombo);
            if (int.TryParse(keySizeText, out int keySize))
            {
                return keySize;
            }

            return 256;
        }
        private void ShowPasswordCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            PasswordBox.PasswordRevealMode = PasswordRevealMode.Visible;
        }

        private void ShowPasswordCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            PasswordBox.PasswordRevealMode = PasswordRevealMode.Peek;
        }

        private async Task ProcessFilesAsync(ProcessingIntent intent)
        {
            ProcessingRunOptions? runOptions = null;
            try
            {
                SetUIEnabled(false);
                _isProcessing = true;
                RefreshEncryptFilesState();
                _processingCancellation = new CancellationTokenSource();
                CancelRunButton.IsEnabled = true;
                HideBatchInfoBar();

                string password = PasswordBox.Password;
                string keyfilePath = KeyfilePathBox.Text?.Trim() ?? string.Empty;
                byte[]? keyfileBytes = await Task.Run(() => ReadKeyfileBytesIfConfigured(keyfilePath));
                runOptions = CaptureProcessingRunOptions(keyfilePath, keyfileBytes);
                if (!await ConfirmPreflightAsync(intent, runOptions))
                {
                    return;
                }

                if (intent == ProcessingIntent.Encrypt)
                {
                    MetadataHelperText.Text = runOptions.Metadata.Randomize
                        ? "Randomized metadata will be applied during encryption."
                        : "Manual metadata values will be applied during encryption.";
                }

                var workItems = BuildProcessingWorkItems(intent, runOptions);

                if (workItems.Count == 0)
                {
                    SetStatus("Nothing in the queue needs processing for this action.");
                    return;
                }

                int processed = 0;
                List<string> failedPaths = [];
                List<string> pendingPaths = [];
                List<FileOperationResult> results = [];
                bool cancelled = false;

                for (int fileIndex = 0; fileIndex < workItems.Count; fileIndex++)
                {
                    ProcessingWorkItem workItem = workItems[fileIndex];
                    string filePath = workItem.PrimaryPath;
                    if (_processingCancellation.IsCancellationRequested)
                    {
                        cancelled = true;
                        pendingPaths.AddRange(workItems.Skip(fileIndex).Select(item => item.PrimaryPath));
                        foreach (ProcessingWorkItem pendingWorkItem in workItems.Skip(fileIndex))
                        {
                            SetWorkItemStatus(pendingWorkItem, "Cancelled", "Cancelled before this item started.");
                        }
                        break;
                    }

                    var itemElapsed = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        if (intent == ProcessingIntent.Decrypt &&
                            IsPayloadV3File(filePath) &&
                            !await ConfirmFolderPackageRestoreAsync(filePath, password, runOptions))
                        {
                            SetWorkItemStatus(workItem, "Needs attention", "Restore preview was cancelled.");
                            continue;
                        }

                        SetWorkItemStatus(workItem, "Processing");
                        UpdateWorkItemProgress(workItem, 5, "Preparing");
                        FileOperationResult result;
                        if (intent == ProcessingIntent.Encrypt)
                        {
                            result = await Task.Run(() => workItem.EncryptAsFolderPackage
                                ? EncryptFolderPackage(workItem, password, runOptions, (percent, status) => UpdateWorkItemProgress(workItem, percent, status))
                                : EncryptFileAdvancedCore(
                                    filePath,
                                    password,
                                    runOptions,
                                    workItem.FolderRootPath,
                                    workItem.FolderRootPath is not null,
                                    (percent, status) => UpdateWorkItemProgress(workItem, percent, status)));
                        }
                        else if (intent == ProcessingIntent.Decrypt)
                        {
                            result = await Task.Run(() => DecryptFileAdvanced(filePath, password, runOptions, (percent, status) => UpdateWorkItemProgress(workItem, percent, status)));
                        }
                        else
                        {
                            result = await Task.Run(() => VerifyLockedFile(filePath, password, runOptions, (percent, status) => UpdateWorkItemProgress(workItem, percent, status)));
                        }

                        result.ElapsedMilliseconds ??= itemElapsed.ElapsedMilliseconds;
                        results.Add(result);
                        processed++;
                        UpdateWorkItemProgress(workItem, 100, intent == ProcessingIntent.Verify ? "Verified" : "Completed");
                        SetWorkItemStatus(
                            workItem,
                            intent == ProcessingIntent.Verify ? "Verified" : "Completed",
                            result.Message ?? "Completed successfully.");
                        SetStatus($"Processed {processed}/{workItems.Count} item(s)...");
                    }
                    catch (OperationCanceledException) when (_processingCancellation?.IsCancellationRequested == true)
                    {
                        cancelled = true;
                        results.Add(new FileOperationResult
                        {
                            SourcePath = filePath,
                            Status = "Cancelled",
                            Message = "Cancelled before this item completed.",
                            OriginalRetained = true,
                            OutputVerified = false,
                            FailureCategory = "Cancelled",
                            ElapsedMilliseconds = itemElapsed.ElapsedMilliseconds
                        });
                        UpdateWorkItemProgress(workItem, 100, "Cancelled");
                        SetWorkItemStatus(workItem, "Cancelled", "Cancelled before this item completed.");

                        pendingPaths.AddRange(workItems.Skip(fileIndex + 1).Select(item => item.PrimaryPath));
                        foreach (ProcessingWorkItem pendingWorkItem in workItems.Skip(fileIndex + 1))
                        {
                            SetWorkItemStatus(pendingWorkItem, "Cancelled", "Cancelled before this item started.");
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        string failureMessage = GetFriendlyExceptionMessage(
                            ex,
                            intent == ProcessingIntent.Encrypt
                                ? "Unknown error while encrypting."
                                : intent == ProcessingIntent.Decrypt
                                    ? "Unknown error while decrypting."
                                    : "Unknown error while verifying.");

                        failedPaths.Add(filePath);
                        results.Add(new FileOperationResult
                        {
                            SourcePath = filePath,
                            Status = "Failed",
                            Message = failureMessage,
                            OriginalRetained = true,
                            OutputVerified = false,
                            FailureCategory = OperationFailureClassifier.Classify(ex),
                            ElapsedMilliseconds = itemElapsed.ElapsedMilliseconds
                        });
                        UpdateWorkItemProgress(workItem, 100, "Failed");
                        SetWorkItemStatus(workItem, "Needs attention", failureMessage);
                    }
                }

                OperationMetricsSummary runMetrics = OperationHistoryMetrics.Calculate(results);
                AppendHistory(intent.ToString(), runOptions, results, cancelled);

                if (failedPaths.Count > 0 || pendingPaths.Count > 0 || cancelled)
                {
                    SetStatus(cancelled
                        ? failedPaths.Count > 0
                            ? $"Stopped after {processed} item(s). {failedPaths.Count} failed before cancellation."
                            : $"Stopped after {processed} item(s)."
                        : processed > 0
                            ? $"Completed {processed} item(s). {failedPaths.Count} item(s) still need attention."
                            : $"No items were completed. {failedPaths.Count} item(s) need attention.");

                    ShowBatchInfoBar(
                        cancelled
                            ? failedPaths.Count > 0
                                ? $"Run stopped. {failedPaths.Count} file(s) failed before cancellation."
                                : pendingPaths.Count > 0
                                    ? $"Run stopped. {pendingPaths.Count} file(s) were not started."
                                    : "Run stopped by user."
                            : $"{failedPaths.Count} file(s) need attention. Use the row actions to inspect, retry, or remove them.",
                        InfoBarSeverity.Warning);
                }
                else
                {
                    SetStatus(cancelled
                        ? $"Stopped after {processed} item(s)."
                        : $"Completed {processed} item(s).");
                    ShowBatchInfoBar(
                        cancelled
                            ? $"Run stopped after {processed} file(s)."
                            : intent == ProcessingIntent.Encrypt && runMetrics.CompressionRequestedCount > 0
                                ? BuildCompressionRunSummary(runMetrics)
                                : $"{processed} file(s) completed successfully.",
                        InfoBarSeverity.Success);
                }

                RefreshQueueSummary();
                RefreshPreflightPreview();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Error: {GetFriendlyExceptionMessage(ex, "Processing failed.")}");
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
                RefreshEncryptFilesState();
            }
        }

    }
}


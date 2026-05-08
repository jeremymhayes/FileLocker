using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const string DefaultFileHashAlgorithm = "SHA-256";
        private const long AutoHashMaxBytes = 256L * 1024 * 1024;

        private HashSelectedFileViewModel? _hashSelectedFile;
        private string _hashSelectedAlgorithm = DefaultFileHashAlgorithm;
        private string _generatedFileHash = string.Empty;
        private string _expectedFileHash = string.Empty;
        private string _hashStatusDisplay = "Waiting for file";
        private string _hashVerificationDisplay = "Not verified";
        private bool _isHashingFile;

        public ObservableCollection<HashRecentItem> RecentHashItems { get; } = [];

        private sealed record HashSelectedFileViewModel(
            string DisplayName,
            string FullPath,
            string FileType,
            long SizeBytes,
            string SizeDisplay,
            string LastModifiedDisplay,
            string StatusDisplay);

        public sealed class HashRecentItem
        {
            public string FileName { get; set; } = string.Empty;

            public string Algorithm { get; set; } = string.Empty;

            public string TimestampDisplay { get; set; } = string.Empty;
        }

        private void InitializeHashFilesView()
        {
            _hashSelectedAlgorithm = ReadSelectedHashAlgorithm();
            if (string.IsNullOrWhiteSpace(_hashSelectedAlgorithm))
            {
                _hashSelectedAlgorithm = DefaultFileHashAlgorithm;
            }

            if (HashAlgorithmCombo.SelectedIndex < 0)
            {
                HashAlgorithmCombo.SelectedIndex = 0;
            }

            RefreshHashFilesRecentHashes();
            RefreshHashFilesState();
        }

        private void PrepareHashFilesSection()
        {
            _hashSelectedAlgorithm = ReadSelectedHashAlgorithm();
            if (string.IsNullOrWhiteSpace(_hashSelectedAlgorithm))
            {
                _hashSelectedAlgorithm = DefaultFileHashAlgorithm;
                HashAlgorithmCombo.SelectedIndex = 0;
            }

            RefreshHashFilesRecentHashes();
            RefreshHashFilesState();
        }

        private async void HashBrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = CreateOpenFilePicker(PickerLocationId.DocumentsLibrary, "*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            SelectHashFile(file.Path);
        }

        private async void HashDropPanel_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                int fileCount = items.OfType<StorageFile>().Count();
                HashDropTitleText.Text = fileCount > 0
                    ? fileCount == 1 ? "Release to select this file" : "Release to select the first file"
                    : "Drop a file here to generate a hash";
                HashDropHelperText.Text = fileCount > 0
                    ? "Hash Files uses one primary file at a time."
                    : "Folders are not hashed from this screen.";
                SetHashDropVisual(true);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void HashDropPanel_Drop(object sender, DragEventArgs e)
        {
            ResetHashDropVisual();

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                StorageFile? file = items.OfType<StorageFile>().FirstOrDefault();
                if (file == null)
                {
                    SetStatus("Hash Files accepts one file at a time. Choose a file instead of a folder.");
                    return;
                }

                SelectHashFile(file.Path);
                int droppedFiles = items.OfType<StorageFile>().Count();
                if (droppedFiles > 1)
                {
                    SetStatus($"Selected {file.Name}. Hash Files uses one primary file at a time.");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to select dropped file: {ex.Message}");
            }
        }

        private void HashDropPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetHashDropVisual(true);
        }

        private void HashDropPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ResetHashDropVisual();
        }

        private void SelectHashFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                SetStatus("The selected file could not be found.");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            _hashSelectedFile = new HashSelectedFileViewModel(
                fileInfo.Name,
                fileInfo.FullName,
                GetFileTypeDisplay(fileInfo.FullName),
                fileInfo.Length,
                FormatFileSize(fileInfo.Length),
                FormatDashboardTimestamp(fileInfo.LastWriteTimeUtc),
                "Ready");

            ClearHashOutput(clearExpectedHash: true);
            _hashStatusDisplay = "Ready";
            SetStatus($"Hash file selected: {fileInfo.Name}");
            RefreshHashFilesState();

            if (fileInfo.Length <= AutoHashMaxBytes)
            {
                _ = GenerateSelectedFileHashAsync(autoStarted: true);
            }
            else
            {
                SetStatus($"Hash file selected: {fileInfo.Name}. Large files wait for manual Generate Hash.");
            }
        }

        private async void GenerateFileHashButton_Click(object sender, RoutedEventArgs e)
        {
            await GenerateSelectedFileHashAsync(autoStarted: false);
        }

        private async Task GenerateSelectedFileHashAsync(bool autoStarted)
        {
            if (_hashSelectedFile == null)
            {
                SetStatus("Select a file before generating a hash.");
                return;
            }

            if (_isHashingFile)
            {
                return;
            }

            HashSelectedFileViewModel selectedFile = _hashSelectedFile;
            string selectedAlgorithm = _hashSelectedAlgorithm;
            _isHashingFile = true;
            _hashStatusDisplay = "Hashing";
            _hashVerificationDisplay = "Not verified";
            _generatedFileHash = string.Empty;
            FileHashOutputBox.Text = string.Empty;
            SetStatus($"{(autoStarted ? "Auto-generating" : "Generating")} {selectedAlgorithm} hash for {selectedFile.DisplayName}...");
            RefreshHashFilesState();

            var hashElapsed = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var progress = new Progress<double>(percent =>
                {
                    if (_currentSection == AppSection.HashFiles)
                    {
                        SetStatus($"Hashing {selectedFile.DisplayName}: {percent:0}%");
                    }
                });

                string generatedHash = await FileHashService.ComputeHashHexAsync(
                    selectedFile.FullPath,
                    selectedAlgorithm,
                    progress,
                    CancellationToken.None);

                if (_hashSelectedFile?.FullPath != selectedFile.FullPath ||
                    !string.Equals(_hashSelectedAlgorithm, selectedAlgorithm, StringComparison.OrdinalIgnoreCase))
                {
                    _hashStatusDisplay = _hashSelectedFile == null ? "Waiting for file" : "Ready";
                    SetStatus("Hash result ignored because the selected file or algorithm changed.");
                    return;
                }

                _generatedFileHash = generatedHash;
                FileHashOutputBox.Text = _generatedFileHash;
                _hashStatusDisplay = "Generated";
                _hashVerificationDisplay = "Not verified";
                RecordHashOperation(hashElapsed.ElapsedMilliseconds);
                SetStatus($"{selectedAlgorithm} hash generated for {selectedFile.DisplayName}.");
            }
            catch (Exception ex)
            {
                _hashStatusDisplay = "Failed";
                _generatedFileHash = string.Empty;
                FileHashOutputBox.Text = string.Empty;
                string message = $"Unable to generate the hash: {GetFriendlyExceptionMessage(ex, "Hashing failed.")}";
                if (autoStarted)
                {
                    SetStatus(message);
                }
                else
                {
                    await ShowErrorDialogAsync(message);
                }
            }
            finally
            {
                _isHashingFile = false;
                RefreshHashFilesState();
            }
        }

        private void ClearHashFilesButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHashFilesState(clearFile: true, setStatus: true);
        }

        private void HashRemoveFileButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHashFilesState(clearFile: true, setStatus: true);
        }

        private void HashAlgorithmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string nextAlgorithm = ReadSelectedHashAlgorithm();
            if (string.IsNullOrWhiteSpace(nextAlgorithm))
            {
                nextAlgorithm = DefaultFileHashAlgorithm;
            }

            bool changed = !string.Equals(_hashSelectedAlgorithm, nextAlgorithm, StringComparison.OrdinalIgnoreCase);
            _hashSelectedAlgorithm = nextAlgorithm;

            if (!IsHashFilesUiReady())
            {
                return;
            }

            UpdateHashAlgorithmHelper();

            if (changed && !string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                ClearHashOutput(clearExpectedHash: false);
                _hashStatusDisplay = _hashSelectedFile == null ? "Waiting for file" : "Ready";
                SetStatus("Hash algorithm changed. Generate a new hash for the selected file.");
            }

            RefreshHashFilesState();
        }

        private void ExpectedHashBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _expectedFileHash = ExpectedHashBox.Text ?? string.Empty;
            if (_hashVerificationDisplay is "Match" or "Mismatch")
            {
                _hashVerificationDisplay = "Not verified";
            }

            if (IsHashFilesUiReady())
            {
                RefreshHashFilesState();
            }
        }

        private void VerifyHashButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                _hashVerificationDisplay = "Not verified";
                SetStatus("Generate a hash before verifying.");
                RefreshHashFilesState();
                return;
            }

            string expected = NormalizeHashInput(ExpectedHashBox.Text);
            if (string.IsNullOrWhiteSpace(expected))
            {
                _hashVerificationDisplay = "Not verified";
                SetStatus("Paste an expected hash before verifying.");
                RefreshHashFilesState();
                return;
            }

            string generated = NormalizeHashInput(_generatedFileHash);
            _hashVerificationDisplay = string.Equals(expected, generated, StringComparison.OrdinalIgnoreCase)
                ? "Match"
                : "Mismatch";

            SetStatus(_hashVerificationDisplay == "Match"
                ? "Hash match confirmed."
                : "Hash mismatch detected.");
            RefreshHashFilesState();
        }

        private void CopyFileHashButton_Click(object sender, RoutedEventArgs e)
        {
            CopyGeneratedFileHashToClipboard();
        }

        private void HashCopyResultsHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            CopyHashResultSummaryToClipboard();
        }

        private async void HashSaveResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hashSelectedFile == null || string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                SetStatus("Generate a hash before saving the result.");
                return;
            }

            try
            {
                string algorithmSlug = _hashSelectedAlgorithm.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
                FileSavePicker picker = CreateSaveFilePicker(PickerLocationId.DocumentsLibrary, $"{Path.GetFileNameWithoutExtension(_hashSelectedFile.DisplayName)}.{algorithmSlug}.hash");
                picker.FileTypeChoices.Add("Text hash result", [".txt"]);

                StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                await File.WriteAllTextAsync(file.Path, BuildHashResultExport(), Encoding.UTF8);
                SetStatus($"Hash result saved to {file.Name}.");
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to save the hash result: {ex.Message}");
            }
        }

        private async void HashGuideHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 620
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Use Hash Files to calculate a file fingerprint and compare it with a known value from a trusted source.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "SHA-256 is the default recommendation for general integrity checks. SHA-512 is available when you need a longer digest.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "A matching hash means the generated value equals the expected value. It does not prove that the source of the expected hash was trustworthy.",
                TextWrapping = TextWrapping.WrapWholeWords
            });

            await ShowInfoDialogAsync(panel, "Hash Guide");
        }

        private void ClearHashFilesState(bool clearFile, bool setStatus)
        {
            if (clearFile)
            {
                _hashSelectedFile = null;
            }

            ClearHashOutput(clearExpectedHash: true);
            _hashStatusDisplay = _hashSelectedFile == null ? "Waiting for file" : "Ready";
            _hashVerificationDisplay = "Not verified";

            if (setStatus)
            {
                SetStatus("Hash Files cleared.");
            }

            RefreshHashFilesState();
        }

        private void ClearHashOutput(bool clearExpectedHash)
        {
            _generatedFileHash = string.Empty;
            _hashVerificationDisplay = "Not verified";
            if (FileHashOutputBox != null)
            {
                FileHashOutputBox.Text = string.Empty;
            }

            if (clearExpectedHash && ExpectedHashBox != null)
            {
                ExpectedHashBox.Text = string.Empty;
                _expectedFileHash = string.Empty;
            }
        }

        private void RefreshHashFilesState()
        {
            if (!IsHashFilesUiReady())
            {
                return;
            }

            bool hasFile = _hashSelectedFile != null;
            bool hasHash = CanCopyCurrentHash();
            bool canGenerate = hasFile && !_isHashingFile;
            bool canVerify = CanVerifyCurrentHash() && !_isHashingFile;

            HashSelectedFileEmptyText.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
            HashSelectedFileDetailsGrid.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
            HashRemoveFileButton.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;

            if (_hashSelectedFile != null)
            {
                HashSelectedFileNameText.Text = _hashSelectedFile.DisplayName;
                HashSelectedFileTypeText.Text = _hashSelectedFile.FileType;
                HashSelectedFileSizeText.Text = _hashSelectedFile.SizeDisplay;
                HashSelectedFileModifiedText.Text = _hashSelectedFile.LastModifiedDisplay;
                HashSelectedFileStatusText.Text = _isHashingFile ? "Hashing" : _hashStatusDisplay == "Generated" ? "Generated" : "Ready";
                Brush fileStatusBrush = GetHashStatusBrush(_hashStatusDisplay);
                HashSelectedFileStatusText.Foreground = fileStatusBrush;
                HashSelectedFileStatusMarker.Background = fileStatusBrush;
            }

            HashOutputAlgorithmLabel.Text = $"{_hashSelectedAlgorithm} Hash";
            HashSummaryFileText.Text = _hashSelectedFile?.DisplayName ?? "—";
            HashSummaryAlgorithmText.Text = _hashSelectedAlgorithm;
            HashSummaryLengthText.Text = $"{FileHashService.GetExpectedHexLength(_hashSelectedAlgorithm)} characters";
            HashSummaryStatusText.Text = _isHashingFile ? "Hashing" : _hashStatusDisplay;
            HashSummaryVerificationText.Text = _hashVerificationDisplay;

            Brush statusBrush = GetHashStatusBrush(_isHashingFile ? "Hashing" : _hashStatusDisplay);
            HashSummaryStatusText.Foreground = statusBrush;
            HashSummaryStatusMarker.Background = statusBrush;

            Brush verificationBrush = GetVerificationBrush(_hashVerificationDisplay);
            HashSummaryVerificationText.Foreground = verificationBrush;
            HashSummaryVerificationMarker.Background = verificationBrush;

            HashGeneratedStatusBadge.Visibility = hasHash && _hashStatusDisplay == "Generated"
                ? Visibility.Visible
                : Visibility.Collapsed;

            GenerateFileHashButton.IsEnabled = canGenerate;
            HashCopyButton.IsEnabled = hasHash && !_isHashingFile;
            HashInlineCopyButton.IsEnabled = hasHash && !_isHashingFile;
            HashSaveResultButton.IsEnabled = hasHash && !_isHashingFile;
            HashCopyResultsHeaderButton.IsEnabled = hasHash && !_isHashingFile;
            VerifyHashButton.IsEnabled = canVerify;
            HashAlgorithmCombo.IsEnabled = !_isHashingFile;
            HashBrowseFileButton.IsEnabled = !_isHashingFile;
            ExpectedHashBox.IsEnabled = !_isHashingFile;
            HashDropPanel.AllowDrop = !_isHashingFile;

            UpdateHashAlgorithmHelper();
            UpdateHashVerificationResultVisual();
        }

        private void RefreshHashFilesRecentHashes()
        {
            if (!IsHashFilesUiReady())
            {
                return;
            }

            RecentHashItems.Clear();

            if (_preferences.HistoryPrivacyMode != HistoryPrivacyMode.Off)
            {
                foreach (OperationHistoryEntry entry in _operationHistory
                    .Where(history => string.Equals(history.Operation, "Hash", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(history => history.TimestampUtc)
                    .Take(5))
                {
                    FileOperationResult? result = entry.Results.FirstOrDefault();
                    string fileName = result == null
                        ? "Unknown file"
                        : Path.GetFileName(GetResultDisplayPath(result));
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = "Unknown file";
                    }

                    RecentHashItems.Add(new HashRecentItem
                    {
                        FileName = fileName,
                        Algorithm = entry.Algorithm,
                        TimestampDisplay = FormatRecentHashTimestamp(entry.TimestampUtc)
                    });
                }
            }

            HashRecentEmptyText.Visibility = RecentHashItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HashRecentHashesListView.Visibility = RecentHashItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RecordHashOperation(long elapsedMilliseconds)
        {
            if (_hashSelectedFile == null ||
                string.IsNullOrWhiteSpace(_generatedFileHash) ||
                _preferences.HistoryPrivacyMode == HistoryPrivacyMode.Off)
            {
                RefreshHashFilesRecentHashes();
                return;
            }

            var entry = new OperationHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow,
                Operation = "Hash",
                ProfileName = "Hash Files",
                Algorithm = _hashSelectedAlgorithm,
                Mode = "File integrity hash",
                KeySizeBits = FileHashService.GetDigestBits(_hashSelectedAlgorithm),
                UsedKeyfile = false,
                RemoveOriginalsAfterSuccess = false,
                SecureDeleteOriginals = false,
                VerifyAfterWrite = false,
                BackupFolderPath = string.Empty,
                Cancelled = false,
                SuccessCount = 1,
                FailureCount = 0,
                TotalOriginalSizeBytes = _hashSelectedFile.SizeBytes,
                TotalOutputSizeBytes = _hashSelectedFile.SizeBytes,
                TotalStorageSavedBytes = 0,
                TotalStorageAddedBytes = 0,
                ElapsedMilliseconds = elapsedMilliseconds,
                Results =
                [
                    new FileOperationResult
                    {
                        SourcePath = _hashSelectedFile.FullPath,
                        OutputPath = null,
                        Status = "Completed",
                        Message = $"{_hashSelectedAlgorithm} hash generated.",
                        OriginalRetained = true,
                        OutputVerified = true,
                        OriginalSizeBytes = _hashSelectedFile.SizeBytes,
                        OutputSizeBytes = _hashSelectedFile.SizeBytes,
                        ElapsedMilliseconds = elapsedMilliseconds,
                        HashValue = _generatedFileHash
                    }
                ]
            };

            _operationHistory.Insert(0, entry);
            while (_operationHistory.Count > MaxHistoryEntries)
            {
                _operationHistory.RemoveAt(_operationHistory.Count - 1);
            }

            SaveHistory();
            RefreshHistoryItems();
            RefreshHashFilesRecentHashes();
        }

        private void CopyGeneratedFileHashToClipboard()
        {
            if (string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                SetStatus("Generate a hash before copying results.");
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(_generatedFileHash);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            SetStatus("Hash copied to clipboard.");
        }

        private void CopyHashResultSummaryToClipboard()
        {
            if (_hashSelectedFile == null || string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                SetStatus("Generate a hash before copying the result summary.");
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(BuildHashResultExport());
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            SetStatus("Filename and hash summary copied to clipboard.");
        }

        private string BuildHashResultExport()
        {
            if (_hashSelectedFile == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("FileLocker Hash Result");
            builder.AppendLine();
            builder.AppendLine($"File: {(_preferences.IncludeFullPathsInExports ? _hashSelectedFile.FullPath : _hashSelectedFile.DisplayName)}");
            builder.AppendLine($"Algorithm: {_hashSelectedAlgorithm}");
            builder.AppendLine($"Hash: {_generatedFileHash}");
            builder.AppendLine($"Generated: {DateTime.Now.ToString("f", CultureInfo.CurrentCulture)}");
            builder.AppendLine();
            builder.AppendLine("Verification note: a matching hash means the generated value equals the expected value you compare against.");
            return builder.ToString();
        }

        private void UpdateHashVerificationResultVisual()
        {
            string title;
            string detail;
            string glyph;
            Brush accentBrush;
            Brush backgroundBrush;

            if (_hashVerificationDisplay == "Match")
            {
                title = "Hash matches";
                detail = "Generated hash equals the expected value.";
                glyph = "\uE73E";
                accentBrush = GetBrushResource("SuccessBrush");
                backgroundBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                HashVerifyResultBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 45, 41));
            }
            else if (_hashVerificationDisplay == "Mismatch")
            {
                title = "Hash does not match";
                detail = "The file may be different or corrupted.";
                glyph = "\uE711";
                accentBrush = GetBrushResource("DangerBrush");
                backgroundBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 55, 18, 30));
                HashVerifyResultBorder.Background = backgroundBrush;
            }
            else if (string.IsNullOrWhiteSpace(_generatedFileHash))
            {
                title = "Generate a hash before verifying.";
                detail = "Paste a known hash to compare against the generated result.";
                glyph = "\uE946";
                accentBrush = GetBrushResource("TextSecondaryBrush");
                backgroundBrush = GetBrushResource("InputSurfaceBrush");
                HashVerifyResultBorder.Background = backgroundBrush;
            }
            else if (string.IsNullOrWhiteSpace(_expectedFileHash))
            {
                title = "Paste an expected hash";
                detail = "Verification will compare it with the generated file hash.";
                glyph = "\uE946";
                accentBrush = GetBrushResource("TextSecondaryBrush");
                backgroundBrush = GetBrushResource("InputSurfaceBrush");
                HashVerifyResultBorder.Background = backgroundBrush;
            }
            else
            {
                title = "Ready to verify";
                detail = "Press Verify to compare the expected value with the generated hash.";
                glyph = "\uE946";
                accentBrush = GetBrushResource("BrightBlueBrush");
                backgroundBrush = GetBrushResource("InputSurfaceBrush");
                HashVerifyResultBorder.Background = backgroundBrush;
            }

            HashVerifyResultTitleText.Text = title;
            HashVerifyResultDetailText.Text = detail;
            HashVerifyResultIcon.Glyph = glyph;
            HashVerifyResultTitleText.Foreground = accentBrush;
            HashVerifyResultIcon.Foreground = accentBrush;
            HashVerifyResultIconBorder.BorderBrush = accentBrush;
        }

        private void UpdateHashAlgorithmHelper()
        {
            if (!IsHashFilesUiReady())
            {
                return;
            }

            HashAlgorithmHelperText.Text = _hashSelectedAlgorithm == "SHA-512"
                ? "SHA-512 produces a longer digest. SHA-256 remains the default recommendation for general file integrity checks."
                : "SHA-256 is recommended for general file integrity checks.";
        }

        private bool IsHashFilesUiReady()
        {
            return HashSelectedFileEmptyText != null &&
                HashSummaryFileText != null &&
                HashAlgorithmHelperText != null &&
                HashVerifyResultTitleText != null &&
                HashRecentEmptyText != null &&
                GenerateFileHashButton != null;
        }

        private void SetHashDropVisual(bool active)
        {
            HashDropPanel.Background = active
                ? GetBrushResource("DropPanelActiveBrush")
                : GetBrushResource("HeroSurfaceBrush");
            HashDropPanel.BorderBrush = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            HashDropPanelDashBorder.Stroke = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            HashDropIconTile.Background = active
                ? GetBrushResource("PrimaryActionBrush")
                : GetBrushResource("AccentSoftBrush");
        }

        private void ResetHashDropVisual()
        {
            SetHashDropVisual(false);
            HashDropTitleText.Text = "Drop a file here to generate a hash";
            HashDropHelperText.Text = "Useful for checking file integrity and verifying downloads";
        }

        private static string NormalizeHashInput(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            return new string(input.Where(character => !char.IsWhiteSpace(character)).ToArray())
                .Trim()
                .ToLowerInvariant();
        }

        private string ReadSelectedHashAlgorithm()
        {
            string? selected = (HashAlgorithmCombo.SelectedItem as ComboBoxItem)?.Content as string;
            return selected?.Contains("512", StringComparison.OrdinalIgnoreCase) == true
                ? "SHA-512"
                : "SHA-256";
        }

        private bool CanCopyCurrentHash() => !string.IsNullOrWhiteSpace(_generatedFileHash);

        private bool CanVerifyCurrentHash() =>
            !string.IsNullOrWhiteSpace(_generatedFileHash) &&
            !string.IsNullOrWhiteSpace(_expectedFileHash);

        private Brush GetHashStatusBrush(string status)
        {
            return status switch
            {
                "Ready" or "Generated" => GetBrushResource("SuccessBrush"),
                "Hashing" => GetBrushResource("BrightBlueBrush"),
                "Failed" => GetBrushResource("DangerBrush"),
                _ => GetBrushResource("TextSecondaryBrush")
            };
        }

        private Brush GetVerificationBrush(string verification)
        {
            return verification switch
            {
                "Match" => GetBrushResource("SuccessBrush"),
                "Mismatch" => GetBrushResource("DangerBrush"),
                _ => GetBrushResource("TextSecondaryBrush")
            };
        }

        private static string FormatRecentHashTimestamp(DateTime timestampUtc)
        {
            DateTime local = timestampUtc.ToLocalTime();
            if (local.Date == DateTime.Today)
            {
                return "Today";
            }

            if (local.Date == DateTime.Today.AddDays(-1))
            {
                return "Yesterday";
            }

            return local.ToString("M/d", CultureInfo.CurrentCulture);
        }
    }
}

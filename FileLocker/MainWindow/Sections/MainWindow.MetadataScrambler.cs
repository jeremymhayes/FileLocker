using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private const int MaxMetadataFolderFiles = 250;
        private const bool MetadataWriteSupportEnabled = false;

        private readonly HashSet<string> _metadataSelectedPaths = new(StringComparer.OrdinalIgnoreCase);
        private string _metadataScramblerStatus = "Waiting for files";
        private string _metadataScramblerMode = "Remove metadata";
        private string _lastMetadataPreviewReport = string.Empty;

        public ObservableCollection<MetadataSelectedFileViewModel> MetadataSelectedFiles { get; } = [];

        public ObservableCollection<MetadataCategoryViewModel> MetadataCategories { get; } = [];

        public ObservableCollection<MetadataPreviewItemViewModel> MetadataPreviewItems { get; } = [];

        public sealed class MetadataSelectedFileViewModel
        {
            public required string DisplayName { get; set; }

            public required string FullPath { get; set; }

            public required string FileType { get; set; }

            public long SizeBytes { get; set; }

            public required string SizeDisplay { get; set; }

            public required string MetadataCountDisplay { get; set; }

            public int MetadataTagCount { get; set; }

            public required string StatusDisplay { get; set; }

            public bool IsSupported { get; set; }
        }

        public sealed class MetadataCategoryViewModel : INotifyPropertyChanged
        {
            private bool _isSelected;

            public required string Name { get; set; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public bool IsSupported { get; set; } = true;

            public required string Description { get; set; }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public sealed class MetadataPreviewItemViewModel
        {
            public required string Label { get; set; }

            public required string BeforeValue { get; set; }

            public required string AfterValue { get; set; }
        }

        private sealed class MetadataPathExpansionResult
        {
            public List<string> FilePaths { get; } = [];

            public List<string> Warnings { get; } = [];

            public bool HitLimit { get; set; }
        }

        private void InitializeMetadataScramblerView()
        {
            MetadataSelectedFilesListView.ItemsSource = MetadataSelectedFiles;
            MetadataCategoriesListView.ItemsSource = MetadataCategories;
            MetadataPreviewItemsListView.ItemsSource = MetadataPreviewItems;

            if (MetadataCategories.Count == 0)
            {
                InitializeMetadataCategories();
            }

            if (MetadataModeComboBox.SelectedIndex < 0)
            {
                MetadataModeComboBox.SelectedIndex = 0;
            }

            _metadataScramblerMode = ReadSelectedMetadataMode();
            SetMetadataScramblerStatus("Waiting for files", updateGlobalStatus: false);
            ResetMetadataDropText();
            RefreshMetadataScramblerState();
        }

        private void PrepareMetadataScramblerSection()
        {
            _metadataScramblerMode = ReadSelectedMetadataMode();
            RefreshMetadataScramblerState();
        }

        private void InitializeMetadataCategories()
        {
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Author information",
                Description = "Names, document authors, owner fields",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "GPS/location data",
                Description = "Location-related fields when a file exposes them",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Camera/device data",
                Description = "Device and capture fields for supported media",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Timestamps",
                Description = "Created, modified, and access timestamps",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Document properties",
                Description = "Title, subject, company, and similar fields",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Application metadata",
                Description = "Producer, generator, and editing application fields",
                IsSelected = true
            });
            MetadataCategories.Add(new MetadataCategoryViewModel
            {
                Name = "Custom metadata",
                Description = "Other structured fields exposed by supported formats",
                IsSelected = true
            });
        }

        private async void MetadataBrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker picker = CreateOpenFilePicker(
                    PickerLocationId.DocumentsLibrary,
                    ".jpg",
                    ".jpeg",
                    ".png",
                    ".webp",
                    ".tif",
                    ".tiff",
                    ".heic",
                    ".bmp",
                    ".pdf",
                    ".docx",
                    ".xlsx",
                    ".pptx",
                    ".rtf",
                    ".txt",
                    ".mp4",
                    ".mov",
                    ".webm",
                    ".mkv",
                    ".mp3",
                    ".wav");
                picker.FileTypeFilter.Add("*");

                IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
                if (files.Count == 0)
                {
                    return;
                }

                await AddMetadataScramblerPathsAsync(files.Select(file => file.Path));
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to browse metadata files: {GetFriendlyExceptionMessage(ex, "File picker failed.")}");
            }
        }

        private async void MetadataBrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);
                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    return;
                }

                await AddMetadataScramblerPathsAsync([folder.Path]);
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to browse metadata folder: {GetFriendlyExceptionMessage(ex, "Folder picker failed.")}");
            }
        }

        private async void MetadataDropPanel_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            SetMetadataDropVisual(active: true);

            var deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                MetadataDropTitleText.Text = items.Count > 0
                    ? $"Release {items.Count} item(s) to inspect"
                    : "Drop files here to inspect metadata";
                MetadataDropHelperText.Text = "Files stay on this device. Preview does not modify originals.";
            }
            catch
            {
                MetadataDropTitleText.Text = "Drop files here to inspect metadata";
                MetadataDropHelperText.Text = "or browse from your device";
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void MetadataDropPanel_Drop(object sender, DragEventArgs e)
        {
            SetMetadataDropVisual(active: false);
            ResetMetadataDropText();

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            try
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                List<string> paths = [];
                foreach (IStorageItem item in items)
                {
                    if (item is StorageFile file)
                    {
                        paths.Add(file.Path);
                    }
                    else if (item is StorageFolder folder)
                    {
                        paths.Add(folder.Path);
                    }
                }

                await AddMetadataScramblerPathsAsync(paths);
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to inspect dropped files: {GetFriendlyExceptionMessage(ex, "Drop failed.")}");
            }
        }

        private void MetadataDropPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetMetadataDropVisual(active: true);
        }

        private void MetadataDropPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            SetMetadataDropVisual(active: false);
            ResetMetadataDropText();
        }

        private void MetadataSelectedFilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshMetadataScramblerState();
        }

        private void MetadataSelectedFileRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string path)
            {
                return;
            }

            MetadataSelectedFileViewModel? item = MetadataSelectedFiles.FirstOrDefault(file =>
                string.Equals(file.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return;
            }

            MetadataSelectedFiles.Remove(item);
            _metadataSelectedPaths.Remove(item.FullPath);
            MetadataPreviewItems.Clear();
            _lastMetadataPreviewReport = string.Empty;
            SetMetadataScramblerStatus(MetadataSelectedFiles.Count == 0 ? "Waiting for files" : "Ready to preview", updateGlobalStatus: false);
            RefreshMetadataScramblerState();
            SetStatus($"Removed {item.DisplayName} from Metadata Scrambler.");
        }

        private void MetadataCategoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            RefreshMetadataScramblerState();
        }

        private void MetadataCategorySelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (MetadataCategoryViewModel category in MetadataCategories.Where(category => category.IsSupported))
            {
                category.IsSelected = true;
            }

            RefreshMetadataScramblerState();
            SetStatus("Metadata categories selected for preview.");
        }

        private void MetadataCategoryClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (MetadataCategoryViewModel category in MetadataCategories)
            {
                category.IsSelected = false;
            }

            RefreshMetadataScramblerState();
            SetStatus("Metadata category selection cleared.");
        }

        private void MetadataModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _metadataScramblerMode = ReadSelectedMetadataMode();
            if (MetadataPreviewItems.Count > 0)
            {
                BuildMetadataPreview(GetActiveMetadataFile());
            }

            RefreshMetadataScramblerState();
        }

        private void MetadataPreviewChangesButton_Click(object sender, RoutedEventArgs e)
        {
            MetadataSelectedFileViewModel? activeFile = GetActiveMetadataFile();
            if (activeFile == null)
            {
                SetMetadataScramblerStatus("Waiting for files");
                return;
            }

            BuildMetadataPreview(activeFile);
            SetStatus($"Previewed metadata for {activeFile.DisplayName}. No files were changed.");
        }

        private async void MetadataScrambleButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowInfoDialogAsync(
                "Metadata writing is not available yet. FileLocker can select files and build a local, non-destructive preview, but it will not claim metadata was removed until safe writing support is available.",
                "Metadata Scrambler");
            SetMetadataScramblerStatus("Preview only");
        }

        private void MetadataClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            MetadataSelectedFiles.Clear();
            _metadataSelectedPaths.Clear();
            MetadataPreviewItems.Clear();
            _lastMetadataPreviewReport = string.Empty;
            SetMetadataScramblerStatus("Waiting for files", updateGlobalStatus: false);
            RefreshMetadataScramblerState();
            SetStatus("Metadata Scrambler selection cleared. No files were changed.");
        }

        private async void MetadataGuideHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 620
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Metadata Scrambler is local-only. Files are selected and previewed on this device; nothing is uploaded.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Metadata support varies by file type. This screen currently previews basic file properties and keeps write actions disabled until safe cleanup support is available.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Preview Changes does not modify files. FileLocker will not claim metadata was removed from unsupported formats.",
                TextWrapping = TextWrapping.WrapWholeWords
            });

            await ShowInfoDialogAsync(panel, "Metadata Guide");
        }

        private async void MetadataReportHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastMetadataPreviewReport))
            {
                await ShowInfoDialogAsync("Preview a selected file before viewing a report.", "Metadata Report");
                return;
            }

            await ShowInfoDialogAsync(
                new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock
                    {
                        Text = _lastMetadataPreviewReport,
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                },
                "Metadata Preview Report");
        }

        private async Task AddMetadataScramblerPathsAsync(IEnumerable<string> rawPaths)
        {
            string[] requestedPaths = rawPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .ToArray();

            if (requestedPaths.Length == 0)
            {
                return;
            }

            MetadataPathExpansionResult expansion = await Task.Run(() => ExpandMetadataScramblerPaths(requestedPaths));
            int addedCount = 0;
            int duplicateCount = 0;

            foreach (string path in expansion.FilePaths)
            {
                if (!_metadataSelectedPaths.Add(path))
                {
                    duplicateCount++;
                    continue;
                }

                MetadataSelectedFiles.Add(CreateMetadataSelectedFile(path));
                addedCount++;
            }

            if (MetadataSelectedFiles.Count > 0 && MetadataSelectedFilesListView.SelectedItem == null)
            {
                MetadataSelectedFilesListView.SelectedItem = MetadataSelectedFiles[0];
            }

            MetadataPreviewItems.Clear();
            _lastMetadataPreviewReport = string.Empty;
            SetMetadataScramblerStatus(MetadataSelectedFiles.Count == 0 ? "Waiting for files" : "Ready to preview", updateGlobalStatus: false);
            RefreshMetadataScramblerState();

            string summary = addedCount == 0 && duplicateCount > 0
                ? $"Skipped {duplicateCount} duplicate metadata file(s)."
                : $"Added {addedCount} file(s) for metadata preview.";

            if (duplicateCount > 0 && addedCount > 0)
            {
                summary += $" Skipped {duplicateCount} duplicate(s).";
            }

            if (expansion.HitLimit)
            {
                summary += $" Folder scan stopped at {MaxMetadataFolderFiles} files.";
            }

            if (expansion.Warnings.Count > 0)
            {
                summary += $" {expansion.Warnings.Count} warning(s) captured.";
            }

            SetStatus(summary);
        }

        private static MetadataPathExpansionResult ExpandMetadataScramblerPaths(IEnumerable<string> paths)
        {
            var result = new MetadataPathExpansionResult();
            foreach (string rawPath in paths)
            {
                if (result.FilePaths.Count >= MaxMetadataFolderFiles)
                {
                    result.HitLimit = true;
                    break;
                }

                string path = rawPath.Trim();
                if (File.Exists(path))
                {
                    result.FilePaths.Add(path);
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    result.Warnings.Add($"Skipped missing path: {path}");
                    continue;
                }

                foreach (string filePath in EnumerateMetadataFolderFiles(path, result.Warnings))
                {
                    result.FilePaths.Add(filePath);
                    if (result.FilePaths.Count >= MaxMetadataFolderFiles)
                    {
                        result.HitLimit = true;
                        break;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> EnumerateMetadataFolderFiles(string rootFolderPath, ICollection<string> warnings)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootFolderPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectory = pendingDirectories.Pop();
                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(currentDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Unable to enumerate folders inside {currentDirectory}: {ex.Message}");
                    continue;
                }

                foreach (string childDirectory in childDirectories)
                {
                    pendingDirectories.Push(childDirectory);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDirectory);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Unable to enumerate files inside {currentDirectory}: {ex.Message}");
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
                }
            }
        }

        private MetadataSelectedFileViewModel CreateMetadataSelectedFile(string path)
        {
            var fileInfo = new FileInfo(path);
            string extension = fileInfo.Extension.ToLowerInvariant();
            bool isCommonMetadataType = IsCommonMetadataFileType(extension);
            int fieldCount = CountBasicMetadataFields(fileInfo);

            return new MetadataSelectedFileViewModel
            {
                DisplayName = fileInfo.Name,
                FullPath = fileInfo.FullName,
                FileType = GetMetadataFileTypeDisplay(extension),
                SizeBytes = fileInfo.Length,
                SizeDisplay = FormatFileSize(fileInfo.Length),
                MetadataTagCount = fieldCount,
                MetadataCountDisplay = fieldCount > 0 ? $"{fieldCount} basic fields" : "No basic fields",
                StatusDisplay = isCommonMetadataType ? "Ready" : "Review",
                IsSupported = isCommonMetadataType
            };
        }

        private void BuildMetadataPreview(MetadataSelectedFileViewModel? file)
        {
            MetadataPreviewItems.Clear();

            if (file == null)
            {
                _lastMetadataPreviewReport = string.Empty;
                SetMetadataScramblerStatus("Waiting for files", updateGlobalStatus: false);
                RefreshMetadataScramblerState();
                return;
            }

            if (!File.Exists(file.FullPath))
            {
                _lastMetadataPreviewReport = string.Empty;
                SetMetadataScramblerStatus("Failed", updateGlobalStatus: false);
                RefreshMetadataScramblerState();
                return;
            }

            try
            {
                var fileInfo = new FileInfo(file.FullPath);
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "File name",
                    BeforeValue = fileInfo.Name,
                    AfterValue = "Preserved in preview"
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "File type",
                    BeforeValue = GetMetadataFileTypeDisplay(fileInfo.Extension.ToLowerInvariant()),
                    AfterValue = "Preserved in preview"
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "Size",
                    BeforeValue = FormatFileSize(fileInfo.Length),
                    AfterValue = "Preserved in preview"
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "Created",
                    BeforeValue = FormatMetadataDate(fileInfo.CreationTime),
                    AfterValue = GetTimestampPreviewAction()
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "Modified",
                    BeforeValue = FormatMetadataDate(fileInfo.LastWriteTime),
                    AfterValue = GetTimestampPreviewAction()
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "Last accessed",
                    BeforeValue = FormatMetadataDate(fileInfo.LastAccessTime),
                    AfterValue = GetTimestampPreviewAction()
                });
                MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                {
                    Label = "Attributes",
                    BeforeValue = fileInfo.Attributes.ToString(),
                    AfterValue = "Preserved in preview"
                });

                foreach (MetadataCategoryViewModel category in MetadataCategories.Where(category => category.IsSelected && !IsBasicTimestampCategory(category.Name)))
                {
                    MetadataPreviewItems.Add(new MetadataPreviewItemViewModel
                    {
                        Label = category.Name,
                        BeforeValue = "Not inspected",
                        AfterValue = "No file changes in preview"
                    });
                }

                SetMetadataScramblerStatus("Preview ready", updateGlobalStatus: false);
                _lastMetadataPreviewReport = BuildMetadataPreviewReport(file);
            }
            catch (Exception ex)
            {
                MetadataPreviewItems.Clear();
                _lastMetadataPreviewReport = string.Empty;
                SetMetadataScramblerStatus("Failed", updateGlobalStatus: false);
                SetStatus($"Unable to preview metadata: {GetFriendlyExceptionMessage(ex, "Preview failed.")}");
            }
            finally
            {
                RefreshMetadataScramblerState();
            }
        }

        private MetadataSelectedFileViewModel? GetActiveMetadataFile()
        {
            return MetadataSelectedFilesListView?.SelectedItem as MetadataSelectedFileViewModel
                ?? MetadataSelectedFiles.FirstOrDefault();
        }

        private void SetMetadataDropVisual(bool active)
        {
            if (MetadataDropPanel == null)
            {
                return;
            }

            MetadataDropPanel.Background = active
                ? GetBrushResource("DropPanelActiveBrush")
                : GetBrushResource("HeroSurfaceBrush");
            MetadataDropPanel.BorderBrush = GetBrushResource("MetadataBrush");
            MetadataDropPanelDashBorder.Stroke = GetBrushResource("MetadataBrush");
            MetadataDropIconTile.Background = active
                ? GetBrushResource("AccentSoftBrush")
                : GetBrushResource("AccentSoftBrush");
            MetadataDropIconTile.BorderBrush = active
                ? GetBrushResource("MetadataBrush")
                : GetBrushResource("AppBorderBrush");
        }

        private void ResetMetadataDropText()
        {
            if (MetadataDropTitleText == null || MetadataDropHelperText == null)
            {
                return;
            }

            MetadataDropTitleText.Text = "Drop files here to inspect metadata";
            MetadataDropHelperText.Text = "or browse from your device";
        }

        private void RefreshMetadataScramblerState()
        {
            if (MetadataSelectedFilesListView == null ||
                MetadataSelectedFilesCountText == null ||
                MetadataSummaryFileCountText == null ||
                MetadataCategoriesCountText == null ||
                MetadataPreviewChangesButton == null)
            {
                return;
            }

            int fileCount = MetadataSelectedFiles.Count;
            int selectedCategoryCount = MetadataCategories.Count(category => category.IsSelected);
            int totalBasicFields = MetadataSelectedFiles.Sum(file => file.MetadataTagCount);
            bool hasFiles = fileCount > 0;
            bool hasPreview = MetadataPreviewItems.Count > 0;
            bool canPreview = hasFiles;
            bool canScramble = MetadataWriteSupportEnabled &&
                hasFiles &&
                selectedCategoryCount > 0 &&
                !string.Equals(_metadataScramblerMode, "Preview only", StringComparison.OrdinalIgnoreCase);

            MetadataSelectedFilesCountText.Text = $"({fileCount.ToString(CultureInfo.InvariantCulture)})";
            MetadataSelectedFilesStatusText.Text = hasFiles ? "Selected locally" : "Local preview only";
            MetadataSelectedFilesEmptyText.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
            MetadataSelectedFilesListView.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;

            MetadataPreviewEmptyText.Visibility = hasPreview ? Visibility.Collapsed : Visibility.Visible;
            MetadataPreviewItemsListView.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
            MetadataReportHeaderButton.IsEnabled = hasPreview;

            MetadataCategoriesCountText.Text = $"{selectedCategoryCount.ToString(CultureInfo.InvariantCulture)} selected";
            MetadataSummaryFileCountText.Text = fileCount.ToString(CultureInfo.InvariantCulture);
            MetadataSummaryTagsFoundText.Text = totalBasicFields == 0 ? "—" : $"{totalBasicFields.ToString(CultureInfo.InvariantCulture)} basic";
            MetadataSummaryCategoryCountText.Text = selectedCategoryCount.ToString(CultureInfo.InvariantCulture);
            MetadataSummaryModeText.Text = _metadataScramblerMode;
            MetadataSummaryOutputText.Text = MetadataWriteSupportEnabled ? "Create cleaned copies" : "Preview only";
            MetadataSummaryStatusText.Text = BuildMetadataSummaryStatus(hasFiles, selectedCategoryCount, hasPreview);

            MetadataPreviewChangesButton.IsEnabled = canPreview;
            MetadataScrambleButton.IsEnabled = canScramble;

            Brush statusBrush = GetMetadataStatusBrush(MetadataSummaryStatusText.Text);
            MetadataSummaryStatusMarker.Background = statusBrush;
            MetadataSummaryStatusText.Foreground = statusBrush;
            MetadataPreviewStatusBadgeBorder.BorderBrush = hasPreview ? GetBrushResource("MetadataBrush") : GetBrushResource("AppBorderBrush");
            MetadataPreviewStatusBadgeIcon.Foreground = hasPreview ? GetBrushResource("MetadataBrush") : GetBrushResource("TextSecondaryBrush");
            MetadataPreviewStatusBadgeText.Foreground = hasPreview ? GetBrushResource("MetadataBrush") : GetBrushResource("TextSecondaryBrush");
        }

        private string BuildMetadataSummaryStatus(bool hasFiles, int selectedCategoryCount, bool hasPreview)
        {
            if (!hasFiles)
            {
                return "Waiting for files";
            }

            if (selectedCategoryCount == 0)
            {
                return "Select categories";
            }

            if (hasPreview)
            {
                return MetadataWriteSupportEnabled ? "Ready to scramble" : "Preview ready";
            }

            return "Ready to preview";
        }

        private void SetMetadataScramblerStatus(string status, bool updateGlobalStatus = true)
        {
            _metadataScramblerStatus = status;
            if (updateGlobalStatus)
            {
                SetStatus(status);
            }
        }

        private string ReadSelectedMetadataMode()
        {
            if (MetadataModeComboBox?.SelectedItem is ComboBoxItem item)
            {
                return item.Content?.ToString() ?? "Remove metadata";
            }

            return "Remove metadata";
        }

        private static Brush GetMetadataStatusBrush(string status)
        {
            return status switch
            {
                "Preview ready" or "Ready to scramble" => GetBrushResource("SuccessBrush"),
                "Ready to preview" => GetBrushResource("BrightBlueBrush"),
                "Select categories" => GetBrushResource("WarningBrush"),
                "Failed" => GetBrushResource("DangerBrush"),
                _ => GetBrushResource("TextSecondaryBrush")
            };
        }

        private string GetTimestampPreviewAction()
        {
            bool timestampsSelected = MetadataCategories.Any(category =>
                category.IsSelected &&
                IsBasicTimestampCategory(category.Name));

            if (!timestampsSelected)
            {
                return "Preserved";
            }

            return _metadataScramblerMode switch
            {
                "Randomize metadata" => MetadataWriteSupportEnabled ? "Would randomize in cleaned copy" : "Preview only",
                "Remove metadata" => MetadataWriteSupportEnabled ? "Would remove where supported" : "Preview only",
                _ => "Preview only"
            };
        }

        private string BuildMetadataPreviewReport(MetadataSelectedFileViewModel file)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Metadata Scrambler Preview");
            builder.AppendLine($"File: {file.DisplayName}");
            builder.AppendLine($"Type: {file.FileType}");
            builder.AppendLine($"Size: {file.SizeDisplay}");
            builder.AppendLine($"Mode: {_metadataScramblerMode}");
            builder.AppendLine("No files changed.");
            builder.AppendLine();
            foreach (MetadataPreviewItemViewModel item in MetadataPreviewItems)
            {
                builder.AppendLine($"{item.Label}: {item.BeforeValue} -> {item.AfterValue}");
            }

            return builder.ToString();
        }

        private static bool IsBasicTimestampCategory(string categoryName)
        {
            return string.Equals(categoryName, "Timestamps", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountBasicMetadataFields(FileInfo fileInfo)
        {
            int count = 0;
            if (!string.IsNullOrWhiteSpace(fileInfo.Name))
            {
                count++;
            }

            if (!string.IsNullOrWhiteSpace(fileInfo.Extension))
            {
                count++;
            }

            count += 4; // size, created, modified, attributes
            return count;
        }

        private static bool IsCommonMetadataFileType(string extension)
        {
            return extension is
                ".jpg" or
                ".jpeg" or
                ".png" or
                ".webp" or
                ".tif" or
                ".tiff" or
                ".heic" or
                ".bmp" or
                ".pdf" or
                ".docx" or
                ".xlsx" or
                ".pptx" or
                ".rtf" or
                ".txt" or
                ".mp4" or
                ".mov" or
                ".webm" or
                ".mkv" or
                ".mp3" or
                ".wav";
        }

        private static string GetMetadataFileTypeDisplay(string extension)
        {
            return extension switch
            {
                ".jpg" or ".jpeg" => "JPG Image",
                ".png" => "PNG Image",
                ".webp" => "WEBP Image",
                ".tif" or ".tiff" => "TIFF Image",
                ".heic" => "HEIC Image",
                ".bmp" => "BMP Image",
                ".pdf" => "PDF Document",
                ".docx" => "Word Document",
                ".xlsx" => "Excel Workbook",
                ".pptx" => "PowerPoint Deck",
                ".rtf" => "RTF Document",
                ".txt" => "Text File",
                ".mp4" => "MP4 Video",
                ".mov" => "MOV Video",
                ".webm" => "WEBM Video",
                ".mkv" => "MKV Video",
                ".mp3" => "MP3 Audio",
                ".wav" => "WAV Audio",
                "" => "Unknown File",
                _ => $"{extension.TrimStart('.').ToUpperInvariant()} File"
            };
        }

        private static string FormatMetadataDate(DateTime dateTime)
        {
            if (dateTime == DateTime.MinValue)
            {
                return "Unavailable";
            }

            return dateTime.ToString("g", CultureInfo.CurrentCulture);
        }
    }
}

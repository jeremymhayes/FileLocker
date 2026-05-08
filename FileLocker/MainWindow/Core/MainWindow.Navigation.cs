using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Windows.UI;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private enum AppSection
        {
            Dashboard,
            EncryptFiles,
            DecryptFiles,
            HashFiles,
            EncodeText,
            MetadataScrambler,
            SecureDelete,
            Settings,
            About
        }

        private sealed class DashboardStats
        {
            public string ProtectedFilesCount { get; init; } = "0";
            public string ProtectedFilesDeltaText { get; init; } = "Tracking starts now";
            public string ProtectedFilesSubtitle { get; init; } = "Files are encrypted";
            public string StorageSavedDisplay { get; init; } = "—";
            public string StorageSavedDeltaText { get; init; } = "Tracking starts now";
            public string StorageSavedSubtitle { get; init; } = "Storage tracking not available yet";
            public Brush StorageSavedAccentBrush { get; init; } = new SolidColorBrush(Colors.MediumAquamarine);
            public string LastOperationName { get; init; } = "No recent activity";
            public string LastOperationFileName { get; init; } = "Run an action to populate this card";
            public string LastOperationTimeDisplay { get; init; } = "Waiting for the next completed job";
            public string SecurityStatusTitle { get; init; } = "Secure";
            public string SecurityStatusSubtitle { get; init; } = "No issues detected";
            public string SecurityStatusDetail { get; init; } = "Local-only protection active";
            public Brush SecurityAccentBrush { get; init; } = new SolidColorBrush(Colors.MediumAquamarine);
            public Brush SecurityBackgroundBrush { get; init; } = new SolidColorBrush(Color.FromArgb(255, 19, 53, 59));
            public Brush LastOperationAccentBrush { get; init; } = new SolidColorBrush(Colors.DeepSkyBlue);
        }

        public sealed class DashboardRecentFileItem
        {
            public string Name { get; set; } = string.Empty;
            public string FileIconText { get; set; } = "FILE";
            public string Type { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string LastModified { get; set; } = string.Empty;
            public Brush StatusBrush { get; set; } = new SolidColorBrush(Colors.Transparent);
        }

        private AppSection _currentSection = AppSection.Dashboard;

        public ObservableCollection<DashboardRecentFileItem> DashboardRecentFiles { get; } = [];

        private void InitializeDashboardShell()
        {
            RefreshDashboardData();
            InitializeSettingsView();
            InitializeEncryptFilesView();
            InitializeDecryptFilesView();
            InitializeEncodeTextView();
            InitializeMetadataScramblerView();
            ApplyDashboardActionLayout(DashboardActionGrid.ActualWidth);
            NavigateToSection(AppSection.Dashboard, announce: false);
        }

        private void RefreshDashboardData()
        {
            RefreshDashboardRecentFiles();
            RefreshDashboardStats();
        }

        private void RefreshDashboardRecentFiles()
        {
            DashboardRecentFiles.Clear();

            IReadOnlyList<DashboardRecentFileItem> recentItems = BuildDashboardRecentFilesFromHistory();
            if (recentItems.Count == 0)
            {
                recentItems = LoadDashboardFallbackRecentFiles();
            }

            foreach (DashboardRecentFileItem item in recentItems)
            {
                DashboardRecentFiles.Add(item);
            }
        }

        private List<DashboardRecentFileItem> BuildDashboardRecentFilesFromHistory()
        {
            var items = new List<DashboardRecentFileItem>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (OperationHistoryEntry entry in _operationHistory.OrderByDescending(history => history.TimestampUtc))
            {
                foreach (FileOperationResult result in entry.Results)
                {
                    string displayPath = GetResultDisplayPath(result);
                    string fileName = Path.GetFileName(displayPath);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = entry.Operation;
                    }

                    if (!seenKeys.Add($"{entry.TimestampUtc:O}|{fileName}|{result.Status}"))
                    {
                        continue;
                    }

                    items.Add(new DashboardRecentFileItem
                    {
                        Name = fileName,
                        FileIconText = GetFileIconText(displayPath),
                        Type = GetFileTypeDisplay(displayPath),
                        Status = GetDashboardResultStatus(entry, result),
                        LastModified = FormatDashboardTimestamp(entry.TimestampUtc),
                        StatusBrush = GetDashboardStatusBrush(entry, result)
                    });

                    if (items.Count >= 5)
                    {
                        return items;
                    }
                }
            }

            return items;
        }

        private static List<DashboardRecentFileItem> LoadDashboardFallbackRecentFiles()
        {
            return
            [
                new DashboardRecentFileItem
                {
                    Name = "Quarterly_Report.pdf",
                    FileIconText = "PDF",
                    Type = "PDF Document",
                    Status = "Encrypted",
                    LastModified = "Sample activity",
                    StatusBrush = new SolidColorBrush(Colors.MediumTurquoise)
                },
                new DashboardRecentFileItem
                {
                    Name = "Budget_2024.xlsx",
                    FileIconText = "XLSX",
                    Type = "Excel Workbook",
                    Status = "Hashed",
                    LastModified = "Sample activity",
                    StatusBrush = new SolidColorBrush(Colors.DodgerBlue)
                },
                new DashboardRecentFileItem
                {
                    Name = "Project_Photos.zip",
                    FileIconText = "ZIP",
                    Type = "ZIP Archive",
                    Status = "Encrypted",
                    LastModified = "Sample activity",
                    StatusBrush = new SolidColorBrush(Colors.MediumTurquoise)
                },
                new DashboardRecentFileItem
                {
                    Name = "Client_Contract.docx",
                    FileIconText = "DOCX",
                    Type = "Word Document",
                    Status = "Metadata Scrambled",
                    LastModified = "Sample activity",
                    StatusBrush = new SolidColorBrush(Colors.MediumPurple)
                },
                new DashboardRecentFileItem
                {
                    Name = "Notes.txt",
                    FileIconText = "TXT",
                    Type = "Text Document",
                    Status = "Encoded",
                    LastModified = "Sample activity",
                    StatusBrush = new SolidColorBrush(Colors.DeepSkyBlue)
                }
            ];
        }

        private void RefreshDashboardStats()
        {
            DashboardStats stats = BuildDashboardStats();

            ProtectedFilesValueText.Text = stats.ProtectedFilesCount;
            ProtectedFilesSubtitleText.Text = stats.ProtectedFilesSubtitle;
            ProtectedFilesDeltaText.Text = stats.ProtectedFilesDeltaText;

            StorageSavedValueText.Text = stats.StorageSavedDisplay;
            StorageSavedSubtitleText.Text = stats.StorageSavedSubtitle;
            StorageSavedDeltaText.Text = stats.StorageSavedDeltaText;
            StorageSavedDeltaText.Foreground = stats.StorageSavedAccentBrush;
            StorageSavedIcon.Foreground = stats.StorageSavedAccentBrush;
            StorageSavedIconBorder.BorderBrush = stats.StorageSavedAccentBrush;

            LastOperationNameText.Text = stats.LastOperationName;
            LastOperationFileNameText.Text = stats.LastOperationFileName;
            LastOperationTimeText.Text = stats.LastOperationTimeDisplay;
            LastOperationIcon.Foreground = stats.LastOperationAccentBrush;
            LastOperationIconBorder.BorderBrush = stats.LastOperationAccentBrush;

            SecurityStatusTitleText.Text = stats.SecurityStatusTitle;
            SecurityStatusSubtitleText.Text = stats.SecurityStatusSubtitle;
            SecurityStatusDetailText.Text = stats.SecurityStatusDetail;
            SecurityStatusDetailText.Foreground = stats.SecurityAccentBrush;
            SecurityStatusIcon.Foreground = stats.SecurityAccentBrush;
            SecurityStatusIconBorder.BorderBrush = stats.SecurityAccentBrush;
            SecurityStatusIconBorder.Background = stats.SecurityBackgroundBrush;
        }

        private DashboardStats BuildDashboardStats()
        {
            int protectedFiles = _operationHistory
                .Where(entry => IsEncryptOperation(entry.Operation))
                .Sum(entry => entry.Results.Count(result => IsSuccessfulDashboardResult(result)));

            int protectedThisWeek = _operationHistory
                .Where(entry => IsEncryptOperation(entry.Operation) && IsThisWeek(entry.TimestampUtc))
                .Sum(entry => entry.Results.Count(result => IsSuccessfulDashboardResult(result)));

            string protectedDelta = protectedThisWeek > 0
                ? $"+{protectedThisWeek} this week"
                : "Tracking starts now";

            var storage = CalculateStorageSavings();
            string storageValue = storage.HasTrackedStorage
                ? storage.TotalSavedBytes > 0
                    ? $"Saved {FormatDashboardFileSize(storage.TotalSavedBytes)}"
                    : storage.TotalAddedBytes > 0
                        ? $"Increased {FormatDashboardFileSize(storage.TotalAddedBytes)}"
                        : "No savings"
                : "—";
            string storageSubtitle = storage.HasTrackedStorage
                ? "Compression payload savings before encryption"
                : "Storage tracking not available yet";
            string storageDelta = storage.HasTrackedStorage
                ? storage.ThisWeekSavedBytes > 0
                    ? $"Saved {FormatDashboardFileSize(storage.ThisWeekSavedBytes)} this week"
                    : storage.ThisWeekAddedBytes > 0
                        ? $"Increased {FormatDashboardFileSize(storage.ThisWeekAddedBytes)} this week"
                        : "No net savings this week"
                : "Tracking starts now";
            Brush storageAccent = new SolidColorBrush(storage.TotalSavedBytes > 0
                ? Colors.MediumAquamarine
                : storage.TotalAddedBytes > 0
                    ? Colors.Goldenrod
                    : Colors.DeepSkyBlue);

            OperationHistoryEntry? latestEntry = _operationHistory.FirstOrDefault();
            FileOperationResult? latestResult = latestEntry?.Results.FirstOrDefault();
            string lastOperationName = latestEntry == null
                ? "No recent activity"
                : GetDashboardOperationDisplay(latestEntry, latestResult);
            string lastOperationFileName = latestResult == null
                ? "Run an action to populate this card"
                : Path.GetFileName(GetResultDisplayPath(latestResult));
            if (string.IsNullOrWhiteSpace(lastOperationFileName))
            {
                lastOperationFileName = latestEntry == null ? "Run an action to populate this card" : "Multiple files";
            }

            bool queueHasIssues = FileList.Any(item =>
                string.Equals(item.Status, "Needs attention", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));
            bool recentRunHasIssues = latestEntry != null && (latestEntry.Cancelled || latestEntry.FailureCount > 0);
            bool needsAttention = queueHasIssues || recentRunHasIssues;

            return new DashboardStats
            {
                ProtectedFilesCount = protectedFiles.ToString(CultureInfo.InvariantCulture),
                ProtectedFilesDeltaText = protectedDelta,
                ProtectedFilesSubtitle = protectedFiles == 0 ? "Protected file history will appear here" : "Files are encrypted",
                StorageSavedDisplay = storageValue,
                StorageSavedDeltaText = storageDelta,
                StorageSavedSubtitle = storageSubtitle,
                StorageSavedAccentBrush = storageAccent,
                LastOperationName = lastOperationName,
                LastOperationFileName = lastOperationFileName,
                LastOperationTimeDisplay = latestEntry == null
                    ? "Waiting for the next completed job"
                    : FormatDashboardTimestamp(latestEntry.TimestampUtc),
                SecurityStatusTitle = needsAttention ? "Needs attention" : "Secure",
                SecurityStatusSubtitle = queueHasIssues
                    ? "Queued item needs review"
                    : recentRunHasIssues
                        ? "Recent activity reported an issue"
                        : "No issues detected",
                SecurityStatusDetail = queueHasIssues
                    ? $"{FileList.Count(item => string.Equals(item.Status, "Needs attention", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))} queued item(s) need attention"
                    : recentRunHasIssues
                        ? "Check the latest job details in Settings."
                        : "Local-only protection active",
                SecurityAccentBrush = new SolidColorBrush(needsAttention ? Colors.IndianRed : Colors.MediumAquamarine),
                SecurityBackgroundBrush = new SolidColorBrush(needsAttention
                    ? Color.FromArgb(255, 56, 21, 31)
                    : Color.FromArgb(255, 19, 53, 59)),
                LastOperationAccentBrush = new SolidColorBrush(needsAttention ? Colors.IndianRed : Colors.DeepSkyBlue)
            };
        }

        private (
            long TotalSavedBytes,
            long TotalAddedBytes,
            long ThisWeekSavedBytes,
            long ThisWeekAddedBytes,
            bool HasTrackedStorage,
            int TrackedFiles,
            int CompressionRequested,
            int CompressionApplied) CalculateStorageSavings()
        {
            long totalSaved = 0;
            long totalAdded = 0;
            long savedThisWeek = 0;
            long addedThisWeek = 0;
            bool hasTrackedStorage = false;
            int trackedFiles = 0;
            int compressionRequested = 0;
            int compressionApplied = 0;

            foreach (OperationHistoryEntry entry in _operationHistory.Where(history => IsEncryptOperation(history.Operation)))
            {
                foreach (FileOperationResult result in entry.Results.Where(IsSuccessfulDashboardResult))
                {
                    if (!TryGetTrackedStorageDeltaBytes(result, out long storageDeltaBytes))
                    {
                        continue;
                    }

                    hasTrackedStorage = true;
                    trackedFiles++;
                    if (result.CompressionRequested)
                    {
                        compressionRequested++;
                    }

                    if (result.CompressionApplied)
                    {
                        compressionApplied++;
                    }

                    if (storageDeltaBytes >= 0)
                    {
                        totalSaved += storageDeltaBytes;
                        if (IsThisWeek(entry.TimestampUtc))
                        {
                            savedThisWeek += storageDeltaBytes;
                        }
                    }
                    else
                    {
                        long addedBytes = Math.Abs(storageDeltaBytes);
                        totalAdded += addedBytes;
                        if (IsThisWeek(entry.TimestampUtc))
                        {
                            addedThisWeek += addedBytes;
                        }
                    }
                }
            }

            return (totalSaved, totalAdded, savedThisWeek, addedThisWeek, hasTrackedStorage, trackedFiles, compressionRequested, compressionApplied);
        }

        private async void StorageSavedDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            var storage = CalculateStorageSavings();
            if (!storage.HasTrackedStorage)
            {
                await ShowInfoDialogAsync(
                    "Compression accounting starts after a completed encryption run with compression enabled and available compressed payload sizes.",
                    "Storage Impact Details");
                return;
            }

            string message =
                $"Compression-tracked files: {storage.TrackedFiles}\n" +
                $"Compression savings: {FormatDashboardFileSize(storage.TotalSavedBytes)}\n" +
                $"Compression increase: {FormatDashboardFileSize(storage.TotalAddedBytes)}\n" +
                $"This week: {(storage.ThisWeekSavedBytes > 0 ? FormatDashboardFileSize(storage.ThisWeekSavedBytes) + " saved" : storage.ThisWeekAddedBytes > 0 ? FormatDashboardFileSize(storage.ThisWeekAddedBytes) + " increased" : "No savings")}\n" +
                $"Compressed files: {storage.CompressionApplied}/{storage.CompressionRequested}";

            if (storage.TotalAddedBytes > 0 && storage.TotalSavedBytes == 0)
            {
                message += $"\nCompression increased size by {FormatDashboardFileSize(storage.TotalAddedBytes)} before encryption.";
            }

            await ShowInfoDialogAsync(message, "Storage Impact Details");
        }

        private static bool TryGetTrackedStorageDeltaBytes(FileOperationResult result, out long storageDeltaBytes)
        {
            storageDeltaBytes = 0;

            if (!result.CompressionRequested || result.OriginalSizeBytes is not long originalSizeBytes)
            {
                return false;
            }

            if (result.CompressedSizeBytes is long compressedSizeBytes)
            {
                storageDeltaBytes = originalSizeBytes - compressedSizeBytes;
                return true;
            }

            if (result.EstimatedCompressedSizeBytes is long estimatedCompressedSizeBytes)
            {
                storageDeltaBytes = originalSizeBytes - estimatedCompressedSizeBytes;
                return true;
            }

            return false;
        }

        private static bool IsPathRedacted(string? path) =>
            string.IsNullOrWhiteSpace(path) ||
            path.Contains("[redacted]", StringComparison.OrdinalIgnoreCase);

        private static bool IsSuccessfulDashboardResult(FileOperationResult result) =>
            string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Status, "Verified", StringComparison.OrdinalIgnoreCase);

        private static bool IsEncryptOperation(string operation) =>
            string.Equals(operation, "Encrypt", StringComparison.OrdinalIgnoreCase);

        private static string GetDashboardOperationDisplay(OperationHistoryEntry entry, FileOperationResult? result)
        {
            if (result != null && string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Failed";
            }

            if (entry.Cancelled)
            {
                return "Cancelled";
            }

            return entry.Operation.ToLowerInvariant() switch
            {
                "encrypt" => "Encrypted",
                "decrypt" => "Decrypted",
                "verify" => "Verified",
                _ => entry.Operation
            };
        }

        private static string GetDashboardResultStatus(OperationHistoryEntry entry, FileOperationResult result)
        {
            if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Failed";
            }

            if (entry.Cancelled)
            {
                return "Cancelled";
            }

            return GetDashboardOperationDisplay(entry, result);
        }

        private static Brush GetDashboardStatusBrush(OperationHistoryEntry entry, FileOperationResult result)
        {
            if (string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) || entry.Cancelled)
            {
                return new SolidColorBrush(Colors.IndianRed);
            }

            return entry.Operation.ToLowerInvariant() switch
            {
                "encrypt" => new SolidColorBrush(Colors.MediumTurquoise),
                "decrypt" => new SolidColorBrush(Colors.DeepSkyBlue),
                "verify" => new SolidColorBrush(Colors.DodgerBlue),
                _ => new SolidColorBrush(Colors.MediumPurple)
            };
        }

        private static string GetResultDisplayPath(FileOperationResult result) =>
            string.IsNullOrWhiteSpace(result.OutputPath)
                ? result.SourcePath
                : result.OutputPath;

        private static string GetFileTypeDisplay(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => "PDF Document",
                ".doc" or ".docx" => "Word Document",
                ".xls" or ".xlsx" => "Excel Workbook",
                ".zip" => "ZIP Archive",
                ".txt" => "Text Document",
                ".png" => "PNG Image",
                ".locked" => "Locked File",
                "" => "File",
                _ => $"{extension.TrimStart('.').ToUpperInvariant()} File"
            };
        }

        private static string GetFileIconText(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return "DIR";
            }

            string extension = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(extension))
            {
                return "FILE";
            }

            return extension.Length <= 4
                ? extension
                : extension[..4];
        }

        private static bool IsThisWeek(DateTime timestampUtc)
        {
            DateTime localTimestamp = timestampUtc.ToLocalTime();
            DateTime now = DateTime.Now;
            DayOfWeek firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            int diff = (7 + (now.DayOfWeek - firstDay)) % 7;
            DateTime weekStart = now.Date.AddDays(-diff);
            return localTimestamp >= weekStart;
        }

        private static string FormatDashboardTimestamp(DateTime timestampUtc)
        {
            DateTime local = timestampUtc.ToLocalTime();
            DateTime today = DateTime.Today;
            if (local.Date == today)
            {
                return $"Today, {local:t}";
            }

            if (local.Date == today.AddDays(-1))
            {
                return $"Yesterday, {local:t}";
            }

            return local.ToString("g", CultureInfo.CurrentCulture);
        }

        private static string FormatDashboardFileSize(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double length = bytes;
            int order = 0;
            while (length >= 1024 && order < sizes.Length - 1)
            {
                order++;
                length /= 1024;
            }

            return $"{length:0.#} {sizes[order]}";
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag ||
                !Enum.TryParse(tag, ignoreCase: true, out AppSection section))
            {
                return;
            }

            NavigateToSection(section);
        }

        private void NavigateToSection(AppSection section, bool announce = true)
        {
            _currentSection = section;
            DashboardView.Visibility = section == AppSection.Dashboard ? Visibility.Visible : Visibility.Collapsed;
            EncryptFilesView.Visibility = section == AppSection.EncryptFiles ? Visibility.Visible : Visibility.Collapsed;
            DecryptFilesView.Visibility = section == AppSection.DecryptFiles ? Visibility.Visible : Visibility.Collapsed;
            HashFilesView.Visibility = section == AppSection.HashFiles ? Visibility.Visible : Visibility.Collapsed;
            EncodeTextView.Visibility = section == AppSection.EncodeText ? Visibility.Visible : Visibility.Collapsed;
            MetadataScramblerView.Visibility = section == AppSection.MetadataScrambler ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = section == AppSection.Settings ? Visibility.Visible : Visibility.Collapsed;
            WorkflowView.Visibility = section is AppSection.SecureDelete
                ? Visibility.Visible
                : Visibility.Collapsed;
            AboutView.Visibility = section == AppSection.About ? Visibility.Visible : Visibility.Collapsed;
            DashboardStatusColumn.Visibility = section == AppSection.Dashboard ? Visibility.Visible : Visibility.Collapsed;
            HashFilesSideColumn.Visibility = section == AppSection.HashFiles ? Visibility.Visible : Visibility.Collapsed;
            EncodeTextSideColumn.Visibility = section == AppSection.EncodeText ? Visibility.Visible : Visibility.Collapsed;
            MetadataScramblerSideColumn.Visibility = section == AppSection.MetadataScrambler ? Visibility.Visible : Visibility.Collapsed;
            EncryptSideColumn.Visibility = section == AppSection.EncryptFiles ? Visibility.Visible : Visibility.Collapsed;
            DecryptSideColumn.Visibility = section == AppSection.DecryptFiles ? Visibility.Visible : Visibility.Collapsed;
            SettingsSideColumn.Visibility = section == AppSection.Settings ? Visibility.Visible : Visibility.Collapsed;

            UpdateNavigationSelection();
            UpdateSectionHeader(section);
            UpdateHeaderActionVisibility(section);
            UpdateMainContentLayout(section);

            switch (section)
            {
                case AppSection.EncryptFiles:
                    PrepareEncryptFilesSection();
                    if (announce)
                    {
                        SetStatus("Review queued items, then encrypt, decrypt, verify, or rotate access.");
                    }
                    break;
                case AppSection.DecryptFiles:
                    PrepareDecryptFilesSection();
                    if (announce)
                    {
                        SetStatus("Add FileLocker encrypted files and enter the original password.");
                    }
                    break;
                case AppSection.HashFiles:
                    PrepareHashFilesSection();
                    if (announce)
                    {
                        SetStatus("Select a file to generate or verify a hash.");
                    }
                    break;
                case AppSection.EncodeText:
                    PrepareEncodeTextSection();
                    if (announce)
                    {
                        SetStatus("Text encoding helper ready.");
                    }
                    break;
                case AppSection.MetadataScrambler:
                    PrepareMetadataScramblerSection();
                    if (announce)
                    {
                        SetStatus("Metadata Scrambler preview is ready.");
                    }
                    break;
                case AppSection.SecureDelete:
                    PrepareEncryptFilesSection();
                    if (announce)
                    {
                        SetStatus("Secure delete controls are available in the setup panel.");
                    }
                    break;
                case AppSection.Settings:
                    PrepareSettingsSection();
                    if (announce)
                    {
                        SetStatus("Settings are ready.");
                    }
                    break;
                case AppSection.About:
                    UpdateAboutMenuInfo();
                    if (announce)
                    {
                        SetStatus("About FileLocker.");
                    }
                    break;
                default:
                    if (announce)
                    {
                        SetStatus("Dashboard ready.");
                    }
                    break;
            }

            ContentScrollViewer.ChangeView(null, 0, null, true);
        }

        private void DashboardActionGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyDashboardActionLayout(e.NewSize.Width);
        }

        private void ApplyDashboardActionLayout(double width)
        {
            if (DashboardActionGrid == null)
            {
                return;
            }

            PlaceActionCard(EncryptActionButton, 0, 0);
            PlaceActionCard(DecryptActionButton, 0, 1);
            PlaceActionCard(HashActionButton, 0, 2);
            PlaceActionCard(EncodeActionButton, 0, 3);
            PlaceActionCard(MetadataActionButton, 0, 4);
            PlaceActionCard(DeleteActionButton, 0, 5);
        }

        private static void PlaceActionCard(FrameworkElement element, int row, int column, int columnSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
        }

        private void UpdateNavigationSelection()
        {
            ApplyNavigationStyle(DashboardNavButton, _currentSection == AppSection.Dashboard);
            ApplyNavigationStyle(EncryptFilesNavButton, _currentSection == AppSection.EncryptFiles);
            ApplyNavigationStyle(DecryptFilesNavButton, _currentSection == AppSection.DecryptFiles);
            ApplyNavigationStyle(HashFilesNavButton, _currentSection == AppSection.HashFiles);
            ApplyNavigationStyle(EncodeTextNavButton, _currentSection == AppSection.EncodeText);
            ApplyNavigationStyle(MetadataScramblerNavButton, _currentSection == AppSection.MetadataScrambler);
            ApplyNavigationStyle(SecureDeleteNavButton, _currentSection == AppSection.SecureDelete);
            ApplyNavigationStyle(SettingsButton, _currentSection == AppSection.Settings);
            ApplyNavigationStyle(AboutNavButton, _currentSection == AppSection.About);
        }

        private void ApplyNavigationStyle(Button button, bool isSelected)
        {
            string key = isSelected ? "SidebarNavButtonSelectedStyle" : "SidebarNavButtonStyle";
            if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Style style)
            {
                button.Style = style;
            }
        }

        private void UpdateSectionHeader(AppSection section)
        {
            switch (section)
            {
                case AppSection.EncryptFiles:
                    PageTitleText.Text = "Encrypt Files";
                    PageSubtitleText.Text = "Secure files and folders with strong local encryption.";
                    break;
                case AppSection.DecryptFiles:
                    PageTitleText.Text = "Decrypt Files";
                    PageSubtitleText.Text = "Restore encrypted files using the original password.";
                    break;
                case AppSection.HashFiles:
                    PageTitleText.Text = "Hash Files";
                    PageSubtitleText.Text = "Generate and verify cryptographic hashes for your files.";
                    break;
                case AppSection.EncodeText:
                    PageTitleText.Text = "Encode Text";
                    PageSubtitleText.Text = "Encode and decode text safely for common formats.";
                    break;
                case AppSection.MetadataScrambler:
                    PageTitleText.Text = "Metadata Scrambler";
                    PageSubtitleText.Text = "Remove or randomize hidden file metadata before sharing.";
                    break;
                case AppSection.SecureDelete:
                    PageTitleText.Text = "Secure Delete";
                    PageSubtitleText.Text = "Configure best-effort cleanup and file removal after successful local processing.";
                    break;
                case AppSection.Settings:
                    PageTitleText.Text = "Settings";
                    PageSubtitleText.Text = "Configure FileLocker to match your workflow.";
                    break;
                case AppSection.About:
                    PageTitleText.Text = "About";
                    PageSubtitleText.Text = "Free local file security, helper guides, and update details.";
                    break;
                default:
                    PageTitleText.Text = "Dashboard";
                    PageSubtitleText.Text = "Start local file security workflows and review recent activity.";
                    break;
            }
        }

        private void PrepareEncryptFilesSection()
        {
            SetComboSelection(OperationModeCombo, "Encrypt / Decrypt");
            ConfigureModeOptions();
            ApplyInspectorView("Setup");
            SynchronizeEncryptOptionsFromWorkflow();
            RefreshEncryptFilesState();
        }

        private void PrepareHashOrEncodeSection(string algorithm)
        {
            SetComboSelection(OperationModeCombo, "Hash / Encode");
            ConfigureModeOptions();
            SetComboSelection(AlgorithmCombo, algorithm);
            UpdateKeySizeInteractivity();
            UpdateAlgorithmHelper();
            ApplyInspectorView("Setup");
        }

        private void PrepareSettingsSection()
        {
            LoadSettingsPreferences();
            RefreshDashboardData();
        }

        private void UpdateHeaderActionVisibility(AppSection section)
        {
            bool showDashboardActions = section == AppSection.Dashboard;
            bool showSettingsActions = section == AppSection.Settings;
            bool showEncryptActions = section == AppSection.EncryptFiles;
            bool showDecryptActions = section == AppSection.DecryptFiles;
            bool showHashActions = section == AppSection.HashFiles;
            bool showEncodeActions = section == AppSection.EncodeText;
            bool showMetadataActions = section == AppSection.MetadataScrambler;
            ResetDefaultsHeaderButton.Visibility = showSettingsActions ? Visibility.Visible : Visibility.Collapsed;
            DashboardActivityHeaderButton.Visibility = showDashboardActions ? Visibility.Visible : Visibility.Collapsed;
            DashboardSecurityHeaderButton.Visibility = showDashboardActions ? Visibility.Visible : Visibility.Collapsed;
            DashboardSettingsHeaderButton.Visibility = showDashboardActions ? Visibility.Visible : Visibility.Collapsed;
            EncryptionGuideHeaderButton.Visibility = showEncryptActions ? Visibility.Visible : Visibility.Collapsed;
            EncryptHistoryHeaderButton.Visibility = showEncryptActions ? Visibility.Visible : Visibility.Collapsed;
            DecryptionGuideHeaderButton.Visibility = showDecryptActions ? Visibility.Visible : Visibility.Collapsed;
            DecryptHistoryHeaderButton.Visibility = showDecryptActions ? Visibility.Visible : Visibility.Collapsed;
            HashGuideHeaderButton.Visibility = showHashActions ? Visibility.Visible : Visibility.Collapsed;
            HashCopyResultsHeaderButton.Visibility = showHashActions ? Visibility.Visible : Visibility.Collapsed;
            EncodingGuideHeaderButton.Visibility = showEncodeActions ? Visibility.Visible : Visibility.Collapsed;
            EncodeCopyOutputHeaderButton.Visibility = showEncodeActions ? Visibility.Visible : Visibility.Collapsed;
            MetadataGuideHeaderButton.Visibility = showMetadataActions ? Visibility.Visible : Visibility.Collapsed;
            MetadataReportHeaderButton.Visibility = showMetadataActions ? Visibility.Visible : Visibility.Collapsed;
            QuickStartHeaderButton.Visibility = showSettingsActions || showEncryptActions || showDecryptActions || showHashActions || showEncodeActions || showMetadataActions ? Visibility.Collapsed : Visibility.Visible;
            AboutButton.Visibility = showSettingsActions || showEncryptActions || showDecryptActions || showHashActions || showEncodeActions || showMetadataActions ? Visibility.Collapsed : Visibility.Visible;
            RefreshDecryptFilesState();
            RefreshHashFilesState();
            RefreshEncodeTextState();
            RefreshMetadataScramblerState();
        }

        private async void DashboardSecurityHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            DashboardStats stats = BuildDashboardStats();
            string message =
                $"{stats.SecurityStatusTitle}\n\n" +
                $"{stats.SecurityStatusSubtitle}\n" +
                $"{stats.SecurityStatusDetail}\n\n" +
                "FileLocker processes files on this device. Keep passwords safe because encrypted files require the original password.";

            await ShowInfoDialogAsync(message, "Security Status");
        }

        private void UpdateMainContentLayout(AppSection section)
        {
            if (MainContentHostGrid == null)
            {
                return;
            }

            if (section == AppSection.Settings)
            {
                MainContentHostGrid.MaxWidth = 1260;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 28;
                MainContentHostGrid.Padding = new Thickness(32, 24, 32, 30);
                SideContentColumn.Width = new GridLength(356);
            }
            else if (section is AppSection.EncryptFiles or AppSection.DecryptFiles)
            {
                MainContentHostGrid.MaxWidth = 1480;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 24;
                MainContentHostGrid.Padding = new Thickness(24, 20, 24, 30);
                SideContentColumn.Width = new GridLength(384);
            }
            else if (section == AppSection.HashFiles)
            {
                MainContentHostGrid.MaxWidth = 1440;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 24;
                MainContentHostGrid.Padding = new Thickness(24, 20, 24, 30);
                SideContentColumn.Width = new GridLength(420);
            }
            else if (section == AppSection.EncodeText)
            {
                MainContentHostGrid.MaxWidth = 1480;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 24;
                MainContentHostGrid.Padding = new Thickness(24, 20, 24, 30);
                SideContentColumn.Width = new GridLength(430);
            }
            else if (section == AppSection.MetadataScrambler)
            {
                MainContentHostGrid.MaxWidth = 1480;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 24;
                MainContentHostGrid.Padding = new Thickness(24, 20, 24, 30);
                SideContentColumn.Width = new GridLength(430);
            }
            else if (section == AppSection.Dashboard)
            {
                MainContentHostGrid.MaxWidth = double.PositiveInfinity;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 28;
                MainContentHostGrid.Padding = new Thickness(32, 24, 32, 30);
                SideContentColumn.Width = new GridLength(356);
            }
            else
            {
                MainContentHostGrid.MaxWidth = double.PositiveInfinity;
                MainContentHostGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainContentHostGrid.ColumnSpacing = 0;
                MainContentHostGrid.Padding = new Thickness(32, 24, 32, 30);
                SideContentColumn.Width = new GridLength(0);
            }
        }
    }
}

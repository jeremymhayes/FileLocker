using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private bool _isSyncingEncryptFilesUi;

        private static string GetSameSourceEncryptOutputLabel(bool hasFolderSelections)
        {
            return hasFolderSelections ? "Inside source folder tree" : "Next to source files";
        }

        private void InitializeEncryptFilesView()
        {
            EncryptSelectedFilesListView.ItemsSource = FileList;
            EncryptAlgorithmCombo.SelectedIndex = 0;
            SynchronizeEncryptOptionsFromWorkflow();
            RefreshEncryptFilesState();
        }

        private void SynchronizeEncryptOptionsFromWorkflow()
        {
            _isSyncingEncryptFilesUi = true;
            try
            {
                bool saveNextToSource = OutputCustomLocationRadio.IsChecked != true;
                bool hasFolderSelections = FileList.Any(item => item.SourceRootIsFolder);
                EncryptSaveNextToSourceToggle.IsOn = saveNextToSource;
                EncryptCompressBeforeEncryptionToggle.IsOn = CompressModeToggle.IsOn;
                EncryptDeleteOriginalsToggle.IsOn = RemoveOriginalsToggle.IsOn;
                EncryptPreserveFolderStructureToggle.IsOn = PackageFoldersToggle.IsOn;
                EncryptOutputLocationBox.Text = saveNextToSource
                    ? GetSameSourceEncryptOutputLabel(hasFolderSelections)
                    : string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text)
                        ? GetDefaultEncryptOutputFolder()
                        : EncryptOutputFolderBox.Text.Trim();
            }
            finally
            {
                _isSyncingEncryptFilesUi = false;
            }
        }

        private void RefreshEncryptFilesState()
        {
            int fileCount = FileList.Count;
            long totalSize = FileList.Sum(item => item.SizeBytes);

            SelectedFilesTitleText.Text = $"Selected Files ({fileCount})";
            EncryptSummaryFilesText.Text = fileCount.ToString(CultureInfo.InvariantCulture);
            EncryptSummarySizeText.Text = FormatFileSize(totalSize);
            bool hasFolderSelections = FileList.Any(item => item.SourceRootIsFolder);
            EncryptSummaryOutputText.Text = OutputCustomLocationRadio.IsChecked == true
                ? string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text)
                    ? "Choose output folder"
                    : Path.GetFileName(EncryptOutputFolderBox.Text.Trim())
                : hasFolderSelections ? "Inside source tree" : "Next to source files";
            EncryptSummaryModeText.Text = "AES-256-GCM";

            EncryptSelectedFilesEmptyState.Visibility = fileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            EncryptSelectedFilesListView.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            EncryptImportantWarningPanel.Visibility = _isEncryptImportantDismissedThisSession
                ? Visibility.Collapsed
                : Visibility.Visible;

            (string statusText, Brush statusBrush, string statusGlyph) = GetEncryptOperationStatus();
            EncryptSummaryStatusText.Text = statusText;
            EncryptSummaryStatusText.Foreground = statusBrush;
            EncryptSummaryStatusIcon.Foreground = statusBrush;
            EncryptSummaryStatusIcon.Glyph = statusGlyph;

            bool canStart = CanStartEncryptFiles();
            StartEncryptionButton.IsEnabled = canStart;
            EncryptPanelClearSelectionButton.Visibility = fileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            EncryptClearSelectionButton.IsEnabled = fileCount > 0 && !_isProcessing;
            EncryptPanelClearSelectionButton.IsEnabled = fileCount > 0 && !_isProcessing;
        }

        private (string StatusText, Brush StatusBrush, string StatusGlyph) GetEncryptOperationStatus()
        {
            Brush neutral = GetBrushResource("TextSecondaryBrush");
            Brush success = GetBrushResource("SuccessBrush");
            Brush danger = GetBrushResource("DangerBrush");
            Brush accent = GetBrushResource("BrightBlueBrush");

            if (_isProcessing)
            {
                return ("Encrypting", accent, "\uE768");
            }

            if (FileList.Count == 0)
            {
                return ("Waiting for files", neutral, "\uE9CE");
            }

            if (FileList.Any(item => string.Equals(item.Status, "Needs attention", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return ("Failed", danger, "\uE783");
            }

            if (FileList.All(item => string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase)))
            {
                return ("Complete", success, "\uE73E");
            }

            if (string.IsNullOrWhiteSpace(EncryptPasswordBox.Password))
            {
                return ("Password required", neutral, "\uE72E");
            }

            if (!PasswordsMatch())
            {
                return ("Passwords do not match", danger, "\uE783");
            }

            if (CalculatePasswordStrength(EncryptPasswordBox.Password).Score < StrongPasswordMinimumScore)
            {
                return ("Use a stronger password", danger, "\uE783");
            }

            if (OutputCustomLocationRadio.IsChecked == true && string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text))
            {
                return ("Choose output folder", neutral, "\uE8B7");
            }

            return ("Ready to encrypt", success, "\uE73E");
        }

        private bool CanStartEncryptFiles()
        {
            return !_isProcessing &&
                FileList.Count > 0 &&
                !string.IsNullOrWhiteSpace(EncryptPasswordBox.Password) &&
                PasswordsMatch() &&
                CalculatePasswordStrength(EncryptPasswordBox.Password).Score >= StrongPasswordMinimumScore &&
                (OutputCustomLocationRadio.IsChecked != true || !string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text));
        }

        private void EncryptImportantDismissButton_Click(object sender, RoutedEventArgs e)
        {
            _isEncryptImportantDismissedThisSession = true;
            EncryptImportantWarningPanel.Visibility = Visibility.Collapsed;
        }

        private bool PasswordsMatch()
        {
            return string.Equals(EncryptPasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal);
        }

        private void UpdateEncryptPasswordFeedback()
        {
            PasswordStrengthResult evaluation = CalculatePasswordStrength(EncryptPasswordBox.Password);
            EncryptPasswordStrengthBar.Value = evaluation.Score;
            EncryptPasswordStrengthBar.Foreground = new SolidColorBrush(evaluation.BarColor);

            string strengthLabel = evaluation.Score < 35
                ? "Weak"
                : evaluation.Score < StrongPasswordMinimumScore
                    ? "Fair"
                    : "Strong";
            EncryptPasswordStrengthText.Text = $"Password Strength: {strengthLabel}";

            if (string.IsNullOrEmpty(ConfirmPasswordBox.Password))
            {
                EncryptPasswordMatchText.Text = "Passwords must match before encryption can start.";
                EncryptPasswordMatchText.Foreground = GetBrushResource("TextSecondaryBrush");
            }
            else if (PasswordsMatch())
            {
                EncryptPasswordMatchText.Text = "Passwords match.";
                EncryptPasswordMatchText.Foreground = GetBrushResource("SuccessBrush");
            }
            else
            {
                EncryptPasswordMatchText.Text = "Passwords do not match.";
                EncryptPasswordMatchText.Foreground = GetBrushResource("DangerBrush");
            }

            RefreshEncryptFilesState();
        }

        private async void EncryptBrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            await BrowseFiles();
            RefreshEncryptFilesState();
        }

        private async void EncryptBrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await BrowseFolder();
            RefreshEncryptFilesState();
        }

        private void EncryptPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdateEncryptPasswordFeedback();
        }

        private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdateEncryptPasswordFeedback();
        }

        private async void StartEncryptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateEncryptFilesForm(showDialog: true))
            {
                RefreshEncryptFilesState();
                return;
            }

            PasswordBox.Password = EncryptPasswordBox.Password;
            if (!await ValidateInputAsync(ProcessingIntent.Encrypt))
            {
                RefreshEncryptFilesState();
                return;
            }

            if (!await EnsureEncryptOutputDestinationAsync())
            {
                SynchronizeEncryptOptionsFromWorkflow();
                RefreshEncryptFilesState();
                return;
            }

            if (EncryptDeleteOriginalsToggle.IsOn &&
                !await ConfirmSourceDeletionAsync(ProcessingIntent.Encrypt, FileList.Count))
            {
                SetStatus("Encryption cancelled before deleting originals was confirmed.");
                RefreshEncryptFilesState();
                return;
            }

            await ProcessFilesAsync(ProcessingIntent.Encrypt);
            if (FileList.Count > 0 && FileList.All(item => string.Equals(item.Status, "Completed", StringComparison.OrdinalIgnoreCase)))
            {
                ClearEncryptPasswordFields();
            }

            RefreshEncryptFilesState();
        }

        private bool ValidateEncryptFilesForm(bool showDialog)
        {
            string? message = null;
            if (FileList.Count == 0)
            {
                message = "Please select files to encrypt.";
            }
            else if (string.IsNullOrWhiteSpace(EncryptPasswordBox.Password))
            {
                message = "Please enter a password.";
            }
            else if (!PasswordsMatch())
            {
                message = "Password and confirmation must match before encryption can start.";
            }
            else if (CalculatePasswordStrength(EncryptPasswordBox.Password).Score < StrongPasswordMinimumScore)
            {
                message = "Use a stronger password before encrypting. Include length, upper/lowercase letters, numbers, and symbols.";
            }
            else if (OutputCustomLocationRadio.IsChecked == true && string.IsNullOrWhiteSpace(EncryptOutputFolderBox.Text))
            {
                message = "Choose an output folder or save output next to source files.";
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

        private void EncryptClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            ClearListButton_Click(sender, e);
            ClearEncryptPasswordFields();
            RefreshEncryptFilesState();
        }

        private void ClearEncryptPasswordFields()
        {
            EncryptPasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;
            PasswordBox.Password = string.Empty;
            UpdateEncryptPasswordFeedback();
        }

        private async void EncryptDropPanel_DragOver(object sender, DragEventArgs e)
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
                EncryptDropLabel.Text = items.Count > 0
                    ? $"Release {items.Count} item(s) to add them"
                    : "Release to add files";
                EncryptDropHintText.Text = "Dropped items will be added to the local encryption queue.";
                SetEncryptDropVisual(true);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void EncryptDropPanel_Drop(object sender, DragEventArgs e)
        {
            SetEncryptDropVisual(false);
            ResetEncryptDropText();

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                await ProcessDroppedFilesAsync(e.DataView);
                RefreshEncryptFilesState();
            }
        }

        private void EncryptDropPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SetEncryptDropVisual(true);
        }

        private void EncryptDropPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            SetEncryptDropVisual(false);
            ResetEncryptDropText();
        }

        private void SetEncryptDropVisual(bool active)
        {
            EncryptDropPanel.Background = active
                ? GetBrushResource("DropPanelActiveBrush")
                : GetBrushResource("HeroSurfaceBrush");
            EncryptDropPanel.BorderBrush = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            EncryptDropPanelDashBorder.Stroke = active
                ? GetBrushResource("AccentBrush")
                : GetBrushResource("DropPanelBorderBrush");
            EncryptDropIconTile.Background = active
                ? GetBrushResource("PrimaryActionBrush")
                : GetBrushResource("AccentSoftBrush");
        }

        private void ResetEncryptDropText()
        {
            EncryptDropLabel.Text = "Drag & drop files or folders to encrypt";
            EncryptDropHintText.Text = "Supports files, folders, and batch encryption";
        }

        private async void EncryptOutputBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker picker = CreateFolderPicker(PickerLocationId.DocumentsLibrary);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            _isSyncingEncryptFilesUi = true;
            try
            {
                EncryptSaveNextToSourceToggle.IsOn = false;
                EncryptOutputLocationBox.Text = folder.Path;
                OutputCustomLocationRadio.IsChecked = true;
                OutputSameLocationRadio.IsChecked = false;
                EncryptOutputFolderBox.Text = folder.Path;
            }
            finally
            {
                _isSyncingEncryptFilesUi = false;
            }

            UpdateEncryptDestinationUi();
            RefreshEncryptFilesState();
            SetStatus($"Encrypt output folder selected: {folder.Name}");
        }

        private void EncryptOutputLocationBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (_isSyncingEncryptFilesUi)
            {
                return;
            }

            string text = EncryptOutputLocationBox.Text?.Trim() ?? string.Empty;
            if (string.Equals(text, GetSameSourceEncryptOutputLabel(hasFolderSelections: false), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, GetSameSourceEncryptOutputLabel(hasFolderSelections: true), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            OutputCustomLocationRadio.IsChecked = true;
            OutputSameLocationRadio.IsChecked = false;
            EncryptOutputFolderBox.Text = text;
            UpdateEncryptDestinationUi();
            RefreshEncryptFilesState();
        }

        private void EncryptSaveNextToSourceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (_isSyncingEncryptFilesUi)
            {
                return;
            }

            OutputSameLocationRadio.IsChecked = EncryptSaveNextToSourceToggle.IsOn;
            OutputCustomLocationRadio.IsChecked = !EncryptSaveNextToSourceToggle.IsOn;
            if (!EncryptSaveNextToSourceToggle.IsOn &&
                (string.Equals(EncryptOutputLocationBox.Text, GetSameSourceEncryptOutputLabel(hasFolderSelections: false), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(EncryptOutputLocationBox.Text, GetSameSourceEncryptOutputLabel(hasFolderSelections: true), StringComparison.OrdinalIgnoreCase)))
            {
                EncryptOutputLocationBox.Text = GetDefaultEncryptOutputFolder();
            }

            if (OutputCustomLocationRadio.IsChecked == true)
            {
                EncryptOutputFolderBox.Text = EncryptOutputLocationBox.Text.Trim();
            }

            UpdateEncryptDestinationUi();
            SynchronizeEncryptOptionsFromWorkflow();
            RefreshEncryptFilesState();
        }

        private void EncryptCompressBeforeEncryptionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (_isSyncingEncryptFilesUi)
            {
                return;
            }

            CompressModeToggle.IsOn = EncryptCompressBeforeEncryptionToggle.IsOn;
            RefreshEncryptFilesState();
        }

        private void EncryptDeleteOriginalsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (_isSyncingEncryptFilesUi)
            {
                return;
            }

            RemoveOriginalsToggle.IsOn = EncryptDeleteOriginalsToggle.IsOn;
            RefreshEncryptFilesState();
        }

        private void EncryptPreserveFolderStructureToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            if (_isSyncingEncryptFilesUi)
            {
                return;
            }

            PackageFoldersToggle.IsOn = EncryptPreserveFolderStructureToggle.IsOn;
            RefreshEncryptFilesState();
        }

        private async void EncryptHistoryHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_jobHistoryItems.Count == 0)
            {
                await ShowInfoDialogAsync("No local operation history has been recorded yet.", "History");
                return;
            }

            var builder = new StringBuilder();
            foreach (JobHistoryItem item in _jobHistoryItems.Take(6))
            {
                builder.AppendLine(item.Title);
                builder.AppendLine(item.ResultSummary);
                builder.AppendLine();
            }

            await ShowInfoDialogAsync(builder.ToString().Trim(), "Recent History");
        }

        private string? MaybeAdoptSuggestedEncryptOutputDirectory(bool folderSelectionAdded, int previousCount)
        {
            if (!_isUiReady || !folderSelectionAdded || previousCount > 0 || OutputCustomLocationRadio.IsChecked == true)
            {
                return null;
            }

            string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots(
                FileList
                    .Where(item => item.SourceRootIsFolder)
                    .Select(item => item.SourceRootPath));
            if (string.IsNullOrWhiteSpace(suggestedPath))
            {
                return null;
            }

            _isSyncingEncryptFilesUi = true;
            try
            {
                OutputCustomLocationRadio.IsChecked = true;
                OutputSameLocationRadio.IsChecked = false;
                EncryptSaveNextToSourceToggle.IsOn = false;
                EncryptOutputFolderBox.Text = suggestedPath;
                EncryptOutputLocationBox.Text = suggestedPath;
            }
            finally
            {
                _isSyncingEncryptFilesUi = false;
            }

            UpdateEncryptDestinationUi();
            return suggestedPath;
        }

        private static string GetDefaultEncryptOutputFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "FileLocker", "Encrypted");
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private enum EncodeTextMode
        {
            Encode,
            Decode
        }

        private const string DefaultEncodeTextFormat = "Base64";

        private EncodeTextMode _encodeTextMode = EncodeTextMode.Encode;
        private string _encodeTextFormat = DefaultEncodeTextFormat;
        private string _encodeTextStatus = "Waiting for input";
        private bool _isUpdatingEncodeTextUi;

        public ObservableCollection<EncodeRecentConversionItem> RecentEncodeTextItems { get; } = [];

        public sealed class EncodeRecentConversionItem
        {
            public string IconGlyph { get; set; } = "\uE943";

            public string Summary { get; set; } = string.Empty;

            public string TimestampDisplay { get; set; } = string.Empty;
        }

        private void InitializeEncodeTextView()
        {
            EncodeRecentConversionsListView.ItemsSource = RecentEncodeTextItems;
            if (EncodeFormatComboBox.SelectedIndex < 0)
            {
                EncodeFormatComboBox.SelectedIndex = 0;
            }

            _encodeTextFormat = ReadSelectedEncodeTextFormat();
            SetEncodeTextMode(EncodeTextMode.Encode, clearOutput: false);
            SetEncodeTextStatus("Waiting for input", updateGlobalStatus: false);
            RefreshEncodeTextState();
        }

        private void PrepareEncodeTextSection()
        {
            _encodeTextFormat = ReadSelectedEncodeTextFormat();
            RefreshEncodeTextState();
        }

        private void EncodeInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingEncodeTextUi)
            {
                return;
            }

            if (!string.IsNullOrEmpty(EncodeOutputTextBox.Text))
            {
                EncodeOutputTextBox.Text = string.Empty;
            }

            SetEncodeTextStatus(
                string.IsNullOrEmpty(EncodeInputTextBox.Text) ? "Waiting for input" : "Ready",
                updateGlobalStatus: false);
            RefreshEncodeTextState();
        }

        private async void EncodePasteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DataPackageView content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    SetStatus("Clipboard does not contain text.");
                    return;
                }

                string text = await content.GetTextAsync();
                if (string.IsNullOrEmpty(text))
                {
                    SetStatus("Clipboard text is empty.");
                    return;
                }

                _isUpdatingEncodeTextUi = true;
                EncodeInputTextBox.Text = text;
                EncodeOutputTextBox.Text = string.Empty;
                _isUpdatingEncodeTextUi = false;

                SetEncodeTextStatus("Ready", updateGlobalStatus: false);
                SetStatus("Clipboard text pasted locally.");
                RefreshEncodeTextState();
            }
            catch (Exception ex)
            {
                _isUpdatingEncodeTextUi = false;
                SetEncodeTextStatus("Error");
                SetStatus($"Unable to read clipboard text: {GetFriendlyExceptionMessage(ex, "Clipboard is unavailable.")}");
            }
        }

        private void ClearEncodeTextButton_Click(object sender, RoutedEventArgs e)
        {
            ClearEncodeTextState();
            SetStatus("Encode Text cleared.");
        }

        private void RunEncodeTextButton_Click(object sender, RoutedEventArgs e)
        {
            RunEncodeTextConversionFromUi();
        }

        private async void EncodeCopyOutputButton_Click(object sender, RoutedEventArgs e)
        {
            await CopyEncodeTextOutputAsync();
        }

        private async void EncodeSaveTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(EncodeOutputTextBox.Text))
            {
                SetStatus("Run encode or decode before saving output.");
                return;
            }

            try
            {
                string modeSlug = _encodeTextMode == EncodeTextMode.Decode ? "decoded" : "encoded";
                string formatSlug = _encodeTextFormat.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
                FileSavePicker picker = CreateSaveFilePicker(PickerLocationId.DocumentsLibrary, $"filelocker-{modeSlug}-{formatSlug}");
                picker.FileTypeChoices.Add("Text file", [".txt"]);

                StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                await File.WriteAllTextAsync(file.Path, EncodeOutputTextBox.Text, Encoding.UTF8);
                SetStatus($"Encode Text output saved to {file.Name}.");
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync($"Unable to save the text output: {GetFriendlyExceptionMessage(ex, "Save failed.")}");
            }
        }

        private async void EncodingGuideHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 620
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Encode Text changes how text is represented for transport, storage, or interoperability. It is not encryption and does not protect secrets.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Base64, URL, Hex, HTML Entities, and UTF-8 byte views run locally on this device. Input and output are not added to persistent FileLocker history.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Use Decode only when the input is already in the selected format. Invalid Base64 or Hex input will show an inline status instead of crashing.",
                TextWrapping = TextWrapping.WrapWholeWords
            });

            await ShowInfoDialogAsync(panel, "Encoding Guide");
        }

        private void EncodeModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetEncodeTextMode(EncodeTextMode.Encode);
        }

        private void DecodeModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetEncodeTextMode(EncodeTextMode.Decode);
        }

        private void EncodeFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingEncodeTextUi)
            {
                return;
            }

            _encodeTextFormat = ReadSelectedEncodeTextFormat();
            if (!string.IsNullOrEmpty(EncodeOutputTextBox.Text))
            {
                EncodeOutputTextBox.Text = string.Empty;
                SetEncodeTextStatus(string.IsNullOrEmpty(EncodeInputTextBox.Text) ? "Waiting for input" : "Ready", updateGlobalStatus: false);
            }

            RefreshEncodeTextState();
        }

        private void EncodeOptionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            RefreshEncodeTextState();
        }

        private void EncodeQuickExampleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag)
            {
                return;
            }

            string[] parts = tag.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return;
            }

            SetEncodeTextMode(
                string.Equals(parts[0], "Decode", StringComparison.OrdinalIgnoreCase)
                    ? EncodeTextMode.Decode
                    : EncodeTextMode.Encode);
            SelectEncodeTextFormat(parts[1]);

            _isUpdatingEncodeTextUi = true;
            EncodeInputTextBox.Text = BuildEncodeTextSampleInput(_encodeTextMode, _encodeTextFormat);
            EncodeOutputTextBox.Text = string.Empty;
            _isUpdatingEncodeTextUi = false;

            SetEncodeTextStatus("Ready", updateGlobalStatus: false);
            SetStatus($"{_encodeTextFormat} {_encodeTextMode.ToString().ToLowerInvariant()} example loaded.");
            RefreshEncodeTextState();
        }

        private async void RunEncodeTextConversionFromUi()
        {
            string input = EncodeInputTextBox.Text;
            if (string.IsNullOrEmpty(input))
            {
                SetEncodeTextStatus("Waiting for input");
                SetStatus("Enter text before running Encode Text.");
                return;
            }

            string format = _encodeTextFormat;
            EncodeTextMode mode = _encodeTextMode;
            bool preserveLineBreaks = EncodePreserveLineBreaksToggle.IsOn;

            try
            {
                string output = await Task.Run(() => ConvertEncodeText(input, mode, format, preserveLineBreaks));
                EncodeOutputTextBox.Text = output;
                SetEncodeTextStatus(mode == EncodeTextMode.Decode ? "Decoded" : "Encoded", updateGlobalStatus: false);
                AddEncodeRecentConversion(mode, format, input.Length, output.Length);
                SetStatus($"{(mode == EncodeTextMode.Decode ? "Decoded" : "Encoded")} text using {format}.");
            }
            catch (FormatException ex)
            {
                EncodeOutputTextBox.Text = string.Empty;
                SetEncodeTextStatus("Error", updateGlobalStatus: false);
                SetStatus(ex.Message);
            }
            catch (Exception ex)
            {
                EncodeOutputTextBox.Text = string.Empty;
                SetEncodeTextStatus("Error", updateGlobalStatus: false);
                SetStatus($"Unable to process text: {GetFriendlyExceptionMessage(ex, "Unsupported input.")}");
            }
            finally
            {
                RefreshEncodeTextState();
            }
        }

        private async Task CopyEncodeTextOutputAsync()
        {
            if (string.IsNullOrEmpty(EncodeOutputTextBox.Text))
            {
                SetStatus("Run encode or decode before copying output.");
                return;
            }

            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(EncodeOutputTextBox.Text);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                SetStatus("Encode Text output copied to clipboard.");
            }
            catch (Exception ex)
            {
                SetEncodeTextStatus("Error");
                SetStatus($"Unable to copy output: {GetFriendlyExceptionMessage(ex, "Clipboard is unavailable.")}");
            }

            await Task.CompletedTask;
        }

        private void ClearEncodeTextState()
        {
            _isUpdatingEncodeTextUi = true;
            EncodeInputTextBox.Text = string.Empty;
            EncodeOutputTextBox.Text = string.Empty;
            _isUpdatingEncodeTextUi = false;

            SetEncodeTextStatus("Waiting for input", updateGlobalStatus: false);
            RefreshEncodeTextState();
        }

        private void SetEncodeTextMode(EncodeTextMode mode, bool clearOutput = true)
        {
            _encodeTextMode = mode;

            if (clearOutput && !string.IsNullOrEmpty(EncodeOutputTextBox.Text))
            {
                EncodeOutputTextBox.Text = string.Empty;
                SetEncodeTextStatus(string.IsNullOrEmpty(EncodeInputTextBox.Text) ? "Waiting for input" : "Ready", updateGlobalStatus: false);
            }

            RefreshEncodeTextState();
        }

        private void SelectEncodeTextFormat(string format)
        {
            _isUpdatingEncodeTextUi = true;
            foreach (ComboBoxItem item in EncodeFormatComboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content?.ToString(), format, StringComparison.OrdinalIgnoreCase))
                {
                    EncodeFormatComboBox.SelectedItem = item;
                    _encodeTextFormat = format;
                    break;
                }
            }

            _isUpdatingEncodeTextUi = false;
            RefreshEncodeTextState();
        }

        private string ReadSelectedEncodeTextFormat()
        {
            return GetSelectedComboText(EncodeFormatComboBox, DefaultEncodeTextFormat);
        }

        private void SetEncodeTextStatus(string status, bool updateGlobalStatus = true)
        {
            _encodeTextStatus = status;
            if (updateGlobalStatus)
            {
                SetStatus(status);
            }
        }

        private void RefreshEncodeTextState()
        {
            if (EncodeInputTextBox == null ||
                EncodeOutputTextBox == null ||
                EncodeSummaryModeText == null ||
                EncodeSummaryFormatText == null ||
                RunEncodeTextButton == null ||
                EncodeWrapLongLinesToggle == null)
            {
                return;
            }

            string input = EncodeInputTextBox.Text ?? string.Empty;
            string output = EncodeOutputTextBox.Text ?? string.Empty;
            bool hasInput = input.Length > 0;
            bool hasOutput = output.Length > 0;

            _encodeTextFormat = ReadSelectedEncodeTextFormat();

            EncodeInputCharacterCountText.Text = FormatCharacterCount(input.Length);
            EncodeSummaryModeText.Text = _encodeTextMode == EncodeTextMode.Decode ? "Decode" : "Encode";
            EncodeSummaryFormatText.Text = _encodeTextFormat;
            EncodeSummaryInputLengthText.Text = FormatCharacterCount(input.Length);
            EncodeSummaryOutputLengthText.Text = FormatCharacterCount(output.Length);
            EncodeSummaryStatusText.Text = _encodeTextStatus;
            EncodeStatusBadgeText.Text = GetEncodeTextStatusBadgeText();

            RunEncodeTextButton.IsEnabled = hasInput;
            RunEncodeTextButtonText.Text = _encodeTextMode == EncodeTextMode.Decode ? "Decode Text" : "Encode Text";
            EncodeCopyOutputButton.IsEnabled = hasOutput;
            EncodeInlineCopyButton.IsEnabled = hasOutput;
            EncodeSaveTextButton.IsEnabled = hasOutput;
            EncodeCopyOutputHeaderButton.IsEnabled = hasOutput;

            EncodeOutputTextBox.TextWrapping = EncodeWrapLongLinesToggle.IsOn ? TextWrapping.Wrap : TextWrapping.NoWrap;
            UpdateEncodeTextModeButtons();
            UpdateEncodeTextStatusVisual();
            UpdateEncodeFormatHelper();
            RefreshEncodeRecentConversions();
        }

        private void UpdateEncodeTextModeButtons()
        {
            Brush selectedBackground = GetBrushResource("PrimaryActionBrush");
            Brush selectedForeground = GetBrushResource("PrimaryActionForegroundBrush");
            Brush normalBackground = GetBrushResource("InputSurfaceBrush");
            Brush normalForeground = GetBrushResource("TextPrimaryBrush");

            EncodeModeButton.Background = _encodeTextMode == EncodeTextMode.Encode ? selectedBackground : normalBackground;
            EncodeModeButton.Foreground = _encodeTextMode == EncodeTextMode.Encode ? selectedForeground : normalForeground;
            DecodeModeButton.Background = _encodeTextMode == EncodeTextMode.Decode ? selectedBackground : normalBackground;
            DecodeModeButton.Foreground = _encodeTextMode == EncodeTextMode.Decode ? selectedForeground : normalForeground;
        }

        private void UpdateEncodeTextStatusVisual()
        {
            Brush accent = _encodeTextStatus switch
            {
                "Encoded" or "Decoded" => GetBrushResource("SuccessBrush"),
                "Error" => GetBrushResource("DangerBrush"),
                "Ready" => GetBrushResource("BrightBlueBrush"),
                _ => GetBrushResource("TextSecondaryBrush")
            };

            Brush background = _encodeTextStatus switch
            {
                "Encoded" or "Decoded" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 58, 52)),
                "Error" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 55, 18, 30)),
                _ => GetBrushResource("InputSurfaceBrush")
            };

            string glyph = _encodeTextStatus switch
            {
                "Encoded" or "Decoded" => "\uE73E",
                "Error" => "\uE783",
                "Ready" => "\uE9CE",
                _ => "\uE9CE"
            };

            EncodeStatusBadgeBorder.Background = background;
            EncodeStatusBadgeBorder.BorderBrush = accent;
            EncodeStatusBadgeIcon.Foreground = accent;
            EncodeStatusBadgeIcon.Glyph = glyph;
            EncodeStatusBadgeText.Foreground = accent;
            EncodeSummaryStatusMarker.Background = accent;
            EncodeSummaryStatusText.Foreground = accent;
        }

        private void UpdateEncodeFormatHelper()
        {
            EncodeFormatHelperText.Text = _encodeTextFormat switch
            {
                "URL" => "URL encoding escapes text for query strings, form values, and links.",
                "Hex" => "Hex represents UTF-8 bytes as hexadecimal pairs.",
                "HTML Entities" => "HTML entities escape characters such as angle brackets for markup contexts.",
                "UTF-8" => "UTF-8 shows text as byte values; decode expects UTF-8 byte hex pairs.",
                _ => "Base64 is useful for representing text in systems that expect plain characters."
            };
        }

        private string GetEncodeTextStatusBadgeText()
        {
            return _encodeTextStatus switch
            {
                "Encoded" => "Encoded successfully",
                "Decoded" => "Decoded successfully",
                "Error" => "Error",
                "Ready" => "Ready",
                _ => "Waiting for input"
            };
        }

        private void AddEncodeRecentConversion(EncodeTextMode mode, string format, int inputLength, int outputLength)
        {
            string action = mode == EncodeTextMode.Decode ? "decoded from" : "encoded to";
            RecentEncodeTextItems.Insert(0, new EncodeRecentConversionItem
            {
                Summary = $"Text {action} {format}",
                TimestampDisplay = "Just now",
                IconGlyph = "\uE943"
            });

            while (RecentEncodeTextItems.Count > 5)
            {
                RecentEncodeTextItems.RemoveAt(RecentEncodeTextItems.Count - 1);
            }

            _ = inputLength;
            _ = outputLength;
        }

        private void RefreshEncodeRecentConversions()
        {
            EncodeRecentEmptyText.Visibility = RecentEncodeTextItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EncodeRecentConversionsListView.Visibility = RecentEncodeTextItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string ConvertEncodeText(string input, EncodeTextMode mode, string format, bool preserveLineBreaks)
        {
            string preparedInput = preserveLineBreaks ? input : ReplaceLineBreaksWithSpaces(input);

            return format switch
            {
                "URL" => mode == EncodeTextMode.Decode ? DecodeUrl(preparedInput) : WebUtility.UrlEncode(preparedInput) ?? string.Empty,
                "Hex" => mode == EncodeTextMode.Decode ? DecodeHexToUtf8(preparedInput) : Convert.ToHexString(Encoding.UTF8.GetBytes(preparedInput)),
                "HTML Entities" => mode == EncodeTextMode.Decode ? WebUtility.HtmlDecode(preparedInput) ?? string.Empty : WebUtility.HtmlEncode(preparedInput) ?? string.Empty,
                "UTF-8" => mode == EncodeTextMode.Decode ? DecodeHexToUtf8(preparedInput) : EncodeUtf8ByteView(preparedInput),
                _ => mode == EncodeTextMode.Decode ? DecodeBase64ToUtf8(preparedInput) : Convert.ToBase64String(Encoding.UTF8.GetBytes(preparedInput))
            };
        }

        private static string DecodeBase64ToUtf8(string input)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(RemoveWhitespace(input));
                return StrictUtf8.GetString(bytes);
            }
            catch (FormatException ex)
            {
                throw new FormatException("Base64 decode failed. Check that the input is valid Base64 text.", ex);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("Base64 decoded bytes are not valid UTF-8 text.", ex);
            }
        }

        private static string DecodeUrl(string input)
        {
            return WebUtility.UrlDecode(input) ?? string.Empty;
        }

        private static string DecodeHexToUtf8(string input)
        {
            try
            {
                byte[] bytes = ParseHexBytes(input);
                return StrictUtf8.GetString(bytes);
            }
            catch (FormatException ex)
            {
                throw new FormatException("Hex decode failed. Use an even number of valid hex characters.", ex);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("Hex decoded bytes are not valid UTF-8 text.", ex);
            }
        }

        private static byte[] ParseHexBytes(string input)
        {
            string normalized = input.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
            string hex = new(normalized
                .Where(character => !char.IsWhiteSpace(character) && character != '-' && character != ':' && character != ',')
                .ToArray());

            if (hex.Length == 0)
            {
                return [];
            }

            if (hex.Length % 2 != 0 || hex.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new FormatException("Invalid hex input.");
            }

            return Convert.FromHexString(hex);
        }

        private static string EncodeUtf8ByteView(string input)
        {
            return string.Join(" ", Encoding.UTF8.GetBytes(input).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static string RemoveWhitespace(string input)
        {
            return new string(input.Where(character => !char.IsWhiteSpace(character)).ToArray());
        }

        private static string ReplaceLineBreaksWithSpaces(string input)
        {
            return input.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private static string BuildEncodeTextSampleInput(EncodeTextMode mode, string format)
        {
            if (mode == EncodeTextMode.Encode)
            {
                return format == "HTML Entities"
                    ? "<span>Hello, FileLocker!</span>"
                    : "Hello, FileLocker!";
            }

            return format switch
            {
                "URL" => "Hello%2C+FileLocker%21",
                "Hex" => Convert.ToHexString(Encoding.UTF8.GetBytes("Hello, FileLocker!")),
                "HTML Entities" => "&lt;span&gt;Hello, FileLocker!&lt;/span&gt;",
                "UTF-8" => EncodeUtf8ByteView("Hello, FileLocker!"),
                _ => Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello, FileLocker!"))
            };
        }

        private static string FormatCharacterCount(int count)
        {
            return count == 1 ? "1 character" : $"{count.ToString(CultureInfo.InvariantCulture)} characters";
        }

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }
}

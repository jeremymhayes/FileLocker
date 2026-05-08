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
        private const string ENCRYPTED_EXTENSION = ".locked";
        private const int SALT_SIZE = 32;
        private const int IV_SIZE = 12; // GCM uses 12-byte IV
        private const int KEY_SIZE = 32;
        private const int TAG_SIZE = 16; // GCM authentication tag
        private const byte FORMAT_VERSION = 2; // Version for compatibility
        private const int ARGON2_ITERATIONS = 3;
        private const int ARGON2_MEMORY_SIZE_KB = 65536;
        private const int PBKDF2_FALLBACK_ITERATIONS = 600000;
        private const int MIN_PADDING_SIZE = 1024; // Minimum padding to hide file size
        private const int MAX_PADDING_SIZE = 8192; // Maximum padding
        private const string STEGO_CHUNK_TYPE = "flDR";
        private const string DefaultDropLabelText = "Drop files to start a local encryption run";
        private const string ActiveDropLabelText = "Release to queue items";
        private const int MaxHistoryEntries = 20;
        private const int StrongPasswordMinimumScore = 70;
        private static readonly string[] EncryptionAlgorithms = ["AES-GCM"];
        private static readonly string[] HashAlgorithms = ["SHA-256", "SHA-512", "Base64"];
        private static readonly int[] EncryptionKeySizes = [256];
        private static readonly int[] HashKeySizes = [256, 512];
        private static readonly byte[] StegoCarrierPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAAWgmWQ0AAAAASUVORK5CYII=");
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _decryptQueuedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<PreflightIssueItem> _preflightItems = [];
        private readonly ObservableCollection<JobHistoryItem> _jobHistoryItems = [];
        private readonly List<OperationHistoryEntry> _operationHistory = [];
        private readonly List<EncryptionProfile> _customProfiles = [];
        private readonly AppPreferences _preferences;
        private ThemePreference _themePreference;
        private string _historyOperationFilter = "All operations";
        private string _historyStatusFilter = "All statuses";
        private bool isDarkTheme = true;
        public ObservableCollection<QueuedFileItem> FileList { get; set; } = [];
        public ObservableCollection<DecryptSelectedItemViewModel> DecryptSelectedFiles { get; } = [];
        public string StatusText { get; set; } = "Queue is empty. Add files or folders to begin.";
        private AppWindow? _appWindow;
        private bool _isUpdatingModeOptions;
        private bool _isApplyingProfile;
        private bool _isApplyingExperienceMode;
        private bool _isEncryptImportantDismissedThisSession;
        private CancellationTokenSource? _processingCancellation;
        private CancellationTokenSource? _preflightRefreshCancellation;
        private bool _isProcessing;
        private bool _isUiReady;
        private bool _isWindowClosed;
        private readonly SemaphoreSlim _dialogSemaphore = new(1, 1);
        private UserExperienceLevel _currentExperienceLevel;
        private readonly UpdateSettings _updateSettings = UpdateService.LoadSettings();
        private string _aboutUpdateStatusText = "Updates: automatic checks enabled";
        private bool _isCheckingForUpdates;
        private bool _isDownloadingUpdate;
        private bool _hasStartedAutomaticUpdateCheck;

        // Advanced options properties
        public bool IsCompressModeEnabled { get; set; } = true;
        public bool IsScrambleNamesEnabled { get; set; } = false;
        public bool IsSteganographyEnabled { get; set; } = false;
        public bool IsPackageFoldersEnabled { get; set; } = true;
        private readonly Random _random = new();

        private record struct PasswordStrengthResult(
            int Score,
            string Feedback,
            Windows.UI.Color BarColor);

        private enum PreflightSeverity
        {
            Info,
            Warning,
            Error
        }

        private enum ProcessingIntent
        {
            Encrypt,
            Decrypt,
            Verify
        }

        private sealed record MetadataOverridesSnapshot(
            string Label,
            string Notes,
            bool Randomize,
            string CreatedText,
            string ModifiedText);

        private sealed record ProcessingRunOptions(
            bool CompressFiles,
            bool ScrambleNames,
            bool UseSteganography,
            string Algorithm,
            string Mode,
            int KeySizeBits,
            bool RemoveOriginalsAfterSuccess,
            bool SecureDeleteOriginals,
            bool VerifyAfterWrite,
            bool UseCustomEncryptOutputDirectory,
            string EncryptOutputDirectory,
            bool UseCustomDecryptOutputDirectory,
            string DecryptOutputDirectory,
            bool RestoreOriginalFilenames,
            bool PreserveFolderStructure,
            bool PackageFolders,
            string OutputTimestampPolicy,
            string BackupFolderPath,
            string? KeyfilePath,
            byte[]? KeyfileBytes,
            string? RecoveryKey,
            string ProfileName,
            MetadataOverridesSnapshot Metadata);

        private sealed class PreflightIssueItem
        {
            public required string IconGlyph { get; init; }
            public required string SeverityText { get; init; }
            public required string Message { get; init; }
        }

        private sealed record ExpandedQueueFile(
            string Path,
            string RootPath,
            bool RootIsFolder,
            long SizeBytes);

        private sealed class QueueExpandResult
        {
            public List<ExpandedQueueFile> Files { get; } = [];

            public List<string> Warnings { get; } = [];
        }

        private sealed record PreflightSnapshotItem(
            string SourcePath,
            string SourceRootPath,
            bool SourceRootIsFolder,
            string Status);

        private sealed class PreflightEvaluationResult
        {
            public List<PreflightIssue> Issues { get; } = [];

            public Dictionary<string, string> PredictedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ProcessingWorkItem
        {
            public required string PrimaryPath { get; init; }
            public required List<QueuedFileItem> QueueItems { get; init; }
            public bool EncryptAsFolderPackage { get; init; }
            public string? FolderRootPath { get; init; }
        }

        private sealed class JobHistoryItem
        {
            public required string Id { get; init; }
            public required string Title { get; init; }
            public required string Subtitle { get; init; }
            public required string ResultSummary { get; init; }
        }

        private sealed class EncryptionProfile
        {
            public required string Name { get; init; }
            public string Description { get; init; } = string.Empty;
            public required string Algorithm { get; init; }
            public int KeySizeBits { get; init; }
            public bool CompressFiles { get; init; }
            public bool ScrambleNames { get; init; }
            public bool UseSteganography { get; init; }
            public bool RandomizeMetadata { get; init; }
            public bool RemoveOriginalsAfterSuccess { get; init; }
            public bool SecureDeleteOriginals { get; init; }
            public bool VerifyAfterWrite { get; init; }
            public string BackupFolderPath { get; init; } = string.Empty;
            public string KeyfilePath { get; init; } = string.Empty;
            public bool IsBuiltIn { get; init; }
        }

        public MainWindow(AppPreferences preferences, IReadOnlyList<string>? launchPaths = null, string? launchAction = null)
        {
            _preferences = preferences;
            _themePreference = _preferences.ThemePreference;
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(NativeTitleBar);
            Closed += MainWindow_Closed;
            _currentExperienceLevel = _preferences.ExperienceLevel;

            if (Content is not FrameworkElement)
            {
                // If XAML failed to load, fail clearly instead of crashing later
                throw new InvalidOperationException("MainWindow XAML did not load any root content.");
            }

            // Safely get AppWindow (older OS / failures won’t crash the app)
            try
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);

                if (_appWindow != null)
                {
                    _appWindow.SetPresenter(AppWindowPresenterKind.Default);
                    _appWindow.ResizeClient(new SizeInt32(1320, 900));
                    ApplyWindowTitleBarColors();

                    var minSize = new SizeInt32(1180, 900);
                    _appWindow.Changed += (s, args) =>
                    {
                        try
                        {
                            var sz = s.Size;
                            int w = Math.Max(sz.Width, minSize.Width);
                            int h = Math.Max(sz.Height, minSize.Height);
                            if (w != sz.Width || h != sz.Height)
                            {
                                s.Resize(new SizeInt32(w, h));
                            }
                        }
                        catch
                        {
                            // ignore window sizing errors
                        }
                    };
                }
            }
            catch
            {
                // Keep startup resilient when AppWindow customization is unavailable.
            }

            _ = InitializeWebViewAsync(launchPaths, launchAction);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isWindowClosed = true;
            _processingCancellation?.Cancel();
            _preflightRefreshCancellation?.Cancel();
        }

        private void ApplyWindowTitleBarColors()
        {
            // Keep native window behavior intact; only tint the system title bar when supported.
            if (_appWindow?.TitleBar == null || !AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = _appWindow.TitleBar;
            if (isDarkTheme)
            {
                titleBar.BackgroundColor = ColorHelper.FromArgb(255, 20, 28, 46);
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveBackgroundColor = ColorHelper.FromArgb(255, 20, 28, 46);
                titleBar.InactiveForegroundColor = ColorHelper.FromArgb(255, 170, 180, 195);
                titleBar.ButtonBackgroundColor = ColorHelper.FromArgb(255, 20, 28, 46);
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 20, 28, 46);
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 170, 180, 195);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 20, 36, 59);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 23, 51, 92);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
            else
            {
                titleBar.BackgroundColor = ColorHelper.FromArgb(255, 248, 251, 255);
                titleBar.ForegroundColor = ColorHelper.FromArgb(255, 22, 32, 43);
                titleBar.InactiveBackgroundColor = ColorHelper.FromArgb(255, 232, 238, 246);
                titleBar.InactiveForegroundColor = ColorHelper.FromArgb(255, 86, 101, 120);
                titleBar.ButtonBackgroundColor = ColorHelper.FromArgb(255, 248, 251, 255);
                titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 22, 32, 43);
                titleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 232, 238, 246);
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 86, 101, 120);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 221, 234, 254);
                titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 22, 32, 43);
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 214, 224, 237);
                titleBar.ButtonPressedForegroundColor = ColorHelper.FromArgb(255, 22, 32, 43);
            }
        }

    }
}


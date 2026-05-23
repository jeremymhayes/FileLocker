using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FileLocker
{
    public sealed partial class MainWindow
    {
        private const string DevServerUrl = "http://127.0.0.1:5173";
        private const string AppHostName = "filelocker.app";

        private static readonly JsonSerializerOptions BridgeJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private static readonly HashSet<string> RestartPageKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "partition-cleaner",
            "drive-optimizer",
            "custom-clean",
            "registry-fixer",
            "startup-manager",
            "app-manager"
        };

        private IReadOnlyList<string> _launchPaths = [];
        private string? _launchAction;

#if DEBUG
        private const bool IsDebugBuild = true;
#else
        private const bool IsDebugBuild = false;
#endif

        private async Task InitializeWebViewSafelyAsync(IReadOnlyList<string>? launchPaths, string? launchAction)
        {
            try
            {
                await InitializeWebViewAsync(launchPaths, launchAction);
            }
            catch (Exception ex)
            {
                if (_isWindowClosed)
                {
                    return;
                }

                string message = GetFriendlyExceptionMessage(ex, "The FileLocker interface could not be loaded.");
                SetStatus($"Interface startup failed: {message}");
                await ShowErrorDialogAsync($"Unable to start the FileLocker interface:\n{message}");
            }
        }

        private async Task InitializeWebViewAsync(IReadOnlyList<string>? launchPaths, string? launchAction)
        {
            _launchPaths = launchPaths ?? [];
            _launchAction = launchAction;

            await LoadBridgeHistoryAsync();
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", GetWebViewUserDataDirectory());
            await AppWebView.EnsureCoreWebView2Async();

            AppWebView.AllowDrop = true;
            AppWebView.DragOver += AppWebView_DragOver;
            AppWebView.Drop += AppWebView_Drop;
            AppWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
#if DEBUG
            AppWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            AppWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            AppWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            Uri source = await ResolveAppInterfaceSourceAsync();
            AppWebView.Source = source;
        }

        private void DetachWebViewHandlers()
        {
            AppWebView.DragOver -= AppWebView_DragOver;
            AppWebView.Drop -= AppWebView_Drop;
            if (AppWebView.CoreWebView2 != null)
            {
                AppWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            }
        }

        private static string GetWebViewUserDataDirectory()
        {
            string path = Path.Combine(GetAppDataDirectory(), "WebView2");
            Directory.CreateDirectory(path);
            return path;
        }

        private void AppWebView_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }

        private async void AppWebView_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;

            string[] paths;
            var deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                paths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();
            }
            catch (Exception ex)
            {
                PostBridgeEvent(new
                {
                    type = "dropError",
                    message = SensitiveDataRedactor.RedactMessage(GetFriendlyExceptionMessage(ex, "Drag and drop failed."))
                });
                return;
            }
            finally
            {
                deferral.Complete();
            }

            if (paths.Length > 0)
            {
                PostBridgeEvent(new { type = "droppedPaths", paths });
            }
        }

        private async Task<Uri> ResolveAppInterfaceSourceAsync()
        {
#if DEBUG
            if (await IsDevServerAvailableAsync())
            {
                return new Uri(DevServerUrl);
            }
#endif
            string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            string indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                string repoDistPath = Path.Combine(AppContext.BaseDirectory, "frontend", "dist", "index.html");
                if (File.Exists(repoDistPath))
                {
                    webRoot = Path.GetDirectoryName(repoDistPath)!;
                    indexPath = repoDistPath;
                }
            }

            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException("The FileLocker interface files were not found. Build the app assets before starting FileLocker.", indexPath);
            }

            AppWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            return new Uri($"https://{AppHostName}/index.html");
        }

        private static async Task<bool> IsDevServerAvailableAsync()
        {
            try
            {
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromMilliseconds(600)
                };

                using HttpResponseMessage response = await client.GetAsync(DevServerUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            BridgeRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<BridgeRequest>(args.WebMessageAsJson, BridgeJsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Action))
                {
                    throw new InvalidOperationException("Bridge request is missing an id or action.");
                }

                object? result = await DispatchBridgeRequestAsync(request);
                PostBridgeResponse(request.Id, result);
            }
            catch (Exception ex)
            {
                PostBridgeError(
                    request?.Id ?? string.Empty,
                    "BRIDGE_ERROR",
                    GetFriendlyExceptionMessage(ex, "The request could not be completed."));
            }
        }

        private Task<object?> DispatchBridgeRequestAsync(BridgeRequest request)
        {
            return request.Action switch
            {
                "app.getInitialState" => GetInitialStateAsync(),
                "app.setTitlePage" => Task.FromResult<object?>(SetTitlePageFromBridge(ReadPayload<TitlePageRequest>(request.Payload))),
                "app.restartAsAdministrator" => RestartAsAdministratorFromBridgeAsync(ReadPayload<RestartAsAdministratorRequest>(request.Payload)),
                "files.pickFiles" => PickFilesAsync(),
                "files.pickFolder" => PickFolderAsync(),
                "files.describePaths" => RunBridgeWorkerAsync(() => DescribePathsFromBridge(ReadPayload<PathListRequest>(request.Payload))),
                "files.suggestEncryptOutput" => Task.FromResult<object?>(SuggestEncryptOutputFromBridge(ReadPayload<PathListRequest>(request.Payload))),
                "files.revealPath" => RevealPathAsync(ReadPayload<RevealPathRequest>(request.Payload)),
                "links.openExternal" => OpenExternalLinkAsync(ReadPayload<OpenExternalRequest>(request.Payload)),
                "crypto.encryptFiles" => EncryptFilesFromBridgeAsync(ReadPayload<FileOperationRequest>(request.Payload)),
                "crypto.decryptFiles" => DecryptFilesFromBridgeAsync(ReadPayload<FileOperationRequest>(request.Payload)),
                "crypto.verifyPayload" => VerifyPayloadsFromBridgeAsync(ReadPayload<FileOperationRequest>(request.Payload)),
                "hash.compute" => ComputeHashFromBridgeAsync(ReadPayload<HashComputeRequest>(request.Payload)),
                "hash.verify" => Task.FromResult<object?>(VerifyHashFromBridge(ReadPayload<HashVerifyRequest>(request.Payload))),
                "hash.manifestCreate" => CreateHashManifestFromBridgeAsync(ReadPayload<HashManifestCreateRequest>(request.Payload)),
                "hash.manifestVerify" => VerifyHashManifestFromBridgeAsync(ReadPayload<HashManifestVerifyRequest>(request.Payload)),
                "text.convert" => RunBridgeWorkerAsync(() => ConvertTextFromBridge(ReadPayload<TextConvertRequest>(request.Payload))),
                "metadata.inspect" => InspectMetadataFromBridgeAsync(ReadPayload<MetadataInspectRequest>(request.Payload)),
                "secureDelete.delete" => SecureDeleteFromBridgeAsync(ReadPayload<SecureDeleteRequest>(request.Payload)),
                "maintenance.getDrives" => RunBridgeWorkerAsync(() => SystemMaintenanceService.GetDrives()),
                "maintenance.scanCleanup" => RunBridgeWorkerAsync(() => SystemMaintenanceService.ScanCleanup(ReadPayload<MaintenanceCleanupRequest>(request.Payload).CategoryIds)),
                "maintenance.runCleanup" => RunBridgeWorkerAsync(() => RunCleanupFromBridge(ReadPayload<MaintenanceCleanupRequest>(request.Payload))),
                "maintenance.optimizeDrive" => OptimizeDriveFromBridgeAsync(ReadPayload<MaintenanceDriveActionRequest>(request.Payload)),
                "maintenance.wipeFreeSpace" => WipeFreeSpaceFromBridgeAsync(ReadPayload<MaintenanceDriveActionRequest>(request.Payload)),
                "maintenance.scanRegistry" => RunBridgeWorkerAsync(() => SystemMaintenanceService.ScanRegistry()),
                "maintenance.cleanRegistry" => RunBridgeWorkerAsync(() => CleanRegistryFromBridge(ReadPayload<RegistryCleanRequest>(request.Payload))),
                "maintenance.scanStartup" => RunBridgeWorkerAsync(() => StartupAppMaintenanceService.ScanStartup()),
                "maintenance.setStartupEnabled" => RunBridgeWorkerAsync(() => SetStartupEnabledFromBridge(ReadPayload<StartupToggleRequest>(request.Payload))),
                "maintenance.scanInstalledApps" => RunBridgeWorkerAsync(() => StartupAppMaintenanceService.ScanInstalledApps()),
                "maintenance.launchUninstaller" => RunBridgeWorkerAsync(() => LaunchUninstallerFromBridge(ReadPayload<UninstallerLaunchRequest>(request.Payload))),
                "maintenance.scanAppLeftovers" => RunBridgeWorkerAsync(() => StartupAppMaintenanceService.ScanAppLeftovers(ReadPayload<AppLeftoverRequest>(request.Payload).AppIds)),
                "maintenance.cleanAppLeftovers" => RunBridgeWorkerAsync(() => CleanAppLeftoversFromBridge(ReadPayload<AppLeftoverRequest>(request.Payload))),
                "settings.get" => Task.FromResult<object?>(BuildSettingsPayload()),
                "settings.save" => SaveSettingsFromBridgeAsync(ReadPayload<SettingsSaveRequest>(request.Payload)),
                "settings.reset" => ResetSettingsFromBridgeAsync(),
                "shell.setExplorerIntegration" => Task.FromResult<object?>(SetExplorerIntegrationFromBridge(ReadPayload<ExplorerIntegrationRequest>(request.Payload))),
                "updates.check" => CheckForUpdatesFromBridgeAsync(),
                "updates.download" => DownloadUpdateFromBridgeAsync(),
                "updates.install" => InstallUpdateFromBridgeAsync(),
                "updates.skip" => Task.FromResult<object?>(SkipUpdateFromBridge(ReadPayload<UpdateSkipRequest>(request.Payload))),
                "updates.clearSkip" => Task.FromResult<object?>(ClearSkippedUpdateFromBridge()),
#if DEBUG
                "updates.testDialog" => TestUpdateDialogAsync(),
                "updates.testStartupCheck" => TestUpdateStartupCheckAsync(),
                "updates.testInstallerCleanup" => TestInstallerCleanupAsync(),
#endif
                "history.clear" => ClearHistoryFromBridgeAsync(),
                "history.export" => ExportHistoryFromBridgeAsync(ReadPayload<HistoryExportRequest>(request.Payload)),
                _ => throw new InvalidOperationException($"Unknown bridge action '{request.Action}'.")
            };
        }

        private static T ReadPayload<T>(JsonElement payload)
        {
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                throw new InvalidOperationException("Bridge payload was empty or invalid.");
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(payload.GetRawText(), BridgeJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Bridge payload was empty or invalid.", ex);
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidOperationException("Bridge payload was empty or invalid.", ex);
            }

            return value ?? throw new InvalidOperationException("Bridge payload was empty or invalid.");
        }

        private static Task<object?> RunBridgeWorkerAsync(Func<object?> action)
        {
            return Task.Run(action);
        }

        private void PostBridgeResponse(string requestId, object? result)
        {
            PostBridgeMessage(new BridgeResponse(requestId, true, result, null));
        }

        private void PostBridgeError(string requestId, string code, string message)
        {
            PostBridgeMessage(new BridgeResponse(requestId, false, null, new BridgeError(code, SensitiveDataRedactor.RedactMessage(message))));
        }

        private void PostBridgeEvent(object payload)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => PostBridgeEvent(payload));
                return;
            }

            PostBridgeMessage(payload);
        }

        private void PostBridgeMessage(object payload)
        {
            try
            {
                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => PostBridgeMessage(payload));
                    return;
                }

                if (AppWebView?.CoreWebView2 == null)
                {
                    return;
                }

                string json = JsonSerializer.Serialize(payload, BridgeJsonOptions);
                AppWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bridge post failed: {SensitiveDataRedactor.RedactMessage(GetFriendlyExceptionMessage(ex, "Bridge post failed."))}");
            }
        }

        private async Task<object?> GetInitialStateAsync()
        {
            await LoadBridgeHistoryAsync();

            return new
            {
                app = new
                {
                    name = "FileLocker",
                    version = UpdateService.GetCurrentVersionLabel(),
                    repositoryUrl = UpdateService.GitHubRepositoryUrl,
                    launchPaths = _launchPaths,
                    launchAction = _launchAction,
                    isAdministrator = IsRunningAsAdministrator(),
                    canRestartAsAdministrator = !IsRunningAsAdministrator(),
                    isDebug = IsDebugBuild
                },
                dashboard = BuildDashboardPayload(),
                settings = BuildSettingsPayload()
            };
        }

        private Task<object?> RestartAsAdministratorFromBridgeAsync(RestartAsAdministratorRequest request)
        {
            if (IsRunningAsAdministrator())
            {
                return Task.FromResult<object?>(new
                {
                    restarted = false,
                    isAdministrator = true,
                    message = "FileLocker is already running as administrator."
                });
            }

            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new InvalidOperationException("FileLocker could not find its executable to restart as administrator.");
            }

            string targetPage = NormalizeRestartTargetPage(request.TargetPage);
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.IsNullOrWhiteSpace(targetPage) ? string.Empty : $"--page={targetPage}"
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Windows did not start the elevated FileLocker process.");
            }

            DispatcherQueue.TryEnqueue(() => Close());

            return Task.FromResult<object?>(new
            {
                restarted = true,
                isAdministrator = false,
                targetPage
            });
        }

        private object SetTitlePageFromBridge(TitlePageRequest request)
        {
            string pageName = NormalizeTitlePageName(request.PageName);

            string title = $"FileLocker — {pageName}";
            NativeTitleText.Text = title;
            return new { title };
        }

        internal static string NormalizeTitlePageName(string? pageName)
        {
            if (string.IsNullOrWhiteSpace(pageName))
            {
                return "Dashboard";
            }

            string trimmed = pageName.Trim();
            var builder = new StringBuilder(trimmed.Length);
            bool pendingControlSpace = false;
            foreach (char character in trimmed)
            {
                if (char.IsControl(character))
                {
                    pendingControlSpace = true;
                    continue;
                }

                if (pendingControlSpace)
                {
                    if (builder.Length > 0 &&
                        !char.IsWhiteSpace(builder[^1]) &&
                        !char.IsWhiteSpace(character))
                    {
                        builder.Append(' ');
                    }

                    pendingControlSpace = false;
                }

                builder.Append(character);
            }

            string normalized = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "Dashboard";
            }

            return normalized.Length > 80 ? normalized[..80] : normalized;
        }

        private static string NormalizeRestartTargetPage(string? targetPage)
        {
            if (string.IsNullOrWhiteSpace(targetPage))
            {
                return string.Empty;
            }

            string normalized = targetPage.Trim().TrimStart('#').ToLowerInvariant();
            return RestartPageKeys.Contains(normalized) ? normalized : string.Empty;
        }

        private static bool IsRunningAsAdministrator()
        {
            return SystemMaintenanceService.IsRunningAsAdministrator();
        }

        private static StartupToggleResult SetStartupEnabledFromBridge(StartupToggleRequest request)
        {
            return StartupAppMaintenanceService.SetStartupEnabled(request.ItemId, request.Enabled);
        }

        private static UninstallerLaunchResult LaunchUninstallerFromBridge(UninstallerLaunchRequest request)
        {
            return StartupAppMaintenanceService.LaunchUninstaller(request.AppId, request.Confirmation);
        }

        private static AppLeftoverCleanResult CleanAppLeftoversFromBridge(AppLeftoverRequest request)
        {
            return StartupAppMaintenanceService.CleanAppLeftovers(request.AppIds, request.CategoryIds, request.Confirmation);
        }

        private async Task<object?> PickFilesAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
            return new
            {
                paths = files.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
            };
        }

        private async Task<object?> PickFolderAsync()
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            return new
            {
                path = folder?.Path ?? string.Empty
            };
        }

        private Task<object?> RevealPathAsync(RevealPathRequest request)
        {
            string path = RequireExistingPath(request.Path);
            string target = Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path) ?? path;
            OpenWithShell(target);
            return Task.FromResult<object?>(new { opened = true, path = target });
        }

        private Task<object?> OpenExternalLinkAsync(OpenExternalRequest request)
        {
            string url = RequireExternalHttpsUrl(request.Url);
            OpenWithShell(url);
            return Task.FromResult<object?>(new { opened = true, url });
        }

        internal static string RequireExternalHttpsUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only HTTPS links can be opened.");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("HTTPS links with embedded credentials cannot be opened.");
            }

            return uri.ToString();
        }

        private async Task<object?> EncryptFilesFromBridgeAsync(FileOperationRequest request)
        {
            return await RunFileOperationFromBridgeAsync("Encrypt", ProcessingIntent.Encrypt, request);
        }

        private async Task<object?> DecryptFilesFromBridgeAsync(FileOperationRequest request)
        {
            return await RunFileOperationFromBridgeAsync("Decrypt", ProcessingIntent.Decrypt, request);
        }

        private async Task<object?> VerifyPayloadsFromBridgeAsync(FileOperationRequest request)
        {
            return await RunFileOperationFromBridgeAsync("Verify", ProcessingIntent.Verify, request);
        }

        private async Task<object?> RunFileOperationFromBridgeAsync(string operationName, ProcessingIntent intent, FileOperationRequest request)
        {
            string operationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId;
            string[] paths = ValidateFileOperationBridgePaths(request.Paths);

            if (_processingCancellation is not null)
            {
                throw new InvalidOperationException("A file operation is already running. Wait for it to finish before starting another.");
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                throw new InvalidOperationException(intent == ProcessingIntent.Encrypt
                    ? "Enter a password before encrypting."
                    : "Enter the unlock password.");
            }

            var processingCancellation = new CancellationTokenSource();
            _processingCancellation = processingCancellation;
            ProcessingRunOptions? runOptions = null;
            byte[]? loadedKeyfileBytes = null;
            var results = new List<FileOperationResult>();
            var failedPaths = new List<string>();
            bool cancelled = false;

            try
            {
                ValidateFolderSourceRemovalConfirmation(request.RemoveOriginalsAfterSuccess, paths, request.DeleteConfirmation);

                string keyfilePath = request.KeyfilePath?.Trim() ?? string.Empty;
                loadedKeyfileBytes = await Task.Run(() => ReadKeyfileBytesIfConfigured(keyfilePath));
                runOptions = CreateRunOptionsFromBridge(request, keyfilePath, loadedKeyfileBytes);
                loadedKeyfileBytes = null;

                QueueExpandResult expansion = await Task.Run(() => ExpandQueuePaths(paths));
                List<QueuedFileItem> queueItems = expansion.Files
                    .Select(file => new QueuedFileItem(file.Path, file.RootPath, file.RootIsFolder, file.SizeBytes))
                    .ToList();

                if (intent != ProcessingIntent.Encrypt)
                {
                    queueItems = queueItems
                        .Where(item => intent == ProcessingIntent.Verify || IsSupportedFileLockerEncryptedFile(item.SourcePath, out _))
                        .ToList();
                }

                List<ProcessingWorkItem> workItems = BuildBridgeWorkItems(queueItems, intent, runOptions);
                if (workItems.Count == 0)
                {
                    throw new InvalidOperationException("No supported files were found for this operation.");
                }

                for (int index = 0; index < workItems.Count; index++)
                {
                    ProcessingWorkItem workItem = workItems[index];
                    if (processingCancellation.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    string currentPath = workItem.PrimaryPath;
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        PostProgress(operationId, currentPath, 2, "Preparing");
                        FileOperationResult result = await Task.Run(() =>
                        {
                            Action<double, string> progress = (percent, status) => PostProgress(operationId, currentPath, percent, status);
                            return intent switch
                            {
                                ProcessingIntent.Encrypt when workItem.EncryptAsFolderPackage =>
                                    EncryptFolderPackage(workItem, request.Password, runOptions, progress),
                                ProcessingIntent.Encrypt =>
                                    EncryptFileAdvancedCore(
                                        currentPath,
                                        request.Password,
                                        runOptions,
                                        workItem.FolderRootPath,
                                        workItem.FolderRootPath is not null,
                                        progress),
                                ProcessingIntent.Decrypt =>
                                    DecryptFileAdvanced(currentPath, request.Password, runOptions, progress),
                                _ =>
                                    VerifyLockedFile(currentPath, request.Password, runOptions, progress)
                            };
                        });

                        result.ElapsedMilliseconds ??= elapsed.ElapsedMilliseconds;
                        results.Add(result);
                        PostProgress(operationId, currentPath, 100, intent == ProcessingIntent.Verify ? "Verified" : "Completed");
                    }
                    catch (Exception ex)
                    {
                        failedPaths.Add(currentPath);
                        results.Add(new FileOperationResult
                        {
                            SourcePath = currentPath,
                            Status = "Failed",
                            Message = GetFriendlyExceptionMessage(ex, "File operation failed."),
                            OriginalRetained = true,
                            OutputVerified = false,
                            FailureCategory = OperationFailureClassifier.Classify(ex),
                            ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                        });
                        PostProgress(operationId, currentPath, 100, "Failed");
                    }
                }

                await AppendBridgeHistoryAsync(operationName, runOptions, results, cancelled);

                return new
                {
                    operationId,
                    cancelled,
                    completed = results.Count(result => string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.Equals(result.Status, "Verified", StringComparison.OrdinalIgnoreCase)),
                    failed = failedPaths.Count,
                    warnings = expansion.Warnings,
                    results = results.Select(ToResultDto).ToArray(),
                    dashboard = BuildDashboardPayload()
                };
            }
            finally
            {
                if (runOptions?.KeyfileBytes is { Length: > 0 } keyfileBytes)
                {
                    CryptographicOperations.ZeroMemory(keyfileBytes);
                }
                else if (loadedKeyfileBytes is { Length: > 0 } orphanedKeyfileBytes)
                {
                    CryptographicOperations.ZeroMemory(orphanedKeyfileBytes);
                }

                if (ReferenceEquals(_processingCancellation, processingCancellation))
                {
                    _processingCancellation = null;
                }

                processingCancellation.Dispose();
            }
        }

        internal static string[] ValidateFileOperationBridgePaths(string[]? paths)
        {
            return NormalizeRequiredBridgePathList(paths, "Select at least one file or folder.");
        }

        internal static void ValidateFolderSourceRemovalConfirmation(
            bool removeOriginalsAfterSuccess,
            IEnumerable<string> paths,
            string? deleteConfirmation)
        {
            if (removeOriginalsAfterSuccess &&
                paths.Any(path => Directory.Exists(path)) &&
                !string.Equals(deleteConfirmation, "DELETE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Folder-wide source removal requires typing DELETE before the run starts.");
            }
        }

        private ProcessingRunOptions CreateRunOptionsFromBridge(
            FileOperationRequest request,
            string keyfilePath,
            byte[]? keyfileBytes)
        {
            string encryptOutputDirectory = request.EncryptOutputDirectory?.Trim() ?? string.Empty;
            string decryptOutputDirectory = request.DecryptOutputDirectory?.Trim() ?? string.Empty;
            bool useCustomEncryptOutput = !request.SaveNextToSource && !string.IsNullOrWhiteSpace(encryptOutputDirectory);
            bool useCustomDecryptOutput = !request.SaveNextToEncrypted && !string.IsNullOrWhiteSpace(decryptOutputDirectory);

            var options = new ProcessingRunOptions(
                request.CompressFiles,
                request.ScrambleNames,
                request.UseSteganography,
                "AES-GCM",
                "Encrypt / Decrypt",
                256,
                request.RemoveOriginalsAfterSuccess,
                request.SecureDeleteOriginals,
                request.VerifyAfterWrite,
                useCustomEncryptOutput,
                encryptOutputDirectory,
                useCustomDecryptOutput,
                decryptOutputDirectory,
                request.RestoreOriginalFilenames,
                request.PreserveFolderStructure,
                request.PackageFolders,
                string.IsNullOrWhiteSpace(request.OutputTimestampPolicy) ? "Current time" : request.OutputTimestampPolicy,
                request.BackupFolderPath?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(keyfilePath) ? null : keyfilePath,
                keyfileBytes,
                string.IsNullOrWhiteSpace(request.RecoveryKey) ? null : request.RecoveryKey.Trim(),
                string.IsNullOrWhiteSpace(request.ProfileName) ? "FileLocker" : request.ProfileName,
                new MetadataOverridesSnapshot(
                    request.MetadataLabel?.Trim() ?? string.Empty,
                    request.MetadataNotes?.Trim() ?? string.Empty,
                    request.RandomizeMetadata,
                    request.MetadataCreatedText ?? string.Empty,
                    request.MetadataModifiedText ?? string.Empty));

            return NormalizeRunOptionsForCurrentMode(options);
        }

        private static List<ProcessingWorkItem> BuildBridgeWorkItems(List<QueuedFileItem> queueItems, ProcessingIntent intent, ProcessingRunOptions options)
        {
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
            foreach (IGrouping<string, QueuedFileItem> group in queueItems.Where(item => item.SourceRootIsFolder).GroupBy(item => item.SourceRootPath, StringComparer.OrdinalIgnoreCase))
            {
                workItems.Add(new ProcessingWorkItem
                {
                    PrimaryPath = group.Key,
                    QueueItems = group.ToList(),
                    EncryptAsFolderPackage = true,
                    FolderRootPath = group.Key
                });
            }

            foreach (QueuedFileItem item in queueItems.Where(item => !item.SourceRootIsFolder))
            {
                workItems.Add(new ProcessingWorkItem
                {
                    PrimaryPath = item.SourcePath,
                    QueueItems = [item],
                    EncryptAsFolderPackage = false,
                    FolderRootPath = null
                });
            }

            return workItems;
        }

        private void PostProgress(string operationId, string path, double percent, string status)
        {
            PostBridgeEvent(new
            {
                type = "progress",
                operationId,
                path,
                fileName = Path.GetFileName(path),
                percent = Math.Clamp(percent, 0, 100),
                status
            });
        }

        private async Task<object?> ComputeHashFromBridgeAsync(HashComputeRequest request)
        {
            string path = RequireExistingFile(request.Path);
            string algorithm = NormalizeBridgeHashAlgorithm(request.Algorithm);
            string operationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId;

            var progress = new Progress<double>(percent => PostProgress(operationId, path, percent, "Hashing"));
            string hash = await FileHashService.ComputeHashHexAsync(path, algorithm, progress);

            var result = new FileOperationResult
            {
                SourcePath = path,
                Status = "Completed",
                Message = $"Generated {algorithm} hash.",
                OriginalRetained = true,
                OutputVerified = false,
                OriginalSizeBytes = new FileInfo(path).Length,
                HashValue = hash
            };

            await AppendBridgeHistoryAsync("Hash", CreateHistoryOnlyRunOptions("Hash", algorithm), [result], cancelled: false);

            return new
            {
                operationId,
                path,
                fileName = Path.GetFileName(path),
                algorithm,
                hash,
                digestBits = FileHashService.GetDigestBits(algorithm),
                expectedLength = FileHashService.GetExpectedHexLength(algorithm),
                dashboard = BuildDashboardPayload()
            };
        }

        private static object VerifyHashFromBridge(HashVerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.GeneratedHash) || string.IsNullOrWhiteSpace(request.ExpectedHash))
            {
                throw new InvalidOperationException("Both generated and expected hashes are required.");
            }

            if (!HashInputNormalizer.TryNormalizeSupportedHash(request.GeneratedHash, out string generated))
            {
                throw new InvalidOperationException("The generated hash is not a supported SHA-256 or SHA-512 value.");
            }

            if (!HashInputNormalizer.TryNormalizeSupportedHash(request.ExpectedHash, out string expected))
            {
                throw new InvalidOperationException("Paste a SHA-256 or SHA-512 hash before verifying.");
            }

            bool match = string.Equals(generated, expected, StringComparison.OrdinalIgnoreCase);
            return new
            {
                match,
                status = match ? "Match" : "Mismatch"
            };
        }

        private async Task<object?> CreateHashManifestFromBridgeAsync(HashManifestCreateRequest request)
        {
            string[] paths = ValidateHashManifestBridgePaths(request.Paths);

            string outputDirectory = Path.Combine(GetAppDataDirectory(), "Manifests");
            HashManifestResult manifest = await HashManifestService.CreateManifestAsync(
                paths,
                NormalizeBridgeHashAlgorithm(request.Algorithm),
                outputDirectory);

            var result = new FileOperationResult
            {
                SourcePath = outputDirectory,
                OutputPath = manifest.ManifestPath,
                Status = "Completed",
                Message = $"Generated {manifest.Algorithm} manifest for {manifest.FileCount} file(s).",
                OriginalRetained = true,
                OutputVerified = true
            };
            await AppendBridgeHistoryAsync("Hash Manifest", CreateHistoryOnlyRunOptions("Hash Manifest", manifest.Algorithm), [result], cancelled: false);

            return new
            {
                manifest.ManifestPath,
                manifest.FileName,
                manifest.Algorithm,
                manifest.FileCount,
                dashboard = BuildDashboardPayload()
            };
        }

        internal static string[] ValidateHashManifestBridgePaths(string[]? paths)
        {
            return NormalizeRequiredBridgePathList(paths, "Select at least one file or folder for the hash manifest.");
        }

        private async Task<object?> VerifyHashManifestFromBridgeAsync(HashManifestVerifyRequest request)
        {
            string manifestPath = RequireExistingFile(request.ManifestPath);
            string rootDirectory = RequireExistingPath(request.RootDirectory);
            if (!Directory.Exists(rootDirectory))
            {
                throw new InvalidOperationException("Choose the folder that the manifest paths should be verified against.");
            }

            HashManifestVerificationResult verification = await HashManifestService.VerifyManifestAsync(manifestPath, rootDirectory);
            return new
            {
                verification.ManifestPath,
                verification.EntryCount,
                verification.MatchedCount,
                verification.MismatchedCount,
                verification.MissingCount,
                status = verification.MismatchedCount == 0 && verification.MissingCount == 0 ? "Verified" : "Mismatch"
            };
        }

        private static object ConvertTextFromBridge(TextConvertRequest request)
        {
            EncodeTextMode mode = string.Equals(request.Mode, "decode", StringComparison.OrdinalIgnoreCase)
                ? EncodeTextMode.Decode
                : EncodeTextMode.Encode;
            string output = ConvertEncodeText(request.Input ?? string.Empty, mode, NormalizeEncodeFormat(request.Format), request.PreserveLineBreaks);
            return new
            {
                output,
                inputLength = request.Input?.Length ?? 0,
                outputLength = output.Length
            };
        }

        private Task<object?> InspectMetadataFromBridgeAsync(MetadataInspectRequest request)
        {
            if (MetadataCategories.Count == 0)
            {
                InitializeMetadataCategories();
            }

            MetadataCategorySnapshot[] categorySnapshots = MetadataCategories
                .Select(category => new MetadataCategorySnapshot(
                    category.Name,
                    category.Description,
                    category.IsSelected,
                    category.IsSupported))
                .ToArray();

            return RunBridgeWorkerAsync(() => InspectMetadataFromBridge(request, categorySnapshots));
        }

        private static object InspectMetadataFromBridge(MetadataInspectRequest request, IReadOnlyCollection<MetadataCategorySnapshot> categorySnapshots)
        {
            string[] requestedPaths = NormalizeBridgePathList(request.Paths);

            if (requestedPaths.Length == 0 && !string.IsNullOrWhiteSpace(request.Path))
            {
                requestedPaths = [request.Path.Trim()];
            }

            if (requestedPaths.Length == 0)
            {
                throw new InvalidOperationException("Select at least one file or folder to inspect.");
            }

            MetadataPathExpansionResult expansion = ExpandMetadataScramblerPaths(requestedPaths);
            List<MetadataSelectedFileViewModel> files = expansion.FilePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CreateMetadataSelectedFile)
                .ToList();

            if (files.Count == 0)
            {
                throw new InvalidOperationException("No supported files were found for metadata preview.");
            }

            MetadataSelectedFileViewModel activeFile = files.FirstOrDefault(file =>
                    string.Equals(file.FullPath, request.Path, StringComparison.OrdinalIgnoreCase))
                ?? files[0];

            var fileInfo = new FileInfo(activeFile.FullPath);
            string mode = string.IsNullOrWhiteSpace(request.Mode) ? "Remove metadata" : request.Mode;
            HashSet<string> selectedCategoryNames = BuildSelectedMetadataCategorySet(request.SelectedCategories, categorySnapshots);

            MetadataCategoryDto[] categories = categorySnapshots
                .Select(category => new MetadataCategoryDto(
                    category.Name,
                    category.Description,
                    selectedCategoryNames.Contains(category.Name),
                    category.IsSupported,
                    EstimateMetadataCategoryCount(category.Name, fileInfo)))
                .ToArray();

            MetadataPreviewDto[] preview = BuildMetadataPreviewDtos(fileInfo, mode, selectedCategoryNames);

            return new
            {
                files = files.Select(file => new
                {
                    file.DisplayName,
                    file.FullPath,
                    file.FileType,
                    file.SizeDisplay,
                    file.MetadataTagCount,
                    file.MetadataCountDisplay,
                    file.StatusDisplay,
                    file.IsSupported
                }).ToArray(),
                activeFilePath = activeFile.FullPath,
                file = new
                {
                    displayName = fileInfo.Name,
                    fullPath = fileInfo.FullName,
                    fileType = GetMetadataFileTypeDisplay(fileInfo.Extension.ToLowerInvariant()),
                    sizeDisplay = FormatFileSize(fileInfo.Length),
                    metadataTagCount = CountBasicMetadataFields(fileInfo),
                    statusDisplay = IsCommonMetadataFileType(fileInfo.Extension.ToLowerInvariant()) ? "Ready" : "Review",
                    isSupported = IsCommonMetadataFileType(fileInfo.Extension.ToLowerInvariant())
                },
                mode,
                writeSupportEnabled = MetadataWriteSupportEnabled,
                categories,
                preview,
                summary = new
                {
                    filesSelected = files.Count,
                    tagsFound = files.Sum(file => file.MetadataTagCount),
                    categoriesSelected = categories.Count(category => category.IsSelected),
                    mode,
                    output = MetadataWriteSupportEnabled ? "Create cleaned copies" : "Preview only",
                    status = MetadataWriteSupportEnabled ? "Ready to scramble" : "Ready to preview"
                },
                warnings = expansion.Warnings.ToArray(),
                report = BuildMetadataPreviewReportText(fileInfo, mode, preview)
            };
        }

        private static string BuildMetadataPreviewReportText(FileInfo fileInfo, string mode, IEnumerable<MetadataPreviewDto> preview)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Metadata Scrambler Preview");
            builder.AppendLine($"File: {fileInfo.Name}");
            builder.AppendLine($"Type: {GetMetadataFileTypeDisplay(fileInfo.Extension.ToLowerInvariant())}");
            builder.AppendLine($"Size: {FormatFileSize(fileInfo.Length)}");
            builder.AppendLine($"Mode: {mode}");
            builder.AppendLine("No files changed.");
            builder.AppendLine();
            foreach (MetadataPreviewDto item in preview)
            {
                builder.AppendLine($"{item.Label}: {item.BeforeValue} -> {item.AfterValue}");
            }

            return builder.ToString();
        }

        private static HashSet<string> BuildSelectedMetadataCategorySet(string[]? selectedCategories, IReadOnlyCollection<MetadataCategorySnapshot> categorySnapshots)
        {
            string[] selectedCategoryNames = NormalizeBridgeStringList(selectedCategories);
            if (selectedCategoryNames.Length > 0)
            {
                return new HashSet<string>(
                    selectedCategoryNames,
                    StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                categorySnapshots
                    .Where(category => category.IsSelected)
                    .Select(category => category.Name),
                StringComparer.OrdinalIgnoreCase);
        }

        private static MetadataPreviewDto[] BuildMetadataPreviewDtos(FileInfo fileInfo, string mode, HashSet<string> selectedCategoryNames)
        {
            var preview = new List<MetadataPreviewDto>();
            string extension = fileInfo.Extension.ToLowerInvariant();

            if (selectedCategoryNames.Contains("Document properties"))
            {
                preview.Add(new MetadataPreviewDto("File name", fileInfo.Name, "Preserved in preview"));
                preview.Add(new MetadataPreviewDto("File type", GetMetadataFileTypeDisplay(extension), "Preserved in preview"));
                preview.Add(new MetadataPreviewDto("Size", FormatFileSize(fileInfo.Length), "Preserved in preview"));
            }

            if (selectedCategoryNames.Contains("Timestamps"))
            {
                string timestampAction = BuildMetadataCategoryAfterValue(mode);
                preview.Add(new MetadataPreviewDto("Created", FormatMetadataDate(fileInfo.CreationTime), timestampAction));
                preview.Add(new MetadataPreviewDto("Modified", FormatMetadataDate(fileInfo.LastWriteTime), timestampAction));
                preview.Add(new MetadataPreviewDto("Last accessed", FormatMetadataDate(fileInfo.LastAccessTime), timestampAction));
            }

            if (selectedCategoryNames.Contains("Custom metadata"))
            {
                preview.Add(new MetadataPreviewDto("Attributes", fileInfo.Attributes.ToString(), "Preserved in preview"));
            }

            foreach (string categoryName in selectedCategoryNames.Where(name =>
                         !string.Equals(name, "Document properties", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(name, "Timestamps", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(name, "Custom metadata", StringComparison.OrdinalIgnoreCase)))
            {
                preview.Add(new MetadataPreviewDto(
                    categoryName,
                    BuildMetadataCategoryBeforeValue(categoryName, extension),
                    BuildMetadataCategoryAfterValue(mode)));
            }

            if (preview.Count == 0)
            {
                preview.Add(new MetadataPreviewDto("Selection", "No metadata categories selected", "Choose one or more categories to build a preview."));
            }

            return preview.ToArray();
        }

        private static string BuildMetadataCategoryBeforeValue(string categoryName, string extension)
        {
            return categoryName switch
            {
                "Author information" => IsDocumentLikeMetadataExtension(extension)
                    ? "Potential author, owner, or creator fields"
                    : "No common author fields detected",
                "GPS/location data" => IsMediaMetadataExtension(extension)
                    ? "Location fields may exist in supported media"
                    : "Usually absent for this file type",
                "Camera/device data" => IsMediaMetadataExtension(extension)
                    ? "Device and capture fields may be present"
                    : "Not typical for this file type",
                "Application metadata" => "Editing application fields may be present",
                _ => "Preview representative fields only"
            };
        }

        private static string BuildMetadataCategoryAfterValue(string mode)
        {
            return "Preview only";
        }

        private static int EstimateMetadataCategoryCount(string categoryName, FileInfo fileInfo)
        {
            string extension = fileInfo.Extension.ToLowerInvariant();

            return categoryName switch
            {
                "Document properties" => 3,
                "Timestamps" => 3,
                "Custom metadata" => 1,
                "Author information" => IsDocumentLikeMetadataExtension(extension) ? 1 : 0,
                "GPS/location data" => IsImageMetadataExtension(extension) ? 1 : 0,
                "Camera/device data" => IsMediaMetadataExtension(extension) ? 1 : 0,
                "Application metadata" => IsCommonMetadataFileType(extension) ? 1 : 0,
                _ => 0
            };
        }

        private static bool IsDocumentLikeMetadataExtension(string extension)
        {
            return extension is ".pdf" or ".docx" or ".xlsx" or ".pptx" or ".rtf" or ".txt";
        }

        private static bool IsImageMetadataExtension(string extension)
        {
            return extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".tif" or ".tiff" or ".heic" or ".bmp";
        }

        private static bool IsMediaMetadataExtension(string extension)
        {
            return IsImageMetadataExtension(extension) || extension is ".mp4" or ".mov" or ".webm" or ".mkv" or ".mp3" or ".wav";
        }

        private async Task<object?> SecureDeleteFromBridgeAsync(SecureDeleteRequest request)
        {
            ValidateSecureDeleteConfirmation(request.Confirmation);

            if (_processingCancellation is not null)
            {
                throw new InvalidOperationException("A file operation is already running. Wait for it to finish before starting another.");
            }

            var processingCancellation = new CancellationTokenSource();
            _processingCancellation = processingCancellation;

            try
            {
                string[] paths = ValidateSecureDeleteBridgePaths(request.Paths);
                int overwritePasses = NormalizeSecureDeletePasses(request.OverwritePasses, request.Method);
                string methodLabel = GetSecureDeleteMethodLabel(request.Method, overwritePasses);
                var results = new List<FileOperationResult>();
                foreach (string rawPath in paths)
                {
                    string resultPath = string.IsNullOrWhiteSpace(rawPath) ? "(blank path)" : rawPath;
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        string path = RequireExistingPath(rawPath);
                        resultPath = path;
                        await Task.Run(() =>
                        {
                            if (Directory.Exists(path))
                            {
                                DeleteSourceDirectory(path, secureDelete: true, secureDeletePasses: overwritePasses);
                            }
                            else
                            {
                                SecureDelete(path, overwritePasses);
                            }
                        });

                        results.Add(new FileOperationResult
                        {
                            SourcePath = resultPath,
                            Status = "Completed",
                            Message = $"Secure delete completed using {methodLabel}.",
                            OriginalRetained = false,
                            OutputVerified = false,
                            ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new FileOperationResult
                        {
                            SourcePath = resultPath,
                            Status = "Failed",
                            Message = GetFriendlyExceptionMessage(ex, "Secure delete failed."),
                            OriginalRetained = true,
                            OutputVerified = false,
                            FailureCategory = OperationFailureClassifier.Classify(ex),
                            ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                        });
                    }
                }

                await AppendBridgeHistoryAsync("Secure Delete", CreateHistoryOnlyRunOptions("Secure Delete", methodLabel), results, cancelled: false);
                return new
                {
                    results = results.Select(ToResultDto).ToArray(),
                    dashboard = BuildDashboardPayload()
                };
            }
            finally
            {
                if (ReferenceEquals(_processingCancellation, processingCancellation))
                {
                    _processingCancellation = null;
                }

                processingCancellation.Dispose();
            }
        }

        internal static void ValidateSecureDeleteConfirmation(string? confirmation)
        {
            if (!string.Equals(confirmation, "DELETE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Confirm secure delete before deleting selected files or folders.");
            }
        }

        internal static string[] ValidateSecureDeleteBridgePaths(string[]? paths)
        {
            return NormalizeRequiredBridgePathList(paths, "Select at least one file or folder to delete.");
        }

        private static string[] NormalizeRequiredBridgePathList(string[]? paths, string emptyMessage)
        {
            string[] normalizedPaths;
            try
            {
                normalizedPaths = NormalizeBridgePathList(paths, fullPaths: true);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException("One or more selected paths are invalid.", ex);
            }

            if (normalizedPaths.Length == 0)
            {
                throw new InvalidOperationException(emptyMessage);
            }

            return normalizedPaths;
        }

        private static int NormalizeSecureDeletePasses(int requestedPasses, string? method)
        {
            if (string.Equals(method, "quick", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(method, "gutmann", StringComparison.OrdinalIgnoreCase))
            {
                return 35;
            }

            if (requestedPasses > 0)
            {
                return Math.Clamp(requestedPasses, 1, 35);
            }

            return 3;
        }

        private static string GetSecureDeleteMethodLabel(string? method, int passes)
        {
            if (string.Equals(method, "quick", StringComparison.OrdinalIgnoreCase))
            {
                return "Quick (1 pass)";
            }

            if (string.Equals(method, "gutmann", StringComparison.OrdinalIgnoreCase))
            {
                return "Gutmann (35 passes)";
            }

            return $"DoD 5220.22-M ({passes} passes)";
        }

        private async Task<object?> OptimizeDriveFromBridgeAsync(MaintenanceDriveActionRequest request)
        {
            return await SystemMaintenanceService.OptimizeDriveAsync(request.DriveRoot, request.Mode);
        }

        private async Task<object?> WipeFreeSpaceFromBridgeAsync(MaintenanceDriveActionRequest request)
        {
            return await SystemMaintenanceService.WipeFreeSpaceAsync(request.DriveRoot, request.Confirmation);
        }

        private static object RunCleanupFromBridge(MaintenanceCleanupRequest request)
        {
            return SystemMaintenanceService.RunCleanup(request.CategoryIds, request.Confirmation);
        }

        private static object CleanRegistryFromBridge(RegistryCleanRequest request)
        {
            return SystemMaintenanceService.CleanRegistry(request.IssueIds, request.Confirmation);
        }

        private object BuildSettingsPayload()
        {
            return new
            {
                preferences = new
                {
                    _preferences.IncognitoMode,
                    _preferences.IncludeFullPathsInExports,
                    _preferences.OutputTimestampPolicy,
                    _preferences.UseCustomEncryptOutputDirectory,
                    _preferences.CustomEncryptOutputDirectory,
                    _preferences.UseCustomDecryptOutputDirectory,
                    _preferences.CustomDecryptOutputDirectory,
                    ThemePreference = _preferences.ThemePreference.ToString()
                },
                updates = new
                {
                    _updateSettings.AutoCheckEnabled,
                    _updateSettings.LastCheckedUtc,
                    _updateSettings.SkippedVersion
                },
                explorerIntegration = ExplorerIntegrationService.GetState(GetCurrentExecutablePath())
            };
        }

        private async Task<object?> SaveSettingsFromBridgeAsync(SettingsSaveRequest request)
        {
            ApplyPreferencesDto(request.Preferences);
            if (request.Updates != null)
            {
                _updateSettings.AutoCheckEnabled = request.Updates.AutoCheckEnabled;
                _updateSettings.SkippedVersion = request.Updates.SkippedVersion;
                UpdateService.SaveSettings(_updateSettings);
            }

            await AppPreferencesStore.SaveAsync(GetAppDataDirectory(), _preferences);
            if (_preferences.IncognitoMode)
            {
                _operationHistory.Clear();
                await SaveHistoryAsync();
            }

            return BuildSettingsPayload();
        }

        private object SetExplorerIntegrationFromBridge(ExplorerIntegrationRequest request)
        {
            ExplorerIntegrationService.SetEnabled(GetCurrentExecutablePath(), request.Enabled);
            return BuildSettingsPayload();
        }

        private async Task<object?> ResetSettingsFromBridgeAsync()
        {
            ApplyPreferencesDto(new PreferencesDto());
            _updateSettings.AutoCheckEnabled = true;
            _updateSettings.SkippedVersion = null;
            UpdateService.SaveSettings(_updateSettings);
            await AppPreferencesStore.SaveAsync(GetAppDataDirectory(), _preferences);
            if (_preferences.IncognitoMode)
            {
                _operationHistory.Clear();
                await SaveHistoryAsync();
            }

            return BuildSettingsPayload();
        }

        private static string GetCurrentExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath;
            }

            return Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not locate the running FileLocker executable.");
        }

        private async Task<object?> CheckForUpdatesFromBridgeAsync()
        {
            UpdateCheckResult result = await UpdateService.CheckForUpdatesAsync(CancellationToken.None);
            _updateSettings.LastCheckedUtc = DateTimeOffset.UtcNow;
            UpdateService.SaveSettings(_updateSettings);
            return ToUpdateCheckDto(ApplySkippedUpdateSetting(result));
        }

        private UpdateCheckResult ApplySkippedUpdateSetting(UpdateCheckResult result)
        {
            if (!result.IsUpdateAvailable ||
                result.Release == null ||
                string.IsNullOrWhiteSpace(_updateSettings.SkippedVersion) ||
                !string.Equals(result.Release.DisplayVersion, _updateSettings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            return result with
            {
                IsUpdateAvailable = false,
                StatusMessage = $"Version {result.Release.DisplayVersion} is skipped. Clear the skipped version to install it.",
            };
        }

        private async Task<object?> DownloadUpdateFromBridgeAsync()
        {
            return await DownloadUpdateInstallerAsync();
        }

        private async Task<DownloadedUpdateDto> DownloadUpdateInstallerAsync()
        {
            UpdateCheckResult result = await UpdateService.CheckForUpdatesAsync(CancellationToken.None);
            _updateSettings.LastCheckedUtc = DateTimeOffset.UtcNow;
            UpdateService.SaveSettings(_updateSettings);

            if (!result.IsUpdateAvailable || result.Release == null)
            {
                throw new InvalidOperationException(result.StatusMessage);
            }

            string installerPath = await UpdateService.DownloadInstallerAsync(result.Release, CancellationToken.None);
            _updateSettings.SkippedVersion = null;
            UpdateService.SaveSettings(_updateSettings);
            return new DownloadedUpdateDto(
                installerPath,
                Path.GetFileName(installerPath),
                ToUpdateReleaseDto(result.Release));
        }

        private async Task<object?> InstallUpdateFromBridgeAsync()
        {
            DownloadedUpdateDto downloadedUpdate = await DownloadUpdateInstallerAsync();
            SetAboutUpdateStatusText("Updates: launching installer");
            SetStatus("Launching FileLocker update installer...");
            LaunchInstallerAndExit(downloadedUpdate.InstallerPath);
            return downloadedUpdate;
        }

        private object SkipUpdateFromBridge(UpdateSkipRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Version))
            {
                throw new InvalidOperationException("A version is required to skip an update.");
            }

            _updateSettings.SkippedVersion = request.Version.Trim();
            UpdateService.SaveSettings(_updateSettings);
            return BuildSettingsPayload();
        }

        private object ClearSkippedUpdateFromBridge()
        {
            _updateSettings.SkippedVersion = null;
            UpdateService.SaveSettings(_updateSettings);
            return BuildSettingsPayload();
        }

#if DEBUG
        private async Task<object?> TestUpdateDialogAsync()
        {
            var mockRelease = new UpdateReleaseInfo(
                new Version(99, 99, 99, 0),
                "99.99.99",
                "v99.99.99",
                UpdateService.GitHubRepositoryUrl,
                "## Test Release\n\nThis is a **mock** update dialog for testing the updater UI.\n\n**New in this build:**\n- Auto-check on startup\n- Installer auto-delete after install\n- Dev testing hooks\n\nClicking Install will attempt a download from a fake URL and fail — that is expected.",
                "FileLocker-test-setup.exe",
                "https://example.invalid/test-installer.exe",
                null,
                null);

            await PromptToInstallUpdateAsync(mockRelease, isManualCheck: true);
            return new { tested = true };
        }

        private async Task<object?> TestUpdateStartupCheckAsync()
        {
            return await CheckForUpdatesFromBridgeAsync();
        }

        private async Task<object?> TestInstallerCleanupAsync()
        {
            string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDirectory);

            string sourceExecutablePath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
            string installerPath = Path.Combine(testDirectory, "FileLocker Fake Installer.exe");
            File.Copy(sourceExecutablePath, installerPath);

            using Process process = UpdateService.StartInstallerAndDeleteWhenClosed(installerPath, TimeSpan.Zero);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            bool installerRan = process.ExitCode == 0;
            bool installerDeleted = !File.Exists(installerPath);
            if (installerRan && installerDeleted)
            {
                try
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }

            return new
            {
                installerRan,
                installerDeleted,
                process.ExitCode,
                testDirectory
            };
        }
#endif

        private static object ToUpdateCheckDto(UpdateCheckResult result)
        {
            return new
            {
                currentVersion = result.CurrentVersion.ToString(),
                result.IsUpdateAvailable,
                result.StatusMessage,
                release = result.Release == null ? null : ToUpdateReleaseDto(result.Release)
            };
        }

        private async Task<object?> ClearHistoryFromBridgeAsync()
        {
            _operationHistory.Clear();
            await SaveHistoryAsync();
            return new
            {
                dashboard = BuildDashboardPayload()
            };
        }

        private async Task<object?> ExportHistoryFromBridgeAsync(HistoryExportRequest request)
        {
            string format = string.Equals(request.Format, "csv", StringComparison.OrdinalIgnoreCase)
                ? "csv"
                : "json";
            string exportsDirectory = Path.Combine(GetAppDataDirectory(), "Exports");
            Directory.CreateDirectory(exportsDirectory);

            string fileName = $"FileLocker-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{format}";
            string exportPath = FileWriteService.ResolveAvailablePath(Path.Combine(exportsDirectory, fileName));
            fileName = Path.GetFileName(exportPath);
            bool includeFullPaths = _preferences.IncludeFullPathsInExports;
            string content = format == "csv"
                ? OperationHistoryExporter.ExportCsv(_operationHistory, includeFullPaths)
                : OperationHistoryExporter.ExportJson(_operationHistory, includeFullPaths);

            await FileWriteService.WriteAllTextAtomicallyAsync(exportPath, content, Encoding.UTF8);
            return new
            {
                exportPath,
                fileName,
                format,
                recordCount = _operationHistory.Count,
                fullPathsIncluded = includeFullPaths
            };
        }

        private async Task LoadBridgeHistoryAsync()
        {
            _operationHistory.Clear();

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Off)
            {
                return;
            }

            string protectedPath = GetProtectedHistoryPath();
            string redactedPath = GetHistoryPath();

            if (_preferences.HistoryPrivacyMode == HistoryPrivacyMode.Full && File.Exists(protectedPath))
            {
                try
                {
                    byte[] protectedBytes = await File.ReadAllBytesAsync(protectedPath);
                    byte[] unprotectedBytes = AppPreferencesStore.UnprotectForCurrentUser(protectedBytes);
                    List<OperationHistoryEntry>? loaded = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(Encoding.UTF8.GetString(unprotectedBytes), JsonOptions);
                    if (loaded != null)
                    {
                        _operationHistory.AddRange(loaded.OrderByDescending(entry => entry.TimestampUtc));
                    }
                }
                catch
                {
                    _operationHistory.Clear();
                }
            }
            else if (File.Exists(redactedPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(redactedPath);
                    List<OperationHistoryEntry>? loaded = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _operationHistory.AddRange(loaded.OrderByDescending(entry => entry.TimestampUtc));
                    }
                }
                catch
                {
                    _operationHistory.Clear();
                }
            }
        }

        private async Task AppendBridgeHistoryAsync(string operation, ProcessingRunOptions options, List<FileOperationResult> results, bool cancelled)
        {
            if (_preferences.IncognitoMode)
            {
                _operationHistory.Clear();
                await SaveHistoryAsync();
                return;
            }

            OperationMetricsSummary metrics = OperationHistoryMetrics.Calculate(results);
            _operationHistory.Insert(0, new OperationHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow,
                Operation = operation,
                ProfileName = options.ProfileName,
                Algorithm = options.Algorithm,
                Mode = options.Mode,
                KeySizeBits = options.KeySizeBits,
                UsedKeyfile = options.KeyfileBytes is { Length: > 0 },
                RemoveOriginalsAfterSuccess = options.RemoveOriginalsAfterSuccess,
                SecureDeleteOriginals = options.SecureDeleteOriginals,
                VerifyAfterWrite = options.VerifyAfterWrite,
                BackupFolderPath = options.BackupFolderPath,
                Cancelled = cancelled,
                SuccessCount = results.Count(result => string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.Equals(result.Status, "Verified", StringComparison.OrdinalIgnoreCase)),
                FailureCount = results.Count(result => string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase)),
                TotalOriginalSizeBytes = metrics.TotalOriginalSizeBytes,
                TotalOutputSizeBytes = metrics.TotalOutputSizeBytes,
                TotalStorageSavedBytes = metrics.TotalStorageSavedBytes,
                TotalStorageAddedBytes = metrics.TotalStorageAddedBytes,
                ElapsedMilliseconds = metrics.ElapsedMilliseconds,
                CompressionRequestedCount = metrics.CompressionRequestedCount,
                CompressionAppliedCount = metrics.CompressionAppliedCount,
                CompressionSkippedCount = metrics.CompressionSkippedCount,
                FailureCategorySummary = metrics.FailureCategorySummary,
                Results = results
            });

            while (_operationHistory.Count > MaxHistoryEntries)
            {
                _operationHistory.RemoveAt(_operationHistory.Count - 1);
            }

            await SaveHistoryAsync();
        }

        private ProcessingRunOptions CreateHistoryOnlyRunOptions(string profileName, string algorithm)
        {
            return new ProcessingRunOptions(
                false,
                false,
                false,
                algorithm,
                profileName,
                algorithm.Contains("512", StringComparison.OrdinalIgnoreCase) ? 512 : 256,
                false,
                false,
                false,
                false,
                string.Empty,
                false,
                string.Empty,
                true,
                false,
                false,
                _preferences.OutputTimestampPolicy,
                string.Empty,
                null,
                null,
                null,
                profileName,
                new MetadataOverridesSnapshot(string.Empty, string.Empty, false, string.Empty, string.Empty));
        }

        private object BuildDashboardPayload()
        {
            DashboardStats stats = BuildDashboardStats();
            bool hideActivity = _preferences.IncognitoMode;
            object[] recentFiles = hideActivity
                ? Array.Empty<object>()
                : BuildDashboardRecentFilesFromHistory().Take(5).Select(item => (object)new
                {
                    item.Name,
                    item.FileIconText,
                    item.Type,
                    item.Status,
                    item.LastModified
                }).ToArray();
            object[] history = hideActivity
                ? Array.Empty<object>()
                : _operationHistory.Take(10).Select(ToHistoryDto).ToArray();

            return new
            {
                incognitoMode = _preferences.IncognitoMode,
                protectedFilesCount = stats.ProtectedFilesCount,
                protectedFilesDeltaText = stats.ProtectedFilesDeltaText,
                protectedFilesSubtitle = stats.ProtectedFilesSubtitle,
                storageSavedDisplay = stats.StorageSavedDisplay,
                storageSavedDeltaText = stats.StorageSavedDeltaText,
                storageSavedSubtitle = stats.StorageSavedSubtitle,
                storageSavedBytes = stats.StorageSavedBytes,
                storageAddedBytes = stats.StorageAddedBytes,
                storageTrackedFiles = stats.StorageTrackedFiles,
                compressionRequestedCount = stats.CompressionRequestedCount,
                compressionAppliedCount = stats.CompressionAppliedCount,
                storageBreakdown = stats.StorageBreakdown.Select(item => new
                {
                    label = item.Label,
                    bytes = item.Bytes,
                    display = item.Display,
                    percent = item.Percent,
                    tone = item.Tone
                }).ToArray(),
                operationsThisWeekCount = stats.OperationsThisWeekCount,
                successfulOperationsThisWeekCount = stats.SuccessfulOperationsThisWeekCount,
                failedOperationsThisWeekCount = stats.FailedOperationsThisWeekCount,
                operationsThisWeek = stats.OperationsThisWeek.Select(bucket => new
                {
                    date = bucket.Date,
                    label = bucket.Label,
                    count = bucket.Count,
                    failedCount = bucket.FailedCount
                }).ToArray(),
                lastOperationName = stats.LastOperationName,
                lastOperationFileName = stats.LastOperationFileName,
                lastOperationTimeDisplay = stats.LastOperationTimeDisplay,
                securityStatusTitle = stats.SecurityStatusTitle,
                securityStatusSubtitle = stats.SecurityStatusSubtitle,
                securityStatusDetail = stats.SecurityStatusDetail,
                recentFiles,
                history
            };
        }

        private static object ToHistoryDto(OperationHistoryEntry entry)
        {
            return new
            {
                entry.Id,
                entry.TimestampUtc,
                entry.Operation,
                entry.ProfileName,
                entry.SuccessCount,
                entry.FailureCount,
                entry.Cancelled,
                entry.ElapsedMilliseconds,
                results = entry.Results.Take(8).Select(ToResultDto).ToArray()
            };
        }

        private static object ToResultDto(FileOperationResult result)
        {
            return new
            {
                result.SourcePath,
                result.OutputPath,
                result.BackupPath,
                result.Status,
                result.Message,
                result.OriginalRetained,
                result.OutputVerified,
                result.OriginalSizeBytes,
                result.OutputSizeBytes,
                result.CompressionRequested,
                result.CompressionApplied,
                result.CompressionReason,
                result.EstimatedCompressedSizeBytes,
                result.CompressedSizeBytes,
                result.ElapsedMilliseconds,
                result.FailureCategory,
                result.HashValue
            };
        }

        private static object ToUpdateReleaseDto(UpdateReleaseInfo release)
        {
            return new
            {
                version = release.Version.ToString(),
                release.DisplayVersion,
                release.TagName,
                release.HtmlUrl,
                release.Notes,
                release.InstallerFileName,
                release.InstallerDownloadUrl,
                release.Sha256DigestHex,
                release.Sha256DigestDownloadUrl
            };
        }

        private void ApplyPreferencesDto(PreferencesDto? dto)
        {
            dto ??= new PreferencesDto();
            _preferences.IncognitoMode = dto.IncognitoMode;
            _preferences.IncludeFullPathsInExports = dto.IncludeFullPathsInExports;
            _preferences.OutputTimestampPolicy = string.IsNullOrWhiteSpace(dto.OutputTimestampPolicy) ? "Current time" : dto.OutputTimestampPolicy;
            _preferences.UseCustomEncryptOutputDirectory = dto.UseCustomEncryptOutputDirectory;
            _preferences.CustomEncryptOutputDirectory = dto.CustomEncryptOutputDirectory ?? string.Empty;
            _preferences.UseCustomDecryptOutputDirectory = dto.UseCustomDecryptOutputDirectory;
            _preferences.CustomDecryptOutputDirectory = dto.CustomDecryptOutputDirectory ?? string.Empty;
            _preferences.ThemePreference = ParseEnum(dto.ThemePreference, ThemePreference.Dark);
            _currentExperienceLevel = UserExperienceLevel.Advanced;
            _themePreference = _preferences.ThemePreference;
            isDarkTheme = _themePreference != ThemePreference.Light;
            ApplyWindowTitleBarColors();
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
        }

        internal static string RequireExistingFile(string? path)
        {
            string safePath = RequireExistingPath(path);
            if (!File.Exists(safePath))
            {
                throw new FileNotFoundException("The selected file could not be found.", safePath);
            }

            return safePath;
        }

        internal static string RequireExistingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("A file or folder path is required.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException("The selected path is not valid.", ex);
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new FileNotFoundException("The selected path could not be found.", fullPath);
            }

            return fullPath;
        }

        private static string NormalizeBridgeHashAlgorithm(string? algorithm)
        {
            return string.Equals(algorithm, "SHA-512", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(algorithm, "SHA512", StringComparison.OrdinalIgnoreCase)
                ? "SHA-512"
                : "SHA-256";
        }

        private static string NormalizeEncodeFormat(string? format)
        {
            return format switch
            {
                "URL" => "URL",
                "Hex" => "Hex",
                "HTML Entities" => "HTML Entities",
                "UTF-8" => "UTF-8",
                _ => "Base64"
            };
        }

        private static object SuggestEncryptOutputFromBridge(PathListRequest request)
        {
            string[] selectedPaths = NormalizeBridgePathList(request.Paths);

            string? suggestedPath = EncryptOutputPathAdvisor.SuggestForSelectedPaths(selectedPaths);
            int folderCount = selectedPaths.Count(Directory.Exists);

            return new
            {
                suggestedPath,
                hasFolderSelection = folderCount > 0,
                folderCount
            };
        }

        private object DescribePathsFromBridge(PathListRequest request)
        {
            string[] selectedPaths = NormalizeBridgePathList(request.Paths, fullPaths: true);

            if (selectedPaths.Length == 0)
            {
                return new
                {
                    items = Array.Empty<object>(),
                    totalSizeBytes = 0L,
                    totalSizeDisplay = FormatFileSize(0),
                    warnings = Array.Empty<string>()
                };
            }

            QueueExpandResult expansion = ExpandQueuePaths(selectedPaths);
            Dictionary<string, (long SizeBytes, int FileCount)> folderMetrics = expansion.Files
                .GroupBy(file => file.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (group.Sum(file => file.SizeBytes), group.Count()),
                    StringComparer.OrdinalIgnoreCase);

            List<object> items = [];
            long totalSizeBytes = 0;

            foreach (string selectedPath in selectedPaths)
            {
                if (File.Exists(selectedPath))
                {
                    var fileInfo = new FileInfo(selectedPath);
                    long sizeBytes = fileInfo.Length;
                    totalSizeBytes += sizeBytes;
                    items.Add(new
                    {
                        fullPath = fileInfo.FullName,
                        displayName = fileInfo.Name,
                        itemType = GetBridgePathTypeDisplay(fileInfo.FullName, isDirectory: false),
                        sizeBytes,
                        sizeDisplay = FormatFileSize(sizeBytes),
                        isDirectory = false,
                        details = "Ready to encrypt"
                    });
                    continue;
                }

                if (Directory.Exists(selectedPath))
                {
                    (long sizeBytes, int fileCount) = folderMetrics.TryGetValue(selectedPath, out var metrics)
                        ? metrics
                        : (0L, 0);
                    totalSizeBytes += sizeBytes;
                    items.Add(new
                    {
                        fullPath = selectedPath,
                        displayName = Path.GetFileName(selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        itemType = "Folder",
                        sizeBytes,
                        sizeDisplay = fileCount > 0 ? FormatFileSize(sizeBytes) : "Calculated at run start",
                        isDirectory = true,
                        details = fileCount > 0 ? $"{fileCount} file(s) will be queued" : "Folder contents checked at run start"
                    });
                }
            }

            return new
            {
                items = items.ToArray(),
                totalSizeBytes,
                totalSizeDisplay = FormatFileSize(totalSizeBytes),
                warnings = expansion.Warnings.ToArray()
            };
        }

        internal static string[] NormalizeBridgePathList(string[]? paths, bool fullPaths = false)
        {
            string[] normalizedPaths = NormalizeBridgeStringList(paths);
            if (normalizedPaths.Length == 0)
            {
                return [];
            }

            if (fullPaths)
            {
                return normalizedPaths
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return normalizedPaths;
        }

        internal static string[] NormalizeBridgeStringList(string[]? values)
        {
            if (values is not { Length: > 0 })
            {
                return [];
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();
        }

        private static string GetBridgePathTypeDisplay(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                return "Folder";
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => "PDF Document",
                ".doc" or ".docx" => "Word Document",
                ".xls" or ".xlsx" => "Excel Workbook",
                ".ppt" or ".pptx" => "PowerPoint Deck",
                ".zip" => "ZIP Archive",
                ".txt" => "Text File",
                ".jpg" or ".jpeg" => "JPG Image",
                ".png" => "PNG Image",
                ".mp4" => "MP4 Video",
                ".locked" => "Locked File",
                "" => "File",
                _ => $"{extension.TrimStart('.').ToUpperInvariant()} File"
            };
        }

        private sealed class BridgeRequest
        {
            public string Id { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
            public JsonElement Payload { get; set; }
        }

        private sealed record BridgeResponse(string Id, bool Ok, object? Result, BridgeError? Error);

        private sealed record BridgeError(string Code, string Message);

        private sealed record MetadataPreviewDto(string Label, string BeforeValue, string AfterValue);

        private sealed record MetadataCategorySnapshot(
            string Name,
            string Description,
            bool IsSelected,
            bool IsSupported);

        private sealed record MetadataCategoryDto(
            string Name,
            string Description,
            bool IsSelected,
            bool IsSupported,
            int DetectedCount);

        private sealed class RevealPathRequest
        {
            public string Path { get; set; } = string.Empty;
        }

        private sealed class PathListRequest
        {
            public string[] Paths { get; set; } = [];
        }

        private sealed class OpenExternalRequest
        {
            public string Url { get; set; } = string.Empty;
        }

        private sealed class FileOperationRequest
        {
            public string OperationId { get; set; } = string.Empty;
            public string[] Paths { get; set; } = [];
            public string Password { get; set; } = string.Empty;
            public string? KeyfilePath { get; set; }
            public string? RecoveryKey { get; set; }
            public bool CompressFiles { get; set; } = true;
            public bool ScrambleNames { get; set; }
            public bool UseSteganography { get; set; }
            public bool PackageFolders { get; set; }
            public bool RemoveOriginalsAfterSuccess { get; set; }
            public bool SecureDeleteOriginals { get; set; }
            public bool VerifyAfterWrite { get; set; } = true;
            public bool SaveNextToSource { get; set; } = true;
            public string? EncryptOutputDirectory { get; set; }
            public bool SaveNextToEncrypted { get; set; } = true;
            public string? DecryptOutputDirectory { get; set; }
            public bool RestoreOriginalFilenames { get; set; } = true;
            public bool PreserveFolderStructure { get; set; } = true;
            public string OutputTimestampPolicy { get; set; } = "Current time";
            public string? BackupFolderPath { get; set; }
            public string ProfileName { get; set; } = "FileLocker";
            public string? MetadataLabel { get; set; }
            public string? MetadataNotes { get; set; }
            public bool RandomizeMetadata { get; set; }
            public string? MetadataCreatedText { get; set; }
            public string? MetadataModifiedText { get; set; }
            public string? DeleteConfirmation { get; set; }
        }

        private sealed class HashComputeRequest
        {
            public string OperationId { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Algorithm { get; set; } = "SHA-256";
        }

        private sealed class HashVerifyRequest
        {
            public string GeneratedHash { get; set; } = string.Empty;
            public string ExpectedHash { get; set; } = string.Empty;
        }

        private sealed class HashManifestCreateRequest
        {
            public string[] Paths { get; set; } = [];
            public string Algorithm { get; set; } = "SHA-256";
        }

        private sealed class HashManifestVerifyRequest
        {
            public string ManifestPath { get; set; } = string.Empty;
            public string RootDirectory { get; set; } = string.Empty;
        }

        private sealed class TextConvertRequest
        {
            public string Mode { get; set; } = "encode";
            public string Format { get; set; } = "Base64";
            public string Input { get; set; } = string.Empty;
            public bool PreserveLineBreaks { get; set; } = true;
        }

        private sealed class MetadataInspectRequest
        {
            public string Path { get; set; } = string.Empty;
            public string[] Paths { get; set; } = [];
            public string Mode { get; set; } = "Remove metadata";
            public string[] SelectedCategories { get; set; } = [];
        }

        private sealed class SecureDeleteRequest
        {
            public string[] Paths { get; set; } = [];
            public string Method { get; set; } = "dod";
            public int OverwritePasses { get; set; } = 3;
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class TitlePageRequest
        {
            public string PageName { get; set; } = "Dashboard";
        }

        private sealed class RestartAsAdministratorRequest
        {
            public string TargetPage { get; set; } = string.Empty;
        }

        private sealed class MaintenanceCleanupRequest
        {
            public string[] CategoryIds { get; set; } = [];
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class MaintenanceDriveActionRequest
        {
            public string DriveRoot { get; set; } = string.Empty;
            public string Mode { get; set; } = "analyze";
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class RegistryCleanRequest
        {
            public string[] IssueIds { get; set; } = [];
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class StartupToggleRequest
        {
            public string ItemId { get; set; } = string.Empty;
            public bool Enabled { get; set; }
        }

        private sealed class UninstallerLaunchRequest
        {
            public string AppId { get; set; } = string.Empty;
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class AppLeftoverRequest
        {
            public string[] AppIds { get; set; } = [];
            public string[] CategoryIds { get; set; } = [];
            public string Confirmation { get; set; } = string.Empty;
        }

        private sealed class ExplorerIntegrationRequest
        {
            public bool Enabled { get; set; }
        }

        private sealed class HistoryExportRequest
        {
            public string Format { get; set; } = "json";
        }

        private sealed class UpdateSkipRequest
        {
            public string Version { get; set; } = string.Empty;
        }

        private sealed class SettingsSaveRequest
        {
            public PreferencesDto? Preferences { get; set; }
            public UpdateSettingsDto? Updates { get; set; }
        }

        private sealed class PreferencesDto
        {
            public bool IncognitoMode { get; set; }
            public bool IncludeFullPathsInExports { get; set; }
            public string OutputTimestampPolicy { get; set; } = "Current time";
            public bool UseCustomEncryptOutputDirectory { get; set; }
            public string CustomEncryptOutputDirectory { get; set; } = string.Empty;
            public bool UseCustomDecryptOutputDirectory { get; set; } = true;
            public string CustomDecryptOutputDirectory { get; set; } = string.Empty;
            public string ThemePreference { get; set; } = nameof(FileLocker.ThemePreference.Dark);
        }

        private sealed class UpdateSettingsDto
        {
            public bool AutoCheckEnabled { get; set; } = true;
            public string? SkippedVersion { get; set; }
        }

        private sealed record DownloadedUpdateDto(
            string InstallerPath,
            string FileName,
            object Release);

    }
}

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

        private IReadOnlyList<string> _launchPaths = [];
        private string? _launchAction;

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
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }

        private async void AppWebView_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;

            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            string[] paths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();

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
                "files.pickFiles" => PickFilesAsync(),
                "files.pickFolder" => PickFolderAsync(),
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
                "text.convert" => Task.FromResult<object?>(ConvertTextFromBridge(ReadPayload<TextConvertRequest>(request.Payload))),
                "metadata.inspect" => Task.FromResult<object?>(InspectMetadataFromBridge(ReadPayload<MetadataInspectRequest>(request.Payload))),
                "secureDelete.delete" => SecureDeleteFromBridgeAsync(ReadPayload<SecureDeleteRequest>(request.Payload)),
                "settings.get" => Task.FromResult<object?>(BuildSettingsPayload()),
                "settings.save" => SaveSettingsFromBridgeAsync(ReadPayload<SettingsSaveRequest>(request.Payload)),
                "settings.reset" => ResetSettingsFromBridgeAsync(),
                "shell.setExplorerIntegration" => Task.FromResult<object?>(SetExplorerIntegrationFromBridge(ReadPayload<ExplorerIntegrationRequest>(request.Payload))),
                "updates.check" => CheckForUpdatesFromBridgeAsync(),
                "updates.download" => DownloadUpdateFromBridgeAsync(),
                "updates.install" => InstallUpdateFromBridgeAsync(),
                "updates.skip" => Task.FromResult<object?>(SkipUpdateFromBridge(ReadPayload<UpdateSkipRequest>(request.Payload))),
                "updates.clearSkip" => Task.FromResult<object?>(ClearSkippedUpdateFromBridge()),
                "history.clear" => ClearHistoryFromBridgeAsync(),
                "history.export" => ExportHistoryFromBridgeAsync(ReadPayload<HistoryExportRequest>(request.Payload)),
                _ => throw new InvalidOperationException($"Unknown bridge action '{request.Action}'.")
            };
        }

        private static T ReadPayload<T>(JsonElement payload)
        {
            T? value = JsonSerializer.Deserialize<T>(payload.GetRawText(), BridgeJsonOptions);
            return value ?? throw new InvalidOperationException("Bridge payload was empty or invalid.");
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
            if (AppWebView?.CoreWebView2 == null)
            {
                return;
            }

            string json = JsonSerializer.Serialize(payload, BridgeJsonOptions);
            AppWebView.CoreWebView2.PostWebMessageAsJson(json);
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
                    launchAction = _launchAction
                },
                dashboard = BuildDashboardPayload(),
                settings = BuildSettingsPayload()
            };
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
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is not ("https" or "http"))
            {
                throw new InvalidOperationException("Only HTTP or HTTPS links can be opened.");
            }

            OpenWithShell(uri.ToString());
            return Task.FromResult<object?>(new { opened = true, url = uri.ToString() });
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
            if (request.Paths.Length == 0)
            {
                throw new InvalidOperationException("Select at least one file or folder.");
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                throw new InvalidOperationException(intent == ProcessingIntent.Encrypt
                    ? "Enter a password before encrypting."
                    : "Enter the unlock password.");
            }

            ProcessingRunOptions runOptions = CreateRunOptionsFromBridge(request);
            if (runOptions.RemoveOriginalsAfterSuccess &&
                request.Paths.Any(path => Directory.Exists(path)) &&
                !string.Equals(request.DeleteConfirmation, "DELETE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Folder-wide source removal requires typing DELETE before the run starts.");
            }

            var results = new List<FileOperationResult>();
            var failedPaths = new List<string>();
            bool cancelled = false;

            try
            {
                _processingCancellation = new CancellationTokenSource();
                QueueExpandResult expansion = ExpandQueuePaths(request.Paths);
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
                    if (_processingCancellation.IsCancellationRequested)
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
                if (runOptions.KeyfileBytes is { Length: > 0 } keyfileBytes)
                {
                    CryptographicOperations.ZeroMemory(keyfileBytes);
                }

                _processingCancellation?.Dispose();
                _processingCancellation = null;
            }
        }

        private ProcessingRunOptions CreateRunOptionsFromBridge(FileOperationRequest request)
        {
            string keyfilePath = request.KeyfilePath?.Trim() ?? string.Empty;
            byte[]? keyfileBytes = ReadKeyfileBytesIfConfigured(keyfilePath);
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
            string generated = NormalizeHashInput(request.GeneratedHash);
            string expected = NormalizeHashInput(request.ExpectedHash);
            if (string.IsNullOrWhiteSpace(generated) || string.IsNullOrWhiteSpace(expected))
            {
                throw new InvalidOperationException("Both generated and expected hashes are required.");
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
            if (request.Paths.Length == 0)
            {
                throw new InvalidOperationException("Select at least one file or folder for the hash manifest.");
            }

            string outputDirectory = Path.Combine(GetAppDataDirectory(), "Manifests");
            HashManifestResult manifest = await HashManifestService.CreateManifestAsync(
                request.Paths,
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

        private static object InspectMetadataFromBridge(MetadataInspectRequest request)
        {
            string path = RequireExistingFile(request.Path);
            var fileInfo = new FileInfo(path);
            string extension = fileInfo.Extension.ToLowerInvariant();
            string mode = string.IsNullOrWhiteSpace(request.Mode) ? "Remove metadata" : request.Mode;
            string timestampAction = string.Equals(mode, "Randomize metadata", StringComparison.OrdinalIgnoreCase)
                ? "Preview only"
                : "Preview only";

            var preview = new[]
            {
                new MetadataPreviewDto("File name", fileInfo.Name, "Preserved in preview"),
                new MetadataPreviewDto("File type", GetMetadataFileTypeDisplay(extension), "Preserved in preview"),
                new MetadataPreviewDto("Size", FormatFileSize(fileInfo.Length), "Preserved in preview"),
                new MetadataPreviewDto("Created", FormatMetadataDate(fileInfo.CreationTime), timestampAction),
                new MetadataPreviewDto("Modified", FormatMetadataDate(fileInfo.LastWriteTime), timestampAction),
                new MetadataPreviewDto("Last accessed", FormatMetadataDate(fileInfo.LastAccessTime), timestampAction),
                new MetadataPreviewDto("Attributes", fileInfo.Attributes.ToString(), "Preserved in preview")
            };

            return new
            {
                file = new
                {
                    displayName = fileInfo.Name,
                    fullPath = fileInfo.FullName,
                    fileType = GetMetadataFileTypeDisplay(extension),
                    sizeDisplay = FormatFileSize(fileInfo.Length),
                    metadataTagCount = CountBasicMetadataFields(fileInfo),
                    statusDisplay = IsCommonMetadataFileType(extension) ? "Ready" : "Review",
                    isSupported = IsCommonMetadataFileType(extension)
                },
                mode,
                writeSupportEnabled = false,
                preview,
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

        private async Task<object?> SecureDeleteFromBridgeAsync(SecureDeleteRequest request)
        {
            if (request.Paths.Length == 0)
            {
                throw new InvalidOperationException("Select at least one file or folder to delete.");
            }

            var results = new List<FileOperationResult>();
            foreach (string rawPath in request.Paths)
            {
                string path = RequireExistingPath(rawPath);
                var elapsed = Stopwatch.StartNew();
                try
                {
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(path))
                        {
                            DeleteSourceDirectory(path, secureDelete: true);
                        }
                        else
                        {
                            SecureDelete(path);
                        }
                    });

                    results.Add(new FileOperationResult
                    {
                        SourcePath = path,
                        Status = "Completed",
                        Message = "Secure delete completed with best-effort overwrite.",
                        OriginalRetained = false,
                        OutputVerified = false,
                        ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new FileOperationResult
                    {
                        SourcePath = path,
                        Status = "Failed",
                        Message = GetFriendlyExceptionMessage(ex, "Secure delete failed."),
                        OriginalRetained = true,
                        OutputVerified = false,
                        FailureCategory = OperationFailureClassifier.Classify(ex),
                        ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                    });
                }
            }

            await AppendBridgeHistoryAsync("Secure Delete", CreateHistoryOnlyRunOptions("Secure Delete", "Best-effort overwrite"), results, cancelled: false);
            return new
            {
                results = results.Select(ToResultDto).ToArray(),
                dashboard = BuildDashboardPayload()
            };
        }

        private object BuildSettingsPayload()
        {
            return new
            {
                preferences = new
                {
                    _preferences.HasSelectedExperienceLevel,
                    ExperienceLevel = _preferences.ExperienceLevel.ToString(),
                    HistoryPrivacyMode = _preferences.HistoryPrivacyMode.ToString(),
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
            return new DownloadedUpdateDto(
                installerPath,
                Path.GetFileName(installerPath),
                ToUpdateReleaseDto(result.Release));
        }

        private async Task<object?> InstallUpdateFromBridgeAsync()
        {
            DownloadedUpdateDto downloadedUpdate = await DownloadUpdateInstallerAsync();
            OpenWithShell(downloadedUpdate.InstallerPath);
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
            string exportPath = Path.Combine(exportsDirectory, fileName);
            bool includeFullPaths = _preferences.IncludeFullPathsInExports;
            string content = format == "csv"
                ? OperationHistoryExporter.ExportCsv(_operationHistory, includeFullPaths)
                : OperationHistoryExporter.ExportJson(_operationHistory, includeFullPaths);

            await File.WriteAllTextAsync(exportPath, content, Encoding.UTF8);
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
            return new
            {
                protectedFilesCount = stats.ProtectedFilesCount,
                protectedFilesDeltaText = stats.ProtectedFilesDeltaText,
                protectedFilesSubtitle = stats.ProtectedFilesSubtitle,
                storageSavedDisplay = stats.StorageSavedDisplay,
                storageSavedDeltaText = stats.StorageSavedDeltaText,
                storageSavedSubtitle = stats.StorageSavedSubtitle,
                lastOperationName = stats.LastOperationName,
                lastOperationFileName = stats.LastOperationFileName,
                lastOperationTimeDisplay = stats.LastOperationTimeDisplay,
                securityStatusTitle = stats.SecurityStatusTitle,
                securityStatusSubtitle = stats.SecurityStatusSubtitle,
                securityStatusDetail = stats.SecurityStatusDetail,
                recentFiles = BuildDashboardRecentFilesFromHistory().Take(5).Select(item => new
                {
                    item.Name,
                    item.FileIconText,
                    item.Type,
                    item.Status,
                    item.LastModified
                }).ToArray(),
                history = _operationHistory.Take(10).Select(ToHistoryDto).ToArray()
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
            _preferences.HasSelectedExperienceLevel = dto.HasSelectedExperienceLevel;
            _preferences.ExperienceLevel = ParseEnum(dto.ExperienceLevel, UserExperienceLevel.Beginner);
            _preferences.HistoryPrivacyMode = ParseEnum(dto.HistoryPrivacyMode, HistoryPrivacyMode.Redacted);
            _preferences.IncludeFullPathsInExports = dto.IncludeFullPathsInExports;
            _preferences.OutputTimestampPolicy = string.IsNullOrWhiteSpace(dto.OutputTimestampPolicy) ? "Current time" : dto.OutputTimestampPolicy;
            _preferences.UseCustomEncryptOutputDirectory = dto.UseCustomEncryptOutputDirectory;
            _preferences.CustomEncryptOutputDirectory = dto.CustomEncryptOutputDirectory ?? string.Empty;
            _preferences.UseCustomDecryptOutputDirectory = dto.UseCustomDecryptOutputDirectory;
            _preferences.CustomDecryptOutputDirectory = dto.CustomDecryptOutputDirectory ?? string.Empty;
            _preferences.ThemePreference = ParseEnum(dto.ThemePreference, ThemePreference.Dark);
            _currentExperienceLevel = _preferences.ExperienceLevel;
            _themePreference = _preferences.ThemePreference;
            isDarkTheme = _themePreference != ThemePreference.Light;
            ApplyWindowTitleBarColors();
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
        }

        private static string RequireExistingFile(string? path)
        {
            string safePath = RequireExistingPath(path);
            if (!File.Exists(safePath))
            {
                throw new FileNotFoundException("The selected file could not be found.", safePath);
            }

            return safePath;
        }

        private static string RequireExistingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("A file or folder path is required.");
            }

            string fullPath = Path.GetFullPath(path.Trim());
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
            string[] selectedPaths = request.Paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .ToArray();

            string? suggestedPath = EncryptOutputPathAdvisor.SuggestForSelectedPaths(selectedPaths);
            int folderCount = selectedPaths.Count(Directory.Exists);

            return new
            {
                suggestedPath,
                hasFolderSelection = folderCount > 0,
                folderCount
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
            public string Mode { get; set; } = "Remove metadata";
        }

        private sealed class SecureDeleteRequest
        {
            public string[] Paths { get; set; } = [];
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
            public bool HasSelectedExperienceLevel { get; set; } = true;
            public string ExperienceLevel { get; set; } = nameof(UserExperienceLevel.Advanced);
            public string HistoryPrivacyMode { get; set; } = nameof(FileLocker.HistoryPrivacyMode.Redacted);
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

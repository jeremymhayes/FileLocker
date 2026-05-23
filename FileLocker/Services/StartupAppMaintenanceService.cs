using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FileLocker;

internal static class StartupAppMaintenanceService
{
    private const int MaxLeftoverScanFiles = 75_000;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExecutableExtensions = [".exe", ".bat", ".cmd", ".com", ".msi"];
    private static readonly HashSet<string> SilentUninstallSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "q",
        "qn",
        "qb",
        "quiet",
        "s",
        "silent",
        "verysilent",
        "passive"
    };

    private static readonly string[] SafeLeftoverDirectoryTokens =
    [
        "cache",
        "caches",
        "code cache",
        "gpucache",
        "gpu cache",
        "shadercache",
        "shader cache",
        "logs",
        "log",
        "temp",
        "tmp",
        "crashdumps",
        "crash dumps"
    ];

    private static void AddWarning(ICollection<string>? warnings, string message)
    {
        warnings?.Add(SensitiveDataRedactor.RedactMessage(message));
    }

    internal static StartupScanResult ScanStartup()
    {
        var warnings = new List<string>();
        StartupItem[] enabledItems = EnumerateEnabledStartupEntries(warnings)
            .Select(entry => entry.item)
            .ToArray();

        StartupItem[] disabledItems = LoadDisabledStartupMetadata(warnings)
            .Select(ToDisabledStartupItem)
            .ToArray();

        StartupItem[] items = enabledItems
            .Concat(disabledItems)
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.isEnabled).First())
            .OrderBy(item => item.source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StartupScanResult(
            items,
            items.Count(item => item.isEnabled),
            items.Count(item => !item.isEnabled),
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static StartupToggleResult SetStartupEnabled(string? itemId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("Select a startup item first.");
        }

        string normalizedId = itemId.Trim();
        if (enabled)
        {
            return EnableStartupItem(normalizedId);
        }

        return DisableStartupItem(normalizedId);
    }

    internal static InstalledAppsScanResult ScanInstalledApps()
    {
        var warnings = new List<string>();
        InstalledApp[] apps = DeduplicateInstalledApps(EnumerateInstalledApps(warnings))
            .OrderBy(app => app.displayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.publisher, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new InstalledAppsScanResult(apps, apps.Length, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static UninstallerLaunchResult LaunchUninstaller(string? appId, string? confirmation)
    {
        if (!string.Equals(confirmation, "UNINSTALL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm the uninstall launch before opening the vendor uninstaller.");
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("Select an installed app first.");
        }

        InstalledApp app = ScanInstalledApps().apps
            .FirstOrDefault(item => string.Equals(item.id, appId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected app could not be found.");

        if (string.IsNullOrWhiteSpace(app.uninstallCommand))
        {
            throw new InvalidOperationException("This app does not publish an uninstall command.");
        }

        if (ContainsSilentUninstallSwitch(app.uninstallCommand))
        {
            throw new InvalidOperationException("FileLocker does not launch uninstall commands that include silent or quiet switches.");
        }

        ProcessCommand command = ParseProcessCommand(app.uninstallCommand);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.fileName,
            Arguments = command.arguments,
            UseShellExecute = true
        };

        if (app.requiresAdministrator)
        {
            startInfo.Verb = "runas";
        }

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Windows did not start the vendor uninstaller.");
        }

        return new UninstallerLaunchResult(true, app.id, app.displayName, "Opened the app vendor's uninstaller.");
    }

    internal static AppLeftoverScanResult ScanAppLeftovers(IReadOnlyCollection<string>? appIds)
    {
        HashSet<string> selectedAppIds = NormalizeIds(appIds);
        InstalledAppsScanResult installedAppsScan = ScanInstalledApps();
        InstalledApp[] installedApps = installedAppsScan.apps;
        InstalledApp[] apps = installedApps
            .Where(app => selectedAppIds.Count == 0 || selectedAppIds.Contains(app.id))
            .Take(selectedAppIds.Count == 0 ? 80 : int.MaxValue)
            .ToArray();

        var warnings = new List<string>(installedAppsScan.warnings);
        if (selectedAppIds.Count > 0)
        {
            HashSet<string> foundAppIds = apps.Select(app => app.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string missingAppId in selectedAppIds.Where(appId => !foundAppIds.Contains(appId)))
            {
                warnings.Add($"The selected app could not be found for leftover scanning: {missingAppId}");
            }
        }

        AppLeftoverCategory[] categories = apps
            .SelectMany(app => ScanLeftoversForApp(app, warnings))
            .GroupBy(category => category.id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(category => category.appDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(category => category.group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(category => category.path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AppLeftoverScanResult(
            categories,
            categories.Sum(category => category.sizeBytes),
            FormatFileSize(categories.Sum(category => category.sizeBytes)),
            categories.Sum(category => category.fileCount),
            categories.Sum(category => category.skippedCount),
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static AppLeftoverCleanResult CleanAppLeftovers(
        IReadOnlyCollection<string>? appIds,
        IReadOnlyCollection<string>? categoryIds,
        string? confirmation)
    {
        if (!string.Equals(confirmation, "CLEAN LEFTOVERS", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm app leftover cleanup before deleting selected app data.");
        }

        HashSet<string> selectedCategoryIds = NormalizeIds(categoryIds);
        if (selectedCategoryIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one app leftover category.");
        }

        AppLeftoverScanResult scan = ScanAppLeftovers(appIds);
        AppLeftoverCategory[] selectedCategories = scan.categories
            .Where(category => selectedCategoryIds.Contains(category.id))
            .ToArray();

        if (selectedCategories.Length != selectedCategoryIds.Count)
        {
            throw new InvalidOperationException("The selected app leftover categories are no longer available.");
        }

        if (selectedCategories.Any(category => category.requiresAdministrator))
        {
            RequireAdministrator("Cleaning ProgramData app leftovers");
        }

        var cleaned = new List<AppLeftoverCategory>();
        var failures = new List<AppLeftoverCleanFailure>();
        long freedBytes = 0;

        foreach (AppLeftoverCategory category in selectedCategories)
        {
            try
            {
                if (!category.isEnabled)
                {
                    throw new InvalidOperationException("The selected cleanup area is not currently available.");
                }

                if (!IsApprovedAppLeftoverPath(category.path))
                {
                    throw new InvalidOperationException("The selected path is outside approved app cleanup roots.");
                }

                long categoryBytes = category.sizeBytes;
                DeleteLeftoverPath(category.path);
                freedBytes += categoryBytes;
                cleaned.Add(category);
            }
            catch (Exception ex)
            {
                failures.Add(new AppLeftoverCleanFailure(
                    category.id,
                    category.appDisplayName,
                    category.path,
                    SensitiveDataRedactor.RedactMessage(ex.Message)));
            }
        }

        return new AppLeftoverCleanResult(
            cleaned.Count,
            failures.Count,
            freedBytes,
            FormatFileSize(freedBytes),
            cleaned.ToArray(),
            failures.ToArray(),
            ScanAppLeftovers(appIds));
    }

    internal static string? ParseStartupCommandTargetPath(string? command)
    {
        string? normalized = RegistryPathNormalizer.Normalize(command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.StartsWith('"'))
        {
            int closingQuote = normalized.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return normalized[1..closingQuote];
            }
        }

        int executableEndIndex = FindExecutablePathEnd(normalized);
        if (executableEndIndex > 0)
        {
            return normalized[..executableEndIndex].Trim().Trim('"');
        }

        int firstSpace = normalized.IndexOf(' ');
        return firstSpace > 0 ? normalized[..firstSpace] : normalized;
    }

    private static int FindExecutablePathEnd(string value)
    {
        int bestEndIndex = -1;
        foreach (string extension in ExecutableExtensions)
        {
            int searchIndex = 0;
            while (searchIndex < value.Length)
            {
                int index = value.IndexOf(extension, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                int endIndex = index + extension.Length;
                if (IsCommandPathBoundary(value, endIndex))
                {
                    if (bestEndIndex < 0 || endIndex < bestEndIndex)
                    {
                        bestEndIndex = endIndex;
                    }

                    break;
                }

                searchIndex = index + 1;
            }
        }

        return bestEndIndex;
    }

    private static bool IsCommandPathBoundary(string value, int index)
    {
        return index >= value.Length ||
            char.IsWhiteSpace(value[index]) ||
            value[index] == '"';
    }

    internal static InstalledApp[] DeduplicateInstalledApps(IEnumerable<InstalledApp> apps)
    {
        return apps
            .Where(app => !string.IsNullOrWhiteSpace(app.displayName))
            .GroupBy(CreateInstalledAppDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(GetAppQualityScore).First())
            .ToArray();
    }

    internal static StartupDisableMetadata CreateRegistryStartupDisableMetadata(
        StartupItem item,
        string hive,
        string keyPath,
        string valueName,
        object? value,
        RegistryValueKind valueKind,
        string backupPath)
    {
        return new StartupDisableMetadata(
            item.id,
            item.name,
            item.source,
            item.location,
            item.command,
            item.targetPath,
            item.requiresAdministrator,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            backupPath,
            ToRegistryMetadata(hive, keyPath, valueName, value, valueKind),
            file: null);
    }

    internal static bool IsApprovedAppLeftoverPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        if (IsUnderAnyPath(fullPath, GetBlockedCleanupRoots(), includeRoot: true))
        {
            return false;
        }

        string[] approvedRoots = GetApprovedLeftoverRoots();
        return IsUnderAnyPath(fullPath, approvedRoots, includeRoot: false) &&
            !IsRootLevelGenericLeftoverFolder(fullPath, approvedRoots);
    }

    internal static bool ContainsSilentUninstallSwitch(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        ProcessCommand processCommand = ParseProcessCommand(command);
        foreach (string argument in SplitCommandLineArguments(processCommand.arguments))
        {
            string normalized = argument.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized) || normalized[0] is not ('/' or '-'))
            {
                continue;
            }

            normalized = normalized.TrimStart('/', '-').Trim();
            if (SilentUninstallSwitches.Contains(normalized) ||
                normalized.StartsWith("quiet=", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("silent=", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("qb", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("qn", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static StartupToggleResult DisableStartupItem(string itemId)
    {
        var warnings = new List<string>();
        StartupEntry entry = EnumerateEnabledStartupEntries(warnings)
            .FirstOrDefault(item => string.Equals(item.item.id, itemId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected startup item is no longer enabled.");

        if (entry.item.requiresAdministrator)
        {
            RequireAdministrator("Changing this startup item");
        }

        StartupDisableMetadata metadata;
        Action disableAction;
        if (entry.kind == StartupEntryKind.Registry)
        {
            string backupPath = WriteRegistryStartupBackup(entry);
            metadata = CreateRegistryStartupDisableMetadata(
                entry.item,
                entry.hive ?? string.Empty,
                entry.keyPath ?? string.Empty,
                entry.valueName ?? string.Empty,
                entry.registryValue,
                entry.registryValueKind ?? RegistryValueKind.String,
                backupPath);

            disableAction = () =>
            {
                RegistryKey root = GetRegistryRoot(entry.hive);
                using RegistryKey? key = root.OpenSubKey(entry.keyPath ?? string.Empty, writable: true);
                key?.DeleteValue(entry.valueName ?? string.Empty, throwOnMissingValue: false);
            };
        }
        else
        {
            string sourcePath = entry.filePath ?? throw new InvalidOperationException("The selected startup shortcut could not be found.");
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The selected startup shortcut could not be found.", sourcePath);
            }

            string disabledPath = GetAvailableDisabledStartupFilePath(entry.item.id, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
            metadata = CreateFileStartupDisableMetadata(entry.item, disabledPath);
            disableAction = () => File.Move(sourcePath, disabledPath);
        }

        List<StartupDisableMetadata> previousMetadataItems = LoadDisabledStartupMetadata();
        SaveDisabledStartupMetadata(previousMetadataItems
            .Where(item => !string.Equals(item.id, metadata.id, StringComparison.OrdinalIgnoreCase))
            .Append(metadata)
            .ToList());

        try
        {
            disableAction();
        }
        catch
        {
            TrySaveDisabledStartupMetadata(previousMetadataItems);
            throw;
        }

        StartupItem disabledItem = ToDisabledStartupItem(metadata);
        return new StartupToggleResult(
            disabledItem,
            false,
            metadata.backupPath,
            $"{entry.item.name} was disabled. FileLocker saved restore metadata first.");
    }

    private static StartupToggleResult EnableStartupItem(string itemId)
    {
        List<StartupDisableMetadata> metadataItems = LoadDisabledStartupMetadata();
        StartupDisableMetadata metadata = metadataItems
            .FirstOrDefault(item => string.Equals(item.id, itemId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("FileLocker does not have restore metadata for this startup item.");

        if (metadata.requiresAdministrator)
        {
            RequireAdministrator("Restoring this startup item");
        }

        if (metadata.registry != null)
        {
            RestoreRegistryStartupItem(metadata.registry);
        }
        else if (metadata.file != null)
        {
            RestoreStartupFolderItem(metadata.file);
        }
        else
        {
            throw new InvalidOperationException("The startup restore metadata is incomplete.");
        }

        SaveDisabledStartupMetadata(metadataItems
            .Where(item => !string.Equals(item.id, metadata.id, StringComparison.OrdinalIgnoreCase))
            .ToList());

        StartupItem restoredItem = ToRestoredStartupItem(metadata);
        return new StartupToggleResult(
            restoredItem,
            true,
            metadata.backupPath,
            $"{metadata.name} was restored to startup.");
    }

    private static IEnumerable<StartupEntry> EnumerateEnabledStartupEntries(List<string> warnings)
    {
        foreach (StartupEntry entry in EnumerateRegistryStartupEntries(Registry.CurrentUser, "HKCU", requiresAdministrator: false, warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateRegistryStartupEntries(Registry.LocalMachine, "HKLM", requiresAdministrator: true, warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Startup folder",
            requiresAdministrator: false,
            warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            "Common Startup folder",
            requiresAdministrator: true,
            warnings))
        {
            yield return entry;
        }
    }

    private static IReadOnlyList<StartupEntry> EnumerateRegistryStartupEntries(
        RegistryKey root,
        string hive,
        bool requiresAdministrator,
        List<string> warnings)
    {
        var entries = new List<StartupEntry>();
        try
        {
            using RegistryKey? key = root.OpenSubKey(RunKeyPath, writable: false);
            if (key == null)
            {
                return entries;
            }

            string[] valueNames;
            try
            {
                valueNames = key.GetValueNames();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{hive}\\{RunKeyPath}: {ex.Message}");
                return entries;
            }

            foreach (string valueName in valueNames)
            {
                object? value;
                RegistryValueKind valueKind;
                try
                {
                    value = key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    valueKind = key.GetValueKind(valueName);
                }
                catch (Exception ex)
                {
                    AddWarning(warnings, $"{hive}\\{RunKeyPath}\\{valueName}: {ex.Message}");
                    continue;
                }

                string command = RegistryValueToDisplayString(value);
                string? targetPath = ParseStartupCommandTargetPath(command);
                string[] itemWarnings = BuildStartupTargetWarnings(targetPath);
                string displayName = string.IsNullOrWhiteSpace(valueName) ? "(Default startup entry)" : valueName;
                string id = CreateStableId("startup", "registry", hive, RunKeyPath, valueName);
                var item = new StartupItem(
                    id,
                    displayName,
                    $"{hive} Run",
                    $@"{hive}\{RunKeyPath}",
                    command,
                    targetPath,
                    isEnabled: true,
                    requiresAdministrator,
                    canToggle: true,
                    itemWarnings.Length > 0 ? "Target needs review" : "Enabled",
                    itemWarnings);

                entries.Add(new StartupEntry(item, StartupEntryKind.Registry, hive, RunKeyPath, valueName, valueKind, value, filePath: null));
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{hive}\\{RunKeyPath}: {ex.Message}");
        }

        return entries;
    }

    private static IEnumerable<StartupEntry> EnumerateStartupFolderEntries(
        string folderPath,
        string source,
        bool requiresAdministrator,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsVisibleStartupFolderFile(path, source, warnings))
                .ToArray();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{source}: {ex.Message}");
            yield break;
        }

        foreach (string filePath in files)
        {
            string? targetPath = ResolveStartupFolderTargetPath(filePath);
            string[] itemWarnings = BuildStartupTargetWarnings(targetPath);
            string id = CreateStableId("startup", "folder", source, filePath);
            var item = new StartupItem(
                id,
                Path.GetFileNameWithoutExtension(filePath),
                source,
                filePath,
                filePath,
                targetPath,
                isEnabled: true,
                requiresAdministrator,
                canToggle: true,
                itemWarnings.Length > 0 ? "Target needs review" : "Enabled",
                itemWarnings);

            yield return new StartupEntry(item, StartupEntryKind.File, hive: null, keyPath: null, valueName: null, registryValueKind: null, registryValue: null, filePath);
        }
    }

    internal static bool IsVisibleStartupFolderFile(string filePath, string source, ICollection<string> warnings)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            {
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                warnings.Add($"{source}\\{Path.GetFileName(filePath)}: reparse point skipped.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{source}\\{Path.GetFileName(filePath)}: {ex.Message}");
            return false;
        }
    }

    private static string[] BuildStartupTargetWarnings(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return ["FileLocker could not resolve the startup target path."];
        }

        string normalized = targetPath.Trim().Trim('"');
        if (LooksLikeShellCommand(normalized))
        {
            return [];
        }

        return File.Exists(normalized) || Directory.Exists(normalized)
            ? []
            : ["The startup target path could not be found."];
    }

    private static bool LooksLikeShellCommand(string value)
    {
        string fileName = Path.GetFileName(value);
        return fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("rundll32", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveStartupFolderTargetPath(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        return TryResolveShortcutTarget(filePath) ?? filePath;
    }

    private static string? TryResolveShortcutTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);

            string? targetPath = shortcut?.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null)?.ToString();

            return string.IsNullOrWhiteSpace(targetPath) ? null : Environment.ExpandEnvironmentVariables(targetPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
        }
    }

    private static StartupDisableMetadata CreateFileStartupDisableMetadata(StartupItem item, string disabledPath)
    {
        return new StartupDisableMetadata(
            item.id,
            item.name,
            item.source,
            item.location,
            item.command,
            item.targetPath,
            item.requiresAdministrator,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            backupPath: string.Empty,
            registry: null,
            new StartupFileMetadata(item.location, disabledPath));
    }

    internal static StartupItem ToDisabledStartupItem(StartupDisableMetadata metadata)
    {
        var warnings = new List<string>();
        if (metadata.registry != null && !File.Exists(metadata.backupPath))
        {
            warnings.Add("The .reg backup file is missing, but FileLocker still has restore metadata.");
        }

        bool canToggle = true;
        string status = "Disabled by FileLocker";
        if (metadata.file != null && !File.Exists(metadata.file.disabledPath))
        {
            warnings.Add("The disabled startup shortcut could not be found in FileLocker storage.");
            canToggle = false;
            status = "Restore unavailable";
        }

        if (metadata.file != null && !IsManagedDisabledStartupPath(metadata.file.disabledPath))
        {
            warnings.Add("The disabled startup shortcut is outside FileLocker's managed disabled-startup storage.");
            canToggle = false;
            status = "Restore unavailable";
        }

        if (metadata.file != null && !IsApprovedStartupRestorePath(metadata.file.originalPath))
        {
            warnings.Add("The startup restore path is outside approved Startup folders.");
            canToggle = false;
            status = "Restore unavailable";
        }

        return new StartupItem(
            metadata.id,
            metadata.name,
            metadata.source,
            metadata.file?.originalPath ?? metadata.location,
            metadata.command,
            metadata.targetPath,
            isEnabled: false,
            metadata.requiresAdministrator,
            canToggle,
            status,
            warnings.ToArray());
    }

    private static StartupItem ToRestoredStartupItem(StartupDisableMetadata metadata)
    {
        return new StartupItem(
            metadata.id,
            metadata.name,
            metadata.source,
            metadata.file?.originalPath ?? metadata.location,
            metadata.command,
            metadata.targetPath,
            isEnabled: true,
            metadata.requiresAdministrator,
            canToggle: true,
            "Restored",
            []);
    }

    private static List<StartupDisableMetadata> LoadDisabledStartupMetadata(List<string>? warnings = null)
    {
        string path = GetStartupMetadataPath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StartupDisableMetadata>>(File.ReadAllText(path), MetadataJsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"FileLocker could not read disabled startup metadata: {ex.Message}");
            return [];
        }
    }

    private static void SaveDisabledStartupMetadata(IReadOnlyCollection<StartupDisableMetadata> items)
    {
        string path = GetStartupMetadataPath();
        FileWriteService.WriteAllTextAtomically(path, JsonSerializer.Serialize(items, MetadataJsonOptions), Encoding.UTF8);
    }

    private static string GetStartupMetadataPath()
    {
        return Path.Combine(GetSystemCareDataDirectory(), "startup-disabled.json");
    }

    private static string GetDisabledStartupFilePath(string itemId, string originalPath)
    {
        string extension = Path.GetExtension(originalPath);
        string safeExtension = string.IsNullOrWhiteSpace(extension) ? ".startup" : extension;
        return Path.Combine(GetDisabledStartupItemsDirectory(), $"{itemId}{safeExtension}");
    }

    internal static string GetAvailableDisabledStartupFilePath(string itemId, string originalPath)
    {
        string basePath = GetDisabledStartupFilePath(itemId, originalPath);
        string directory = Path.GetDirectoryName(basePath)!;
        string fileName = Path.GetFileNameWithoutExtension(basePath);
        string extension = Path.GetExtension(basePath);
        string candidate = basePath;
        int counter = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName}-{counter}{extension}");
            counter++;
        }

        return candidate;
    }

    private static void TrySaveDisabledStartupMetadata(IReadOnlyCollection<StartupDisableMetadata> items)
    {
        try
        {
            SaveDisabledStartupMetadata(items);
        }
        catch
        {
            // Best effort rollback; the original disable failure is more useful to surface.
        }
    }

    private static string WriteRegistryStartupBackup(StartupEntry entry)
    {
        string backupDirectory = Path.Combine(GetSystemCareDataDirectory(), "StartupBackups");
        Directory.CreateDirectory(backupDirectory);
        string backupPath = FileWriteService.ResolveAvailablePath(Path.Combine(backupDirectory, $"FileLocker-Startup-{entry.item.id}-{DateTime.Now:yyyyMMdd-HHmmss}.reg"));
        string hiveName = GetRegistryHiveName(entry.hive);

        var builder = new StringBuilder();
        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.AppendLine();
        builder.AppendLine($@"[{hiveName}\{entry.keyPath}]");
        builder.AppendLine(FormatRegistryValue(entry.valueName ?? string.Empty, entry.registryValue, entry.registryValueKind ?? RegistryValueKind.String));
        builder.AppendLine();

        WriteAllTextAtomically(backupPath, builder.ToString(), Encoding.Unicode);
        return backupPath;
    }

    private static void WriteAllTextAtomically(string path, string contents, Encoding encoding)
    {
        FileWriteService.WriteAllTextAtomically(path, contents, encoding);
    }

    private static StartupRegistryMetadata ToRegistryMetadata(
        string hive,
        string keyPath,
        string valueName,
        object? value,
        RegistryValueKind valueKind)
    {
        return valueKind switch
        {
            RegistryValueKind.MultiString => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, value as string[] ?? [], null, null, null),
            RegistryValueKind.DWord => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, value is int intValue ? intValue : 0, null, null),
            RegistryValueKind.QWord => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, null, value is long longValue ? longValue : 0, null),
            RegistryValueKind.Binary => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, null, null, value as byte[] ?? []),
            _ => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), value?.ToString() ?? string.Empty, null, null, null, null)
        };
    }

    internal static void RestoreRegistryStartupItem(StartupRegistryMetadata registry)
    {
        if (!IsApprovedStartupRegistryRestorePath(registry.hive, registry.keyPath))
        {
            throw new InvalidOperationException("Startup restore metadata points outside approved startup registry locations.");
        }

        RegistryKey root = GetRegistryRoot(registry.hive);
        using RegistryKey key = root.CreateSubKey(registry.keyPath, writable: true)
            ?? throw new InvalidOperationException("FileLocker could not open the startup registry key for restore.");

        if (key.GetValue(registry.valueName, defaultValue: null) != null)
        {
            throw new IOException("A startup registry value already exists at the original location.");
        }

        RegistryValueKind kind = Enum.TryParse(registry.valueKind, ignoreCase: true, out RegistryValueKind parsed)
            ? parsed
            : RegistryValueKind.String;
        object value = kind switch
        {
            RegistryValueKind.MultiString => registry.multiStringValue ?? [],
            RegistryValueKind.DWord => registry.dwordValue ?? 0,
            RegistryValueKind.QWord => registry.qwordValue ?? 0L,
            RegistryValueKind.Binary => registry.binaryValue ?? [],
            _ => registry.stringValue ?? string.Empty
        };

        key.SetValue(registry.valueName, value, kind);
    }

    internal static bool IsApprovedStartupRegistryRestorePath(string? hive, string? keyPath)
    {
        return (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(keyPath?.Trim('\\'), RunKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RestoreStartupFolderItem(StartupFileMetadata file)
    {
        if (!IsManagedDisabledStartupPath(file.disabledPath))
        {
            throw new InvalidOperationException("Startup restore metadata points outside FileLocker's managed disabled-startup storage.");
        }

        if (!File.Exists(file.disabledPath))
        {
            throw new FileNotFoundException("The disabled startup shortcut could not be found.", file.disabledPath);
        }

        if (!IsApprovedStartupRestorePath(file.originalPath))
        {
            throw new InvalidOperationException("Startup restore metadata points outside approved Startup folders.");
        }

        if (File.Exists(file.originalPath))
        {
            throw new IOException("A startup item already exists at the original path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file.originalPath)!);
        File.Move(file.disabledPath, file.originalPath);
    }

    internal static bool IsApprovedStartupRestorePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        string[] startupRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        ];

        return startupRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(TrimTrailingSeparator)
            .Any(root => string.Equals(TrimTrailingSeparator(parent), root, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsManagedDisabledStartupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        return IsUnderAnyPath(fullPath, [GetDisabledStartupItemsDirectory()], includeRoot: false);
    }

    private static IEnumerable<InstalledApp> EnumerateInstalledApps(List<string> warnings)
    {
        foreach (InstalledApp app in EnumerateInstalledAppsFromHive(RegistryHive.CurrentUser, RegistryView.Default, "HKCU", "Current user", warnings))
        {
            yield return app;
        }

        RegistryView[] machineViews = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];
        foreach (RegistryView view in machineViews)
        {
            string architecture = view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem ? "x86" : "x64";
            foreach (InstalledApp app in EnumerateInstalledAppsFromHive(RegistryHive.LocalMachine, view, "HKLM", architecture, warnings))
            {
                yield return app;
            }
        }
    }

    private static IReadOnlyList<InstalledApp> EnumerateInstalledAppsFromHive(
        RegistryHive hive,
        RegistryView view,
        string sourceHive,
        string architecture,
        List<string> warnings)
    {
        const string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        var apps = new List<InstalledApp>();
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstallKey = root.OpenSubKey(uninstallKeyPath, writable: false);
            if (uninstallKey == null)
            {
                return apps;
            }

            string[] subKeyNames;
            try
            {
                subKeyNames = uninstallKey.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{sourceHive}\\{uninstallKeyPath}: {ex.Message}");
                return apps;
            }

            foreach (string subKeyName in subKeyNames)
            {
                InstalledApp? app = null;
                try
                {
                    using RegistryKey? appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (appKey == null || RegistryIntValue(appKey, "SystemComponent") == 1)
                    {
                        continue;
                    }

                    string displayName = RegistryStringValue(appKey, "DisplayName");
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    string publisher = RegistryStringValue(appKey, "Publisher");
                    string version = RegistryStringValue(appKey, "DisplayVersion");
                    string installDate = NormalizeInstallDate(RegistryStringValue(appKey, "InstallDate"));
                    long estimatedSizeBytes = RegistryLongValue(appKey, "EstimatedSize") * 1024L;
                    string installLocation = RegistryPathNormalizer.Normalize(RegistryStringValue(appKey, "InstallLocation")) ?? string.Empty;
                    string uninstallCommand = RegistryStringValue(appKey, "UninstallString");
                    string keyPath = architecture == "x86" && sourceHive == "HKLM"
                        ? $@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{subKeyName}"
                        : $@"{uninstallKeyPath}\{subKeyName}";

                    app = new InstalledApp(
                        CreateStableId("app", NormalizeAppIdentity(displayName), NormalizeAppIdentity(publisher), NormalizeAppIdentity(version), RegistryPathNormalizer.Normalize(installLocation) ?? string.Empty),
                        displayName.Trim(),
                        publisher.Trim(),
                        version.Trim(),
                        installDate,
                        Math.Max(0, estimatedSizeBytes),
                        FormatFileSize(Math.Max(0, estimatedSizeBytes)),
                        installLocation,
                        uninstallCommand.Trim(),
                        sourceHive,
                        architecture,
                        requiresAdministrator: sourceHive == "HKLM",
                        canLaunchUninstaller: !string.IsNullOrWhiteSpace(uninstallCommand) && !ContainsSilentUninstallSwitch(uninstallCommand),
                        registryKeyPath: $@"{sourceHive}\{keyPath}");
                }
                catch (Exception ex)
                {
                    AddWarning(warnings, $"{sourceHive}\\{uninstallKeyPath}\\{subKeyName}: {ex.Message}");
                }

                if (app != null)
                {
                    apps.Add(app);
                }
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{sourceHive}\\{uninstallKeyPath}: {ex.Message}");
        }

        return apps;
    }

    private static IEnumerable<AppLeftoverCategory> ScanLeftoversForApp(InstalledApp app, List<string> warnings)
    {
        HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
        string[] rootTokens = BuildAppFolderTokens(app).ToArray();
        if (rootTokens.Length == 0)
        {
            yield break;
        }

        foreach (LeftoverRoot root in GetLeftoverRoots())
        {
            foreach (string candidateRoot in BuildCandidateAppRoots(root.path, rootTokens))
            {
                if (!visitedPaths.Add(candidateRoot) || !Directory.Exists(candidateRoot) || !IsApprovedAppLeftoverPath(candidateRoot))
                {
                    continue;
                }

                string[] safePaths = EnumerateSafeLeftoverDirectories(candidateRoot, warnings).ToArray();
                if (safePaths.Length == 0)
                {
                    AppLeftoverCategory? staleCategory = ScanLeftoverCategory(app, candidateRoot, "Stale app folder", defaultSelected: false, root.requiresAdministrator, warnings);
                    if (staleCategory != null)
                    {
                        yield return staleCategory;
                    }
                }

                foreach (string safePath in safePaths)
                {
                    if (!visitedPaths.Add(safePath))
                    {
                        continue;
                    }

                    string group = GetLeftoverGroupName(safePath);
                    AppLeftoverCategory? category = ScanLeftoverCategory(app, safePath, group, defaultSelected: true, root.requiresAdministrator, warnings);
                    if (category != null)
                    {
                        yield return category;
                    }
                }
            }
        }
    }

    private static AppLeftoverCategory? ScanLeftoverCategory(
        InstalledApp app,
        string path,
        string group,
        bool defaultSelected,
        bool requiresAdministrator,
        List<string> warnings)
    {
        if (!IsApprovedAppLeftoverPath(path))
        {
            AddWarning(warnings, $"{path} is outside approved app cleanup roots.");
            return null;
        }

        bool pathExists = Directory.Exists(path);
        bool isReparsePoint = pathExists && IsReparsePoint(path);
        bool containsReparsePoint = pathExists && !isReparsePoint && ContainsReparsePoint(path);
        bool reparseBlocked = isReparsePoint || containsReparsePoint;
        DirectoryScanSummary summary = pathExists && !reparseBlocked
            ? ScanDirectory(path)
            : new DirectoryScanSummary(
                0,
                0,
                0,
                [reparseBlocked ? "Reparse points are skipped to avoid following links outside approved cleanup roots." : "The leftover path is unavailable."]);
        string id = CreateStableId("leftover", app.id, group, path);

        return new AppLeftoverCategory(
            id,
            app.id,
            app.displayName,
            group,
            $"{app.displayName} {group}",
            group == "Stale app folder"
                ? "App data under approved user or ProgramData roots. Review before selecting."
                : "Cache, log, or temporary files under approved app data roots.",
            path,
            summary.SizeBytes,
            FormatFileSize(summary.SizeBytes),
            summary.FileCount,
            summary.SkippedCount,
            isEnabled: pathExists && !reparseBlocked,
            requiresAdministrator,
            defaultSelected && !reparseBlocked,
            reparseBlocked ? "Skipped" : summary.FileCount > 0 ? "Ready to clean" : "Already clean",
            summary.Warnings);
    }

    private static IEnumerable<string> BuildAppFolderTokens(InstalledApp app)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToken(tokens, app.displayName);
        AddToken(tokens, RemoveParenthetical(app.displayName));
        AddToken(tokens, app.publisher);

        if (!string.IsNullOrWhiteSpace(app.installLocation))
        {
            AddToken(tokens, Path.GetFileName(app.installLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        return tokens.Where(token => token.Length >= 3).Take(8);
    }

    private static IEnumerable<string> BuildCandidateAppRoots(string rootPath, string[] tokens)
    {
        foreach (string token in tokens)
        {
            yield return Path.Combine(rootPath, token);
        }

        foreach (string publisher in tokens.Take(4))
        {
            foreach (string appName in tokens.Take(4))
            {
                if (!string.Equals(publisher, appName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(rootPath, publisher, appName);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSafeLeftoverDirectories(string candidateRoot, List<string> warnings)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((candidateRoot, 0));
        int inspected = 0;

        while (queue.Count > 0 && inspected < 150)
        {
            (string current, int depth) = queue.Dequeue();
            inspected++;

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{current}: {ex.Message}");
                continue;
            }

            foreach (string child in childDirectories)
            {
                if (IsReparsePoint(child))
                {
                    AddWarning(warnings, $"{child}: reparse point skipped.");
                    continue;
                }

                string name = Path.GetFileName(child);
                if (IsSafeLeftoverDirectoryName(name))
                {
                    yield return child;
                    continue;
                }

                if (depth < 3)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    internal static bool IsSafeLeftoverDirectoryName(string name)
    {
        string normalized = NormalizeLeftoverDirectoryName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return SafeLeftoverDirectoryTokens.Any(token =>
            normalized.Equals(token, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(" " + token, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" " + token + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRootLevelGenericLeftoverFolder(string fullPath, IEnumerable<string> approvedRoots)
    {
        string normalizedPath = TrimTrailingSeparator(fullPath);
        string folderName = Path.GetFileName(normalizedPath);
        if (!IsSafeLeftoverDirectoryName(folderName))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        string normalizedParent = TrimTrailingSeparator(parentPath);
        return approvedRoots
            .Select(TrimTrailingSeparator)
            .Any(root => string.Equals(normalizedParent, root, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetLeftoverGroupName(string path)
    {
        string name = NormalizeLeftoverDirectoryName(Path.GetFileName(path));
        if (IsTokenMatch(name, "cache") || IsTokenMatch(name, "caches"))
        {
            return "Cache";
        }

        if (IsTokenMatch(name, "log") || IsTokenMatch(name, "logs") || IsTokenMatch(name, "crash") || IsTokenMatch(name, "crashdumps"))
        {
            return "Logs";
        }

        if (IsTokenMatch(name, "temp") || IsTokenMatch(name, "tmp"))
        {
            return "Temp";
        }

        return "Cache";
    }

    private static bool IsTokenMatch(string normalizedName, string token)
    {
        return normalizedName.Equals(token, StringComparison.OrdinalIgnoreCase) ||
            normalizedName.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.EndsWith(" " + token, StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Contains(" " + token + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLeftoverDirectoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static DirectoryScanSummary ScanDirectory(string rootPath)
    {
        var warnings = new List<string>();
        long sizeBytes = 0;
        int fileCount = 0;
        int skippedCount = 0;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                if (fileCount >= MaxLeftoverScanFiles)
                {
                    warnings.Add($"Scan stopped after {MaxLeftoverScanFiles.ToString(CultureInfo.InvariantCulture)} files.");
                    break;
                }

                try
                {
                    var info = new FileInfo(file);
                    sizeBytes += info.Exists ? info.Length : 0;
                    fileCount++;
                }
                catch
                {
                    skippedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, ex.Message);
            skippedCount++;
        }

        return new DirectoryScanSummary(sizeBytes, fileCount, skippedCount, warnings.ToArray());
    }

    internal static void DeleteLeftoverPath(string path)
    {
        if (!IsApprovedAppLeftoverPath(path))
        {
            throw new InvalidOperationException("The selected path is outside approved app cleanup roots.");
        }

        if (IsReparsePoint(path))
        {
            throw new InvalidOperationException("Reparse points are not cleaned by app leftover cleanup.");
        }

        if (File.Exists(path))
        {
            DeleteLeftoverFile(path);
            return;
        }

        if (Directory.Exists(path))
        {
            DeleteLeftoverDirectory(path);
            return;
        }

        throw new DirectoryNotFoundException("The selected cleanup path no longer exists.");
    }

    private static void DeleteLeftoverDirectory(string directoryPath)
    {
        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (IsReparsePoint(file))
            {
                throw new InvalidOperationException("The selected cleanup path contains a reparse point.");
            }

            DeleteLeftoverFile(file);
        }

        foreach (string childDirectory in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (IsReparsePoint(childDirectory))
            {
                throw new InvalidOperationException("The selected cleanup path contains a reparse point.");
            }

            DeleteLeftoverDirectory(childDirectory);
        }

        FileCleanupService.ClearReadOnlyAttribute(directoryPath);
        Directory.Delete(directoryPath, recursive: false);
    }

    private static void DeleteLeftoverFile(string path)
    {
        FileAttributes? originalAttributes = null;
        try
        {
            originalAttributes = File.GetAttributes(path);
            FileCleanupService.ClearReadOnlyAttribute(path);
            File.Delete(path);
        }
        catch
        {
            RestoreFileAttributesBestEffort(path, originalAttributes);
            throw;
        }
    }

    private static void RestoreFileAttributesBestEffort(string path, FileAttributes? attributes)
    {
        if (attributes is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetAttributes(path, attributes.Value);
        }
        catch
        {
            // Preserve the original cleanup failure for the caller.
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string rootPath)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootPath);
        int inspected = 0;

        while (queue.Count > 0 && inspected < 1000)
        {
            string current = queue.Dequeue();
            inspected++;

            IEnumerable<string> childFiles;
            try
            {
                childFiles = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                childFiles = [];
            }

            foreach (string child in childFiles)
            {
                if (IsReparsePoint(child))
                {
                    return true;
                }
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string child in childDirectories)
            {
                if (IsReparsePoint(child))
                {
                    return true;
                }

                queue.Enqueue(child);
            }
        }

        return false;
    }

    private static ProcessCommand ParseProcessCommand(string command)
    {
        string normalized = command.Trim();
        if (normalized.StartsWith('"'))
        {
            int closingQuote = normalized.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return new ProcessCommand(normalized[1..closingQuote], normalized[(closingQuote + 1)..].Trim());
            }
        }

        string? targetPath = ParseStartupCommandTargetPath(normalized);
        if (!string.IsNullOrWhiteSpace(targetPath) && normalized.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessCommand(targetPath, normalized[targetPath.Length..].Trim());
        }

        int firstSpace = normalized.IndexOf(' ');
        return firstSpace > 0
            ? new ProcessCommand(normalized[..firstSpace], normalized[(firstSpace + 1)..].Trim())
            : new ProcessCommand(normalized, string.Empty);
    }

    private static IEnumerable<string> SplitCommandLineArguments(string arguments)
    {
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char character in arguments)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static HashSet<string> NormalizeIds(IReadOnlyCollection<string>? ids)
    {
        return ids is { Count: > 0 }
            ? ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static int GetAppQualityScore(InstalledApp app)
    {
        int score = 0;
        if (app.architecture == "x64")
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(app.uninstallCommand))
        {
            score += 5;
        }

        if (!string.IsNullOrWhiteSpace(app.installLocation))
        {
            score += 3;
        }

        if (app.sourceHive == "HKLM")
        {
            score += 1;
        }

        return score;
    }

    private static string CreateInstalledAppDedupKey(InstalledApp app)
    {
        return string.Join(
            "|",
            NormalizeAppIdentity(app.displayName),
            NormalizeAppIdentity(app.publisher),
            NormalizeAppIdentity(app.version));
    }

    private static string NormalizeAppIdentity(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : RemoveParenthetical(value).Trim().ToLowerInvariant();
    }

    private static string RemoveParenthetical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int index = value.IndexOf(" (", StringComparison.Ordinal);
        return index > 0 ? value[..index].Trim() : value.Trim();
    }

    private static void AddToken(HashSet<string> tokens, string? value)
    {
        string token = SanitizeFolderToken(value);
        if (!string.IsNullOrWhiteSpace(token))
        {
            tokens.Add(token);
        }
    }

    private static string SanitizeFolderToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string token = value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            token = token.Replace(invalid, ' ');
        }

        return string.Join(" ", token.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
    }

    private static string NormalizeInstallDate(string value)
    {
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value.Trim();
    }

    private static string RegistryStringValue(RegistryKey key, string name)
    {
        return key.GetValue(name)?.ToString() ?? string.Empty;
    }

    private static int RegistryIntValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0
        };
    }

    private static long RegistryLongValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            long longValue => longValue,
            string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => 0L
        };
    }

    private static string RegistryValueToDisplayString(object? value)
    {
        return value switch
        {
            string[] values => string.Join(";", values),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => value?.ToString() ?? string.Empty
        };
    }

    private static string FormatRegistryValue(string valueName, object? value, RegistryValueKind kind)
    {
        string name = string.IsNullOrEmpty(valueName) ? "@" : $"\"{EscapeRegistryString(valueName)}\"";
        return kind switch
        {
            RegistryValueKind.DWord when value is int intValue => $"{name}=dword:{intValue:x8}",
            RegistryValueKind.QWord when value is long longValue => $"{name}=hex(b):{FormatRegHex(BitConverter.GetBytes(longValue))}",
            RegistryValueKind.Binary when value is byte[] bytes => $"{name}=hex:{FormatRegHex(bytes)}",
            RegistryValueKind.MultiString when value is string[] strings => $"{name}=hex(7):{FormatRegHex(Encoding.Unicode.GetBytes(string.Join('\0', strings) + "\0\0"))}",
            RegistryValueKind.ExpandString => $"{name}=hex(2):{FormatRegHex(Encoding.Unicode.GetBytes((value?.ToString() ?? string.Empty) + "\0"))}",
            _ => $"{name}=\"{EscapeRegistryString(value?.ToString() ?? string.Empty)}\""
        };
    }

    private static string FormatRegHex(byte[] bytes)
    {
        return string.Join(",", bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string EscapeRegistryString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static RegistryKey GetRegistryRoot(string? hive)
    {
        return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? Registry.LocalMachine
            : Registry.CurrentUser;
    }

    private static string GetRegistryHiveName(string? hive)
    {
        return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? "HKEY_LOCAL_MACHINE"
            : "HKEY_CURRENT_USER";
    }

    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RequireAdministrator(string operationName)
    {
        if (!IsRunningAsAdministrator())
        {
            throw new InvalidOperationException($"{operationName} requires administrator mode. Use Restart as Administrator and try again.");
        }
    }

    private static IEnumerable<LeftoverRoot> GetLeftoverRoots()
    {
        yield return new LeftoverRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), requiresAdministrator: false);
        yield return new LeftoverRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), requiresAdministrator: false);
        yield return new LeftoverRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), requiresAdministrator: true);
    }

    private static string[] GetApprovedLeftoverRoots()
    {
        return GetLeftoverRoots()
            .Select(root => root.path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
    }

    private static string[] GetBlockedCleanupRoots()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
        ];

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUnderAnyPath(string fullPath, IEnumerable<string> roots, bool includeRoot)
    {
        string normalizedPath = TrimTrailingSeparator(fullPath);
        foreach (string root in roots)
        {
            string normalizedRoot = TrimTrailingSeparator(root);
            if (includeRoot && string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimTrailingSeparator(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CreateStableId(params string[] parts)
    {
        string raw = string.Join("|", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(bytes, 0);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    private static string GetSystemCareDataDirectory()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker",
            "SystemCare");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetDisabledStartupItemsDirectory()
    {
        return Path.Combine(GetSystemCareDataDirectory(), "DisabledStartupItems");
    }

    private sealed record StartupEntry(
        StartupItem item,
        StartupEntryKind kind,
        string? hive,
        string? keyPath,
        string? valueName,
        RegistryValueKind? registryValueKind,
        object? registryValue,
        string? filePath);

    private enum StartupEntryKind
    {
        Registry,
        File
    }

    private sealed record ProcessCommand(string fileName, string arguments);
    private sealed record LeftoverRoot(string path, bool requiresAdministrator);
    private sealed record DirectoryScanSummary(long SizeBytes, int FileCount, int SkippedCount, string[] Warnings);
}

internal sealed record StartupItem(
    string id,
    string name,
    string source,
    string location,
    string command,
    string? targetPath,
    bool isEnabled,
    bool requiresAdministrator,
    bool canToggle,
    string status,
    string[] warnings);

internal sealed record StartupScanResult(
    StartupItem[] items,
    int enabledCount,
    int disabledCount,
    string[] warnings);

internal sealed record StartupToggleResult(
    StartupItem item,
    bool isEnabled,
    string backupPath,
    string message);

internal sealed record StartupDisableMetadata(
    string id,
    string name,
    string source,
    string location,
    string command,
    string? targetPath,
    bool requiresAdministrator,
    string disabledAtUtc,
    string backupPath,
    StartupRegistryMetadata? registry,
    StartupFileMetadata? file);

internal sealed record StartupRegistryMetadata(
    string hive,
    string keyPath,
    string valueName,
    string valueKind,
    string? stringValue,
    string[]? multiStringValue,
    int? dwordValue,
    long? qwordValue,
    byte[]? binaryValue);

internal sealed record StartupFileMetadata(
    string originalPath,
    string disabledPath);

internal sealed record InstalledApp(
    string id,
    string displayName,
    string publisher,
    string version,
    string installDate,
    long estimatedSizeBytes,
    string estimatedSizeDisplay,
    string installLocation,
    string uninstallCommand,
    string sourceHive,
    string architecture,
    bool requiresAdministrator,
    bool canLaunchUninstaller,
    string registryKeyPath);

internal sealed record InstalledAppsScanResult(
    InstalledApp[] apps,
    int appCount,
    string[] warnings);

internal sealed record UninstallerLaunchResult(
    bool started,
    string appId,
    string displayName,
    string message);

internal sealed record AppLeftoverCategory(
    string id,
    string appId,
    string appDisplayName,
    string group,
    string label,
    string description,
    string path,
    long sizeBytes,
    string sizeDisplay,
    int fileCount,
    int skippedCount,
    bool isEnabled,
    bool requiresAdministrator,
    bool defaultSelected,
    string status,
    string[] warnings);

internal sealed record AppLeftoverScanResult(
    AppLeftoverCategory[] categories,
    long totalBytes,
    string totalDisplay,
    int totalFiles,
    int skippedItems,
    string[] warnings);

internal sealed record AppLeftoverCleanFailure(
    string categoryId,
    string appDisplayName,
    string path,
    string message);

internal sealed record AppLeftoverCleanResult(
    int cleanedCount,
    int failedCount,
    long freedBytes,
    string freedDisplay,
    AppLeftoverCategory[] cleanedCategories,
    AppLeftoverCleanFailure[] failures,
    AppLeftoverScanResult scan);

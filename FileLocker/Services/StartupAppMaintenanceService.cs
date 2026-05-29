using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileLocker;

internal static class StartupAppMaintenanceService
{
    private const int MaxLeftoverScanFiles = 75_000;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string StartupCategoryApps = "Startup Apps";
    private const string StartupCategoryBroken = "Broken Startup Items";
    private const string StartupCategoryAdvanced = "Advanced Startup Hooks";
    private const long MaxStartupMetadataJsonBytes = 1 * 1024 * 1024;
    private const int MaxWarningMessageChars = 2048;
    private const int MaxWarningCount = 100;
    private const int MaxRequestIdCount = 500;
    private const int MaxRequestIdChars = 256;
    private const int MaxHiddenProcessOutputChars = 1 * 1024 * 1024;
    private const int MaxStartupFolderFiles = 5_000;
    private const int MaxPathSearchDirectories = 2_048;
    private const int MaxPathSearchExtensions = 64;
    private const int InvalidExecutablePathEnd = -2;
    private const int MaxLeftoverDirectoryChildren = 10_000;
    internal const int MaxComTextChars = 4_096;
    private const int MaxLeftoverReparseInspectionDirectories = 1_000;
    private const int MaxInstalledAppTextChars = 512;
    private const int MaxInstalledAppCommandChars = MaxComTextChars;
    private const int MaxScheduledTaskComItems = 256;
    private const int MaxWmiEventConsumers = 256;
    private const int ServiceNoChange = -1;
    private const int ServiceDisabled = 4;
    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryConfig = 0x0001;
    private const int ServiceChangeConfig = 0x0002;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExecutableExtensions = [".exe", ".bat", ".cmd", ".com", ".msi", ".dll", ".sys", ".ocx"];
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
        string warning = NormalizeWarningMessage(message);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            warnings?.Add(warning);
        }
    }

    internal static string NormalizeWarningMessage(string? message)
    {
        string redacted = SensitiveDataRedactor.RedactMessage(message);
        if (redacted.Length <= MaxWarningMessageChars)
        {
            return redacted;
        }

        const string truncationMessage = "Warning truncated.";
        string suffix = $"{Environment.NewLine}{truncationMessage}";
        int bodyLength = Math.Max(0, MaxWarningMessageChars - suffix.Length);
        return redacted[..bodyLength].TrimEnd() + suffix;
    }

    private static string[] NormalizeWarnings(IEnumerable<string> warnings)
    {
        return warnings
            .Select(NormalizeWarningMessage)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWarningCount)
            .ToArray();
    }

    internal static StartupScanResult ScanStartup()
    {
        var warnings = new List<string>();
        StartupItem[] enabledItems = EnumerateEnabledStartupEntries(warnings)
            .Select(entry => entry.item)
            .ToArray();

        List<StartupDisableMetadata> disabledMetadata = LoadDisabledStartupMetadata(warnings);
        StartupItem[] disabledItems = disabledMetadata
            .Select(ToDisabledStartupItem)
            .ToArray();
        HashSet<string> ignoredIds = LoadIgnoredStartupItemIds(warnings);

        StartupItem[] items = enabledItems
            .Concat(disabledItems)
            .Select(item => item with { isIgnored = ignoredIds.Contains(item.id) })
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.isEnabled)
                .ThenByDescending(item => item.canToggle)
                .First())
            .OrderBy(item => item.source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StartupScanResult(
            items,
            items.Count(item => item.isEnabled),
            items.Count(item => !item.isEnabled),
            items.Count(item => string.Equals(item.category, StartupCategoryBroken, StringComparison.OrdinalIgnoreCase)),
            items.Count(item => string.Equals(item.category, StartupCategoryAdvanced, StringComparison.OrdinalIgnoreCase)),
            disabledItems.Length,
            items.Count(item => item.isIgnored),
            disabledMetadata.Select(ToStartupRestoreRecord).ToArray(),
            NormalizeWarnings(warnings));
    }

    internal static StartupToggleResult SetStartupEnabled(string? itemId, bool enabled)
    {
        string normalizedId = NormalizeRequiredStartupItemId(itemId);
        if (enabled)
        {
            return EnableStartupItem(normalizedId);
        }

        return DisableStartupItem(normalizedId);
    }

    internal static StartupIgnoreResult SetStartupIgnored(string? itemId, bool ignored)
    {
        string normalizedId = NormalizeRequiredStartupItemId(itemId);
        HashSet<string> ignoredIds = LoadIgnoredStartupItemIds();
        if (ignored)
        {
            ignoredIds.Add(normalizedId);
        }
        else
        {
            ignoredIds.Remove(normalizedId);
        }

        SaveIgnoredStartupItemIds(ignoredIds);
        return new StartupIgnoreResult(normalizedId, ignored, ignored ? "Startup item ignored." : "Startup item returned to the active review list.");
    }

    internal static StartupExportResult ExportStartupItemDetails(string? itemId, bool includeFullPaths = true)
    {
        string normalizedId = NormalizeRequiredStartupItemId(itemId);
        StartupScanResult scan = ScanStartup();
        StartupItem item = scan.items.FirstOrDefault(candidate => string.Equals(candidate.id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected startup item could not be found.");
        string exportDirectory = Path.Combine(GetSystemCareDataDirectory(), "Exports");
        Directory.CreateDirectory(exportDirectory);
        string safeName = SanitizeFolderToken(item.name);
        string fileName = $"FileLocker-StartupItem-{(string.IsNullOrWhiteSpace(safeName) ? item.id : safeName)}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        string exportPath = FileWriteService.ResolveAvailablePath(Path.Combine(exportDirectory, fileName));
        StartupItem exportItem = includeFullPaths ? item : RedactStartupItemForExport(item);
        FileWriteService.WriteAllTextAtomically(exportPath, JsonSerializer.Serialize(exportItem, MetadataJsonOptions), Encoding.UTF8);
        return new StartupExportResult(exportPath, Path.GetFileName(exportPath), item.id, includeFullPaths);
    }

    private static StartupItem RedactStartupItemForExport(StartupItem item)
    {
        return item with
        {
            location = RedactStartupExportText(item.location),
            command = RedactStartupExportText(item.command),
            targetPath = RedactOptionalStartupExportText(item.targetPath),
            warnings = item.warnings.Select(RedactStartupExportText).ToArray(),
            commandRaw = RedactStartupExportText(item.commandRaw),
            executableResolved = RedactStartupExportText(item.executableResolved),
            arguments = RedactStartupExportText(item.arguments),
            workingDirectory = RedactStartupExportText(item.workingDirectory),
            sourceLocation = RedactStartupExportText(item.sourceLocation),
            backupPayload = RedactStartupExportText(item.backupPayload),
            notes = RedactStartupExportText(item.notes)
        };
    }

    private static string? RedactOptionalStartupExportText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : RedactStartupExportText(value);

    private static string RedactStartupExportText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : SensitiveDataRedactor.RedactMessage(value);

    internal static StartupToggleResult RemoveBrokenStartupItem(string? itemId, string? confirmation)
    {
        if (!string.Equals(confirmation, "REMOVE BROKEN STARTUP", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm broken startup removal before FileLocker changes the entry.");
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("Select a startup item first.");
        }

        string normalizedId = NormalizeRequiredStartupItemId(itemId);
        StartupItem? item = ScanStartup().items.FirstOrDefault(candidate => string.Equals(candidate.id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (item == null || !string.Equals(item.category, StartupCategoryBroken, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FileLocker only removes startup entries that are classified as broken.");
        }

        if (!item.canToggle)
        {
            throw new InvalidOperationException("This broken startup item is read-only. Export details instead of removing it.");
        }

        StartupToggleResult result = DisableStartupItem(item.id, userAction: "RemoveBroken");
        return result with { message = $"{item.name} was removed from active startup and can be restored from FileLocker metadata." };
    }

    internal static InstalledAppsScanResult ScanInstalledApps()
    {
        var warnings = new List<string>();
        InstalledApp[] apps = DeduplicateInstalledApps(EnumerateInstalledApps(warnings))
            .OrderBy(app => app.displayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.publisher, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new InstalledAppsScanResult(apps, apps.Length, NormalizeWarnings(warnings));
    }

    internal static UninstallerLaunchResult LaunchUninstaller(string? appId, string? confirmation)
    {
        if (!string.Equals(confirmation, "UNINSTALL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm the uninstall launch before opening the vendor uninstaller.");
        }

        string normalizedAppId = NormalizeRequiredInstalledAppId(appId);
        InstalledApp app = ScanInstalledApps().apps
            .FirstOrDefault(item => string.Equals(item.id, normalizedAppId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected app could not be found.");

        string uninstallCommand = NormalizeInstalledAppCommand(app.uninstallCommand);
        if (string.IsNullOrWhiteSpace(uninstallCommand))
        {
            throw new InvalidOperationException("This app does not publish an uninstall command.");
        }

        if (ContainsSilentUninstallSwitch(uninstallCommand))
        {
            throw new InvalidOperationException("FileLocker does not launch uninstall commands that include silent or quiet switches.");
        }

        ProcessCommand command = ParseProcessCommand(uninstallCommand);
        if (string.IsNullOrWhiteSpace(command.fileName) ||
            command.fileName.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            throw new InvalidOperationException("This app's uninstall command is not valid.");
        }

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
            NormalizeWarnings(warnings));
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
                    NormalizeWarningMessage(ex.Message)));
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
        if (string.IsNullOrWhiteSpace(normalized) || ContainsPathControlCharacter(normalized))
        {
            return null;
        }

        string? candidate = ParseStartupCommandTargetCandidate(normalized);
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    internal static StartupCommandResolution ResolveStartupCommand(string? command)
    {
        string raw = command?.Trim() ?? string.Empty;
        string? normalized = RegistryPathNormalizer.Normalize(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new StartupCommandResolution(raw, string.Empty, string.Empty, string.Empty, string.Empty, "UnresolvedCommand", "Low", "Medium", "Command is empty.");
        }

        ProcessCommand processCommand = ParseProcessCommand(normalized);
        string executable = processCommand.fileName.Trim().Trim('"');
        string arguments = processCommand.arguments;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new StartupCommandResolution(raw, string.Empty, arguments, string.Empty, string.Empty, "UnresolvedCommand", "Low", "Medium", "FileLocker could not identify an executable.");
        }

        string resolvedExecutable = ResolveExecutablePath(executable);
        string workingDirectory = GetSafeDirectoryName(resolvedExecutable);
        string launcherName = GetSafeFileName(resolvedExecutable);
        bool knownLauncher = IsKnownStartupLauncher(launcherName) || IsKnownStartupLauncher(executable);
        string status = DetermineCommandStatus(resolvedExecutable, knownLauncher);
        string confidence = status is "MissingTarget" or "Valid" ? "High" : status is "SuspiciousLauncher" or "SystemProtected" ? "Medium" : "Low";
        string risk = status switch
        {
            "MissingTarget" => "High",
            "SuspiciousLauncher" => "Medium",
            "UnresolvedCommand" => "Medium",
            "TargetUnavailable" => "Medium",
            "SystemProtected" => "Low",
            _ => knownLauncher && IsPotentiallyScriptableLauncher(launcherName) ? "Medium" : "Low"
        };
        string notes = status switch
        {
            "SuspiciousLauncher" => "Startup uses a launcher that can run scripts or indirect targets.",
            "TargetUnavailable" => "Target is on a removable, network, or currently inaccessible location.",
            "SystemProtected" => "Target is under a protected Windows or Program Files location and is not treated as a broken startup item.",
            "MissingTarget" => "Resolved startup target could not be found.",
            "UnresolvedCommand" => "Command could not be resolved to a concrete executable.",
            _ => knownLauncher ? "Command uses a recognized Windows launcher." : string.Empty
        };

        return new StartupCommandResolution(raw, resolvedExecutable, arguments, workingDirectory, launcherName, status, confidence, risk, notes);
    }

    private static string ResolveExecutablePath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        string executableText = executable.Trim().Trim('"');
        if (ContainsPathControlCharacter(executableText))
        {
            return string.Empty;
        }

        if (!RegistryPathNormalizer.TryExpandEnvironmentVariables(executableText, out string expanded))
        {
            return string.Empty;
        }

        try
        {
            if (Path.IsPathFullyQualified(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (Path.IsPathRooted(expanded))
            {
                return string.Empty;
            }

            string? pathResolved = ResolveCommandFromPath(expanded);
            return pathResolved ?? expanded;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return expanded;
        }
    }

    private static string? ResolveCommandFromPath(string executable)
    {
        string fileName = GetSafeFileName(executable);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string[] extensions = Path.HasExtension(fileName)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxPathSearchExtensions)
                .ToArray();
        int searchedDirectories = 0;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (searchedDirectories >= MaxPathSearchDirectories)
            {
                break;
            }

            searchedDirectories++;
            foreach (string extension in extensions)
            {
                try
                {
                    string candidate = Path.Combine(directory, fileName + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
            }
        }

        return null;
    }

    private static string DetermineCommandStatus(string executable, bool knownLauncher)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "UnresolvedCommand";
        }

        string fileName = GetSafeFileName(executable);
        if (knownLauncher && IsPotentiallyScriptableLauncher(fileName))
        {
            return "SuspiciousLauncher";
        }

        if (!Path.IsPathFullyQualified(executable))
        {
            return knownLauncher ? "Valid" : "UnresolvedCommand";
        }

        try
        {
            bool protectedPath = IsProtectedStartupPath(executable);
            string root = Path.GetPathRoot(executable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType is DriveType.Network or DriveType.Removable || !drive.IsReady)
                {
                    return "TargetUnavailable";
                }
            }

            if (File.Exists(executable) || Directory.Exists(executable))
            {
                return "Valid";
            }

            return protectedPath ? "SystemProtected" : "MissingTarget";
        }
        catch (UnauthorizedAccessException)
        {
            return IsProtectedStartupPath(executable) ? "SystemProtected" : "TargetUnavailable";
        }
        catch (IOException)
        {
            return IsProtectedStartupPath(executable) ? "SystemProtected" : "TargetUnavailable";
        }
        catch
        {
            return "UnresolvedCommand";
        }
    }

    internal static bool IsProtectedStartupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string trimmedPath = path.Trim();
            if (ContainsPathControlCharacter(trimmedPath) ||
                !Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(trimmedPath);
            return IsUnderAnyPath(fullPath, GetProtectedStartupRoots(), includeRoot: false);
        }
        catch
        {
            return false;
        }
    }

    private static string GetSafeDirectoryName(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(path) ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetSafeFileName(string value)
    {
        try
        {
            return Path.GetFileName(value.Trim()) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static bool ContainsPathControlCharacter(string value)
    {
        return value.Any(character =>
            char.IsControl(character) ||
            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
    }

    private static bool IsKnownStartupLauncher(string value)
    {
        string fileName = GetSafeFileName(value);
        return fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("rundll32", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("regsvr32", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("wscript", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cscript", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("java", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("python.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("python", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPotentiallyScriptableLauncher(string value)
    {
        string fileName = GetSafeFileName(value);
        return fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("wscript", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cscript", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("regsvr32", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("rundll32", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParseStartupCommandTargetCandidate(string normalized)
    {
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

        if (executableEndIndex == InvalidExecutablePathEnd)
        {
            return null;
        }

        int firstSpace = normalized.IndexOf(' ');
        return firstSpace > 0 ? normalized[..firstSpace] : normalized;
    }

    private static int FindExecutablePathEnd(string value)
    {
        int bestEndIndex = -1;
        int streamSuffixEndIndex = -1;
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
                if (endIndex < value.Length && value[endIndex] == ':')
                {
                    if (streamSuffixEndIndex < 0 || endIndex < streamSuffixEndIndex)
                    {
                        streamSuffixEndIndex = endIndex;
                    }

                    searchIndex = index + 1;
                    continue;
                }

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

        if (streamSuffixEndIndex >= 0 && (bestEndIndex < 0 || streamSuffixEndIndex < bestEndIndex))
        {
            return InvalidExecutablePathEnd;
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
        string backupPath,
        RegistryView registryView = RegistryView.Default,
        string userAction = "Disable")
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
            ToRegistryMetadata(hive, keyPath, valueName, value, valueKind, registryView),
            file: null,
            item.sourceType,
            item.category,
            item.scope,
            item.status,
            GetCurrentFileLockerVersion(),
            restoreStatus: "Available",
            failureDetails: string.Empty,
            userAction: userAction,
            resolvedExecutable: GetStartupResolvedTargetSnapshot(item),
            commandStatus: item.status,
            confidence: item.confidence,
            riskLevel: item.riskLevel);
    }

    private static string GetStartupResolvedTargetSnapshot(StartupItem item)
    {
        return FirstNonEmpty(item.executableResolved, item.targetPath ?? string.Empty);
    }

    internal static bool IsApprovedAppLeftoverPath(string? path)
    {
        if (!TryGetNormalFullyQualifiedPath(path, out string fullPath))
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

    private static StartupToggleResult DisableStartupItem(string itemId, string userAction = "Disable")
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
                backupPath,
                entry.registryView,
                userAction);

            disableAction = () =>
            {
                using RegistryKey root = GetRegistryRoot(entry.hive, entry.registryView);
                using RegistryKey? key = root.OpenSubKey(entry.keyPath ?? string.Empty, writable: true);
                key?.DeleteValue(entry.valueName ?? string.Empty, throwOnMissingValue: false);
            };
        }
        else if (entry.kind == StartupEntryKind.File)
        {
            string sourcePath = entry.filePath ?? throw new InvalidOperationException("The selected startup shortcut could not be found.");
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The selected startup shortcut could not be found.", sourcePath);
            }

            string disabledPath = GetAvailableDisabledStartupFilePath(entry.item.id, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
            metadata = CreateFileStartupDisableMetadata(entry.item, disabledPath, userAction);
            disableAction = () => File.Move(sourcePath, disabledPath);
        }
        else if (entry.kind == StartupEntryKind.ScheduledTask)
        {
            string taskPath = entry.keyPath ?? throw new InvalidOperationException("The selected scheduled task could not be found.");
            metadata = CreateScheduledTaskDisableMetadata(entry.item, taskPath, userAction);
            disableAction = () => SetScheduledTaskEnabled(taskPath, enabled: false);
        }
        else if (entry.kind == StartupEntryKind.Service)
        {
            string serviceName = entry.valueName ?? throw new InvalidOperationException("The selected service could not be found.");
            int originalStartType = entry.registryValue is int startType ? startType : QueryServiceStartType(serviceName);
            metadata = CreateServiceDisableMetadata(entry.item, serviceName, originalStartType, userAction);
            disableAction = () => ChangeServiceStartType(serviceName, ServiceDisabled);
        }
        else
        {
            throw new InvalidOperationException("This startup item is read-only and cannot be disabled by FileLocker.");
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

        try
        {
            if (metadata.registry != null)
            {
                RestoreRegistryStartupItem(metadata.registry);
            }
            else if (metadata.file != null)
            {
                RestoreStartupFolderItem(metadata.file);
            }
            else if (metadata.task != null)
            {
                RestoreScheduledTaskItem(metadata.task);
            }
            else if (metadata.service != null)
            {
                RestoreServiceStartupItem(metadata.service);
            }
            else
            {
                throw new InvalidOperationException("The startup restore metadata is incomplete.");
            }
        }
        catch (Exception ex)
        {
            string failureDetails = NormalizeWarningMessage(ex.Message);
            SaveDisabledStartupMetadata(metadataItems
                .Select(item => string.Equals(item.id, metadata.id, StringComparison.OrdinalIgnoreCase)
                    ? item with { restoreStatus = "Failed", failureDetails = failureDetails }
                    : item)
                .ToList());
            throw;
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
        foreach (IStartupEntryProvider provider in CreateStartupEntryProviders())
        {
            foreach (StartupEntry entry in provider.Enumerate(warnings))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<IStartupEntryProvider> CreateStartupEntryProviders()
    {
        yield return new DelegateStartupEntryProvider("HKCU Run", warnings => EnumerateRegistryStartupEntriesFromHive(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            "HKCU",
            "Current user",
            RunKeyPath,
            "HKCU Run",
            canDisable: true,
            requiresAdministrator: false,
            StartupCategoryApps,
            warnings));
        yield return new DelegateStartupEntryProvider("HKCU RunOnce", warnings => EnumerateRegistryStartupEntriesFromHive(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            "HKCU",
            "Current user",
            RunOnceKeyPath,
            "HKCU RunOnce",
            canDisable: true,
            requiresAdministrator: false,
            StartupCategoryApps,
            warnings));

        RegistryView[] machineViews = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];
        foreach (RegistryView view in machineViews)
        {
            string viewLabel = view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem ? "32-bit" : "64-bit";
            yield return new DelegateStartupEntryProvider($"HKLM Run {viewLabel}", warnings => EnumerateRegistryStartupEntriesFromHive(
                RegistryHive.LocalMachine,
                view,
                "HKLM",
                $"All users ({viewLabel})",
                RunKeyPath,
                $"HKLM Run ({viewLabel})",
                canDisable: true,
                requiresAdministrator: true,
                StartupCategoryApps,
                warnings));
            yield return new DelegateStartupEntryProvider($"HKLM RunOnce {viewLabel}", warnings => EnumerateRegistryStartupEntriesFromHive(
                RegistryHive.LocalMachine,
                view,
                "HKLM",
                $"All users ({viewLabel})",
                RunOnceKeyPath,
                $"HKLM RunOnce ({viewLabel})",
                canDisable: true,
                requiresAdministrator: true,
                StartupCategoryApps,
                warnings));
        }

        yield return new DelegateStartupEntryProvider("Startup folder", warnings => EnumerateStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Startup folder",
            requiresAdministrator: false,
            warnings));
        yield return new DelegateStartupEntryProvider("Common Startup folder", warnings => EnumerateStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            "Common Startup folder",
            requiresAdministrator: true,
            warnings));
        yield return new DelegateStartupEntryProvider("Policy startup", EnumeratePolicyStartupEntries);
        yield return new DelegateStartupEntryProvider("Group Policy scripts", EnumerateGroupPolicyScriptEntries);
        yield return new DelegateStartupEntryProvider("Advanced registry hooks", EnumerateAdvancedRegistryStartupEntries);
        yield return new DelegateStartupEntryProvider("Services and drivers", EnumerateServiceStartupEntries);
        yield return new DelegateStartupEntryProvider("Scheduled tasks", EnumerateScheduledTaskStartupEntries);
        yield return new DelegateStartupEntryProvider("WMI event consumers", EnumerateWmiPermanentEventConsumers);
        yield return new DelegateStartupEntryProvider("Packaged startup tasks", EnumeratePackagedStartupTasks);
    }

    private static IReadOnlyList<StartupEntry> EnumerateRegistryStartupEntriesFromHive(
        RegistryHive registryHive,
        RegistryView view,
        string hive,
        string scope,
        string keyPath,
        string source,
        bool canDisable,
        bool requiresAdministrator,
        string defaultCategory,
        List<string> warnings)
    {
        var entries = new List<StartupEntry>();
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(registryHive, view);
            using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
            if (key == null)
            {
                return entries;
            }

            foreach (string valueName in GetRegistryValueNames(key, $@"{hive}\{keyPath}", warnings))
            {
                StartupEntry? entry = TryCreateRegistryStartupEntry(
                    key,
                    hive,
                    scope,
                    keyPath,
                    source,
                    valueName,
                    canDisable,
                    requiresAdministrator,
                    defaultCategory,
                    readOnlyManaged: !canDisable,
                    warnings,
                    view);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{hive}\\{keyPath}: {ex.Message}");
        }

        return entries;
    }

    private static string[] GetRegistryValueNames(RegistryKey key, string location, List<string> warnings)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{location}: {ex.Message}");
            return [];
        }
    }

    private static StartupEntry? TryCreateRegistryStartupEntry(
        RegistryKey key,
        string hive,
        string scope,
        string keyPath,
        string source,
        string valueName,
        bool canDisable,
        bool requiresAdministrator,
        string defaultCategory,
        bool readOnlyManaged,
        List<string> warnings,
        RegistryView registryView = RegistryView.Default)
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
            AddWarning(warnings, $"{hive}\\{keyPath}\\{valueName}: {ex.Message}");
            return null;
        }

        string command = RegistryValueToDisplayString(value);
        string displayName = string.IsNullOrWhiteSpace(valueName) ? "(Default startup entry)" : valueName;
        StartupCommandResolution resolution = ResolveStartupCommand(command);
        string[] itemWarnings = BuildStartupTargetWarnings(resolution);
        string category = GetStartupCategory(defaultCategory, resolution);
        string registryViewLabel = GetRegistryViewLabel(registryView);
        string location = FormatRegistryLocation(hive, keyPath, registryView);
        string sourceLocation = FormatRegistryLocation(hive, $@"{keyPath}\{valueName}", registryView);
        string id = CreateStableId("startup", "registry", hive, registryViewLabel, keyPath, source, valueName);
        StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
        string status = readOnlyManaged
            ? GetReadOnlyStartupStatus(source, keyPath)
            : resolution.status == "Valid" ? "Enabled" : resolution.status;

        var item = new StartupItem(
            id,
            displayName,
            source,
            location,
            command,
            string.IsNullOrWhiteSpace(resolution.executableResolved) ? null : resolution.executableResolved,
            isEnabled: true,
            requiresAdministrator,
            canDisable && !readOnlyManaged,
            status,
            itemWarnings,
            sourceType: "Registry",
            category,
            scope,
            publisher.publisher,
            publisher.signatureStatus,
            publisher.isMicrosoftSigned,
            commandRaw: command,
            executableResolved: resolution.executableResolved,
            arguments: resolution.arguments,
            workingDirectory: resolution.workingDirectory,
            sourceLocation,
            lastModified: string.Empty,
            startupImpact: category == StartupCategoryAdvanced ? "High" : "Medium",
            confidence: resolution.confidence,
            riskLevel: AdjustStartupRisk(resolution.riskLevel, category, publisher),
            disableMethod: canDisable && !readOnlyManaged ? "RegistryValue" : "ReadOnly",
            isReadOnlyManaged: readOnlyManaged,
            backupPayload: valueKind.ToString(),
            notes: resolution.notes);

        return new StartupEntry(item, canDisable && !readOnlyManaged ? StartupEntryKind.Registry : StartupEntryKind.ReadOnlyRegistry, hive, keyPath, valueName, valueKind, value, filePath: null, registryView);
    }

    private static string GetReadOnlyStartupStatus(string source, string keyPath)
    {
        return source.Contains("Policy", StringComparison.OrdinalIgnoreCase) ||
            keyPath.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase) ||
            keyPath.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase)
            ? "PolicyManaged"
            : "Managed / read-only";
    }

    private static string FormatRegistryLocation(string hive, string keyPath, RegistryView registryView)
    {
        string viewLabel = GetRegistryViewLabel(registryView);
        string location = $@"{hive}\{keyPath}";
        return string.IsNullOrWhiteSpace(viewLabel) ? location : $"{location} ({viewLabel} view)";
    }

    private static string GetRegistryViewLabel(RegistryView registryView)
    {
        return registryView switch
        {
            RegistryView.Registry32 => "32-bit",
            RegistryView.Registry64 => "64-bit",
            _ => string.Empty
        };
    }

    private static string[] BuildStartupTargetWarnings(StartupCommandResolution resolution)
    {
        var warnings = new List<string>();
        if (resolution.status == "MissingTarget")
        {
            warnings.Add("The startup target path could not be found.");
        }
        else if (resolution.status == "UnresolvedCommand")
        {
            warnings.Add("FileLocker could not resolve the startup command.");
        }
        else if (resolution.status == "TargetUnavailable")
        {
            warnings.Add("The startup target is unavailable or inaccessible right now.");
        }
        else if (resolution.status == "SystemProtected")
        {
            warnings.Add("The startup target is under a protected system location and is not treated as broken.");
        }
        else if (resolution.status == "SuspiciousLauncher")
        {
            warnings.Add("Startup uses a script-capable or indirect launcher.");
        }

        return warnings.ToArray();
    }

    private static string GetStartupCategory(string defaultCategory, StartupCommandResolution resolution)
    {
        if (resolution.status is "MissingTarget" or "UnresolvedCommand")
        {
            return StartupCategoryBroken;
        }

        return defaultCategory;
    }

    private static string AdjustStartupRisk(string riskLevel, string category, StartupPublisherInfo publisher)
    {
        if (category == StartupCategoryBroken)
        {
            return "High";
        }

        if (publisher.isMicrosoftSigned && string.Equals(riskLevel, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            return "Low";
        }

        return riskLevel;
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
            var visibleFiles = new List<string>();
            foreach (string path in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsVisibleStartupFolderFile(path, source, warnings))
                {
                    continue;
                }

                if (visibleFiles.Count >= MaxStartupFolderFiles)
                {
                    AddWarning(warnings, $"{source}: startup folder scan stopped after {MaxStartupFolderFiles.ToString(CultureInfo.InvariantCulture)} files.");
                    break;
                }

                visibleFiles.Add(path);
            }

            files = visibleFiles.ToArray();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{source}: {ex.Message}");
            yield break;
        }

        foreach (string filePath in files)
        {
            string? targetPath = ResolveStartupFolderTargetPath(filePath);
            StartupCommandResolution resolution = ResolveStartupCommand(targetPath ?? filePath);
            string[] itemWarnings = BuildStartupTargetWarnings(resolution);
            string id = CreateStableId("startup", "folder", source, filePath);
            string category = GetStartupCategory(StartupCategoryApps, resolution);
            StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
            string lastModified = GetFileLastModifiedDisplay(filePath);
            var item = new StartupItem(
                id,
                Path.GetFileNameWithoutExtension(filePath),
                source,
                filePath,
                filePath,
                string.IsNullOrWhiteSpace(resolution.executableResolved) ? targetPath : resolution.executableResolved,
                isEnabled: true,
                requiresAdministrator,
                canToggle: true,
                resolution.status == "Valid" ? "Enabled" : resolution.status,
                itemWarnings,
                sourceType: "Startup folder",
                category,
                scope: requiresAdministrator ? "All users" : "Current user",
                publisher.publisher,
                publisher.signatureStatus,
                publisher.isMicrosoftSigned,
                commandRaw: filePath,
                executableResolved: resolution.executableResolved,
                arguments: resolution.arguments,
                workingDirectory: resolution.workingDirectory,
                sourceLocation: filePath,
                lastModified,
                startupImpact: "Medium",
                confidence: resolution.confidence,
                riskLevel: AdjustStartupRisk(resolution.riskLevel, category, publisher),
                disableMethod: "MoveToDisabledStorage",
                isReadOnlyManaged: false,
                backupPayload: filePath,
                notes: resolution.notes);

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

    private static IEnumerable<StartupEntry> EnumeratePolicyStartupEntries(List<string> warnings)
    {
        const string policyRunPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
        foreach (StartupEntry entry in EnumerateRegistryStartupEntriesFromHive(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            "HKCU",
            "Current user policy",
            policyRunPath,
            "HKCU Policy Run",
            canDisable: false,
            requiresAdministrator: false,
            StartupCategoryApps,
            warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateRegistryStartupEntriesFromHive(
            RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32,
            "HKLM",
            "All users policy",
            policyRunPath,
            "HKLM Policy Run",
            canDisable: false,
            requiresAdministrator: true,
            StartupCategoryApps,
            warnings))
        {
            yield return entry;
        }
    }

    private static IEnumerable<StartupEntry> EnumerateGroupPolicyScriptEntries(List<string> warnings)
    {
        GroupPolicyScriptSource[] sources =
        [
            new(RegistryHive.CurrentUser, "HKCU", "Current user", @"Software\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon", "Group Policy logon script", false),
            new(RegistryHive.CurrentUser, "HKCU", "Current user policy", @"Software\Policies\Microsoft\Windows\System\Scripts\Logon", "Group Policy logon script", false),
            new(RegistryHive.LocalMachine, "HKLM", "All users", @"Software\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Startup", "Group Policy startup script", true),
            new(RegistryHive.LocalMachine, "HKLM", "All users", @"Software\Policies\Microsoft\Windows\System\Scripts\Startup", "Group Policy startup script", true),
            new(RegistryHive.LocalMachine, "HKLM", "All users", @"Software\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon", "Group Policy logon script", true),
            new(RegistryHive.LocalMachine, "HKLM", "All users", @"Software\Policies\Microsoft\Windows\System\Scripts\Logon", "Group Policy logon script", true)
        ];

        foreach (GroupPolicyScriptSource source in sources)
        {
            RegistryView[] views = source.Hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
                ? [RegistryView.Registry64, RegistryView.Registry32]
                : [RegistryView.Default];
            foreach (RegistryView view in views)
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(source.Hive, view);
                using RegistryKey? key = root.OpenSubKey(source.KeyPath, writable: false);
                if (key == null)
                {
                    continue;
                }

                foreach (StartupEntry entry in EnumerateGroupPolicyScriptEntriesFromKey(key, source, source.KeyPath, warnings))
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<StartupEntry> EnumerateGroupPolicyScriptEntriesFromKey(
        RegistryKey key,
        GroupPolicyScriptSource source,
        string keyPath,
        List<string> warnings)
    {
        StartupEntry? entry = TryCreateGroupPolicyScriptEntry(key, source, keyPath, warnings);
        if (entry != null)
        {
            yield return entry;
        }

        string[] subkeyNames;
        try
        {
            subkeyNames = key.GetSubKeyNames();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $@"{source.HiveName}\{keyPath}: {ex.Message}");
            yield break;
        }

        foreach (string subkeyName in subkeyNames.Take(500))
        {
            using RegistryKey? subkey = key.OpenSubKey(subkeyName, writable: false);
            if (subkey == null)
            {
                continue;
            }

            string childPath = $@"{keyPath}\{subkeyName}";
            foreach (StartupEntry childEntry in EnumerateGroupPolicyScriptEntriesFromKey(subkey, source, childPath, warnings))
            {
                yield return childEntry;
            }
        }
    }

    private static StartupEntry? TryCreateGroupPolicyScriptEntry(
        RegistryKey key,
        GroupPolicyScriptSource source,
        string keyPath,
        List<string> warnings)
    {
        string script;
        string parameters;
        try
        {
            script = key.GetValue("Script", defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString()?.Trim() ?? string.Empty;
            parameters = key.GetValue("Parameters", defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $@"{source.HiveName}\{keyPath}: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        string command = CombineCommandAndArguments(script, parameters);
        StartupCommandResolution resolution = ResolveStartupCommand(command);
        string category = GetStartupCategory(StartupCategoryAdvanced, resolution);
        StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
        string id = CreateStableId("startup", "gpo-script", source.HiveName, keyPath, source.Label, command);
        var item = new StartupItem(
            id,
            Path.GetFileNameWithoutExtension(script),
            source.Label,
            $@"{source.HiveName}\{keyPath}",
            command,
            string.IsNullOrWhiteSpace(resolution.executableResolved) ? null : resolution.executableResolved,
            isEnabled: true,
            source.RequiresAdministrator,
            canToggle: false,
            "PolicyManaged",
            BuildStartupTargetWarnings(resolution),
            sourceType: "Group Policy script",
            category,
            scope: source.Scope,
            publisher.publisher,
            publisher.signatureStatus,
            publisher.isMicrosoftSigned,
            commandRaw: command,
            executableResolved: resolution.executableResolved,
            arguments: resolution.arguments,
            workingDirectory: resolution.workingDirectory,
            sourceLocation: $@"{source.HiveName}\{keyPath}",
            lastModified: string.Empty,
            startupImpact: "High",
            confidence: resolution.confidence,
            riskLevel: AdjustStartupRisk(resolution.riskLevel, category, publisher),
            disableMethod: "ReadOnlyPolicy",
            isReadOnlyManaged: true,
            backupPayload: "Script+Parameters",
            notes: "Group Policy startup/logon scripts are managed by Windows policy and are shown read-only.");
        return new StartupEntry(item, StartupEntryKind.ReadOnlyRegistry, source.HiveName, keyPath, "Script", RegistryValueKind.String, script, filePath: null);
    }

    private static IEnumerable<StartupEntry> EnumerateAdvancedRegistryStartupEntries(List<string> warnings)
    {
        var valueSources = new[]
        {
            new RegistryValueSource(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Winlogon", ["Shell", "Userinit", "Notify"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute", ["BootExecute"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit DLLs", ["AppInit_DLLs"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls", "AppCert DLLs", null),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LSA packages", ["Authentication Packages", "Security Packages", "Notification Packages"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs", "KnownDLLs", null),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order", "Network providers", ["ProviderOrder"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\NetworkProvider\HwOrder", "Network providers", ["ProviderOrder"]),
            new RegistryValueSource(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32", "Media codecs", null)
        };

        foreach (RegistryValueSource source in valueSources)
        {
            foreach (StartupEntry entry in EnumerateReadOnlyRegistryValues(source, warnings))
            {
                yield return entry;
            }
        }

        foreach (StartupEntry entry in EnumerateSubkeyRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Active Setup\Installed Components", "Active Setup", "StubPath", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateSubkeyRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", "IFEO Debugger", "Debugger", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateSubkeyRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit", "SilentProcessExit", "MonitorProcess", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateSubkeyRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Print\Monitors", "Print monitors", "Driver", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateRegistrySubkeysAsReadOnlySource(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", "Browser helper object", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateReadOnlyRegistryValues(new RegistryValueSource(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Internet Explorer\Toolbar", "Legacy browser toolbar", null), warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateReadOnlyRegistryValues(new RegistryValueSource(RegistryHive.CurrentUser, @"Software\Microsoft\Internet Explorer\Toolbar\WebBrowser", "Legacy browser toolbar", null), warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateRegistrySubkeysAsReadOnlySource(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries", "Winsock provider", warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateOfficeAddInEntries(warnings))
        {
            yield return entry;
        }

        foreach (StartupEntry entry in EnumerateShellExtensionKeys(warnings))
        {
            yield return entry;
        }
    }

    private static IEnumerable<StartupEntry> EnumerateReadOnlyRegistryValues(RegistryValueSource source, List<string> warnings)
    {
        RegistryView[] views = source.Hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Default];
        foreach (RegistryView view in views)
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(source.Hive, view);
            using RegistryKey? key = root.OpenSubKey(source.KeyPath, writable: false);
            if (key == null)
            {
                continue;
            }

            IEnumerable<string> valueNames = source.ValueNames ?? GetRegistryValueNames(key, $@"HKLM\{source.KeyPath}", warnings);
            foreach (string valueName in valueNames)
            {
                StartupEntry? entry = TryCreateRegistryStartupEntry(
                    key,
                    source.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
                    source.Hive == RegistryHive.LocalMachine ? "All users" : "Current user",
                    source.KeyPath,
                    source.Label,
                    valueName,
                    canDisable: false,
                    requiresAdministrator: source.Hive == RegistryHive.LocalMachine,
                    StartupCategoryAdvanced,
                    readOnlyManaged: true,
                    warnings,
                    view);
                if (entry != null)
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<StartupEntry> EnumerateSubkeyRegistryValue(RegistryHive hive, string parentPath, string label, string valueName, List<string> warnings)
    {
        RegistryView[] views = hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Default];
        foreach (RegistryView view in views)
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? parent = root.OpenSubKey(parentPath, writable: false);
            if (parent == null)
            {
                continue;
            }

            string[] subKeyNames;
            try
            {
                subKeyNames = parent.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{label}: {ex.Message}");
                continue;
            }

            foreach (string subKeyName in subKeyNames.Take(500))
            {
                using RegistryKey? key = parent.OpenSubKey(subKeyName, writable: false);
                if (key?.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames) == null)
                {
                    continue;
                }

                StartupEntry? entry = TryCreateRegistryStartupEntry(
                    key,
                    hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
                    hive == RegistryHive.LocalMachine ? "All users" : "Current user",
                    $@"{parentPath}\{subKeyName}",
                    label,
                    valueName,
                    canDisable: false,
                    requiresAdministrator: hive == RegistryHive.LocalMachine,
                    StartupCategoryAdvanced,
                    readOnlyManaged: true,
                    warnings,
                    view);
                if (entry != null)
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<StartupEntry> EnumerateRegistrySubkeysAsReadOnlySource(RegistryHive hive, string parentPath, string label, List<string> warnings)
    {
        RegistryView[] views = hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Default];
        foreach (RegistryView view in views)
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? parent = root.OpenSubKey(parentPath, writable: false);
            if (parent == null)
            {
                continue;
            }

            string[] subKeyNames;
            try
            {
                subKeyNames = parent.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{label}: {ex.Message}");
                continue;
            }

            foreach (string subKeyName in subKeyNames.Take(500))
            {
                string keyPath = $@"{parentPath}\{subKeyName}";
                string registryViewLabel = GetRegistryViewLabel(view);
                string id = CreateStableId("startup", "registry-subkey", hive.ToString(), registryViewLabel, keyPath, label);
                string location = FormatRegistryLocation(GetHiveShortName(hive), keyPath, view);
                var item = new StartupItem(
                    id,
                    subKeyName,
                    label,
                    location,
                    string.Empty,
                    targetPath: null,
                    isEnabled: true,
                    requiresAdministrator: hive == RegistryHive.LocalMachine,
                    canToggle: false,
                    "Read-only",
                    [],
                    sourceType: "Registry",
                    category: StartupCategoryAdvanced,
                    scope: hive == RegistryHive.LocalMachine ? "All users" : "Current user",
                    publisher: string.Empty,
                    signatureStatus: "Unknown",
                    isMicrosoftSigned: false,
                    commandRaw: string.Empty,
                    executableResolved: string.Empty,
                    arguments: string.Empty,
                    workingDirectory: string.Empty,
                    sourceLocation: location,
                    lastModified: string.Empty,
                    startupImpact: "Medium",
                    confidence: "Medium",
                    riskLevel: "Medium",
                    disableMethod: "ReadOnlyRegistry",
                    isReadOnlyManaged: true,
                    backupPayload: keyPath,
                    notes: $"{label} registration is shown scan-only because deleting the subkey can destabilize host applications.");
                yield return new StartupEntry(item, StartupEntryKind.ReadOnlyRegistry, GetHiveShortName(hive), keyPath, valueName: null, registryValueKind: null, registryValue: null, filePath: null, view);
            }
        }
    }

    private static IEnumerable<StartupEntry> EnumerateOfficeAddInEntries(List<string> warnings)
    {
        string[] apps = ["Access", "Excel", "Outlook", "PowerPoint", "Word"];
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (string app in apps)
            {
                foreach (StartupEntry entry in EnumerateRegistrySubkeysAsReadOnlySource(hive, $@"Software\Microsoft\Office\{app}\Addins", $"Office {app} add-in", warnings))
                {
                    yield return entry;
                }
            }
        }
    }

    private static string GetHiveShortName(RegistryHive hive)
    {
        return hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
    }

    private static IEnumerable<StartupEntry> EnumerateShellExtensionKeys(List<string> warnings)
    {
        RegistryValueSource[] valueSources =
        [
            new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", "Explorer approved shell extension", null),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", "Explorer approved shell extension", null),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", "Explorer preview handler", null),
            new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers", "Explorer preview handler", null),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Classes\*\shellex\{e357fccd-a995-4576-b01f-234630154e96}", "Explorer thumbnail handler", null),
            new(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\{e357fccd-a995-4576-b01f-234630154e96}", "Explorer thumbnail handler", null),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Classes\SystemFileAssociations\image\shellex\{e357fccd-a995-4576-b01f-234630154e96}", "Explorer thumbnail handler", null),
            new(RegistryHive.CurrentUser, @"Software\Classes\SystemFileAssociations\image\shellex\{e357fccd-a995-4576-b01f-234630154e96}", "Explorer thumbnail handler", null)
        ];

        foreach (RegistryValueSource source in valueSources)
        {
            foreach (StartupEntry entry in EnumerateReadOnlyRegistryValues(source, warnings))
            {
                yield return entry;
            }
        }

        (RegistryHive Hive, string Path, string Label)[] subkeySources =
        [
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", "Explorer icon overlay handler"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", "Explorer icon overlay handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\Directory\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers", "Explorer context menu handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\*\shellex\PropertySheetHandlers", "Explorer property sheet handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\*\shellex\PropertySheetHandlers", "Explorer property sheet handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Directory\shellex\CopyHookHandlers", "Explorer copy hook handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\Directory\shellex\CopyHookHandlers", "Explorer copy hook handler"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Directory\shellex\DragDropHandlers", "Explorer drag-drop handler"),
            (RegistryHive.CurrentUser, @"Software\Classes\Directory\shellex\DragDropHandlers", "Explorer drag-drop handler")
        ];

        foreach ((RegistryHive hive, string path, string label) in subkeySources)
        {
            foreach (StartupEntry entry in EnumerateRegistrySubkeysAsReadOnlySource(hive, path, label, warnings))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<StartupEntry> EnumerateServiceStartupEntries(List<string> warnings)
    {
        const string servicesPath = @"SYSTEM\CurrentControlSet\Services";
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using RegistryKey? servicesKey = root.OpenSubKey(servicesPath, writable: false);
        if (servicesKey == null)
        {
            yield break;
        }

        string[] serviceNames;
        try
        {
            serviceNames = servicesKey.GetSubKeyNames();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"Services: {ex.Message}");
            yield break;
        }

        foreach (string serviceName in serviceNames)
        {
            using RegistryKey? serviceKey = servicesKey.OpenSubKey(serviceName, writable: false);
            if (serviceKey == null)
            {
                continue;
            }

            int start = RegistryIntValue(serviceKey, "Start");
            bool delayed = RegistryIntValue(serviceKey, "DelayedAutoStart") == 1;
            bool triggerStart = serviceKey.OpenSubKey("TriggerInfo") != null;
            if (start is not (0 or 1 or 2) && !triggerStart)
            {
                continue;
            }
            bool canToggle = start == 2 || triggerStart;

            string imagePath = GetServiceStartupCommand(serviceKey);
            string hostImagePath = RegistryStringValue(serviceKey, "ImagePath", doNotExpandEnvironmentNames: true);
            string serviceDll = GetServiceDllPath(serviceKey);

            string displayName = RegistryStringValue(serviceKey, "DisplayName");
            StartupCommandResolution resolution = ResolveStartupCommand(imagePath);
            StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
            string startLabel = start switch
            {
                0 => "Boot driver",
                1 => "System driver",
                2 when delayed => "Delayed auto-start service",
                2 => "Automatic service",
                _ when triggerStart => "Trigger-start service",
                _ => "Service"
            };
            string id = CreateStableId("startup", "service", serviceName);
            var item = new StartupItem(
                id,
                string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName,
                startLabel,
                $@"HKLM\{servicesPath}\{serviceName}",
                imagePath,
                string.IsNullOrWhiteSpace(resolution.executableResolved) ? null : resolution.executableResolved,
                isEnabled: true,
                requiresAdministrator: true,
                canToggle,
                canToggle ? "Enabled" : "Read-only",
                BuildStartupTargetWarnings(resolution),
                sourceType: "Service",
                category: StartupCategoryAdvanced,
                scope: "System",
                publisher.publisher,
                publisher.signatureStatus,
                publisher.isMicrosoftSigned,
                commandRaw: imagePath,
                executableResolved: resolution.executableResolved,
                arguments: resolution.arguments,
                workingDirectory: resolution.workingDirectory,
                sourceLocation: $@"HKLM\{servicesPath}\{serviceName}",
                lastModified: string.Empty,
                startupImpact: start is 0 or 1 ? "High" : "Medium",
                confidence: resolution.confidence,
                riskLevel: AdjustStartupRisk(resolution.riskLevel, StartupCategoryAdvanced, publisher),
                disableMethod: canToggle ? "ServiceControlManager" : "ReadOnlyService",
                isReadOnlyManaged: !canToggle,
                backupPayload: BuildServiceStartupBackupPayload(startLabel, hostImagePath, serviceDll),
                notes: BuildServiceStartupNotes(canToggle, hostImagePath, serviceDll));
            yield return new StartupEntry(item, canToggle ? StartupEntryKind.Service : StartupEntryKind.ReadOnlyRegistry, hive: "HKLM", keyPath: $@"{servicesPath}\{serviceName}", valueName: serviceName, registryValueKind: RegistryValueKind.DWord, registryValue: start, filePath: null);
        }
    }

    internal static string GetServiceStartupCommand(RegistryKey serviceKey)
    {
        string imagePath = RegistryStringValue(serviceKey, "ImagePath", doNotExpandEnvironmentNames: true);
        string serviceDll = GetServiceDllPath(serviceKey);
        if (!string.IsNullOrWhiteSpace(serviceDll) &&
            (string.IsNullOrWhiteSpace(imagePath) || IsSvchostLauncher(imagePath)))
        {
            return serviceDll;
        }

        return imagePath;
    }

    private static string GetServiceDllPath(RegistryKey serviceKey)
    {
        string serviceDll = RegistryStringValue(serviceKey, "ServiceDll", doNotExpandEnvironmentNames: true);
        if (!string.IsNullOrWhiteSpace(serviceDll))
        {
            return serviceDll;
        }

        using RegistryKey? parameters = serviceKey.OpenSubKey("Parameters", writable: false);
        return parameters == null ? string.Empty : RegistryStringValue(parameters, "ServiceDll", doNotExpandEnvironmentNames: true);
    }

    private static bool IsSvchostLauncher(string command)
    {
        string? target = ParseStartupCommandTargetPath(command);
        string fileName = Path.GetFileName(string.IsNullOrWhiteSpace(target) ? command : target);
        return fileName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("svchost", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildServiceStartupBackupPayload(string startLabel, string hostImagePath, string serviceDll)
    {
        if (string.IsNullOrWhiteSpace(serviceDll))
        {
            return startLabel;
        }

        return string.IsNullOrWhiteSpace(hostImagePath)
            ? $"{startLabel}; ServiceDll={serviceDll}"
            : $"{startLabel}; ImagePath={hostImagePath}; ServiceDll={serviceDll}";
    }

    private static string BuildServiceStartupNotes(bool canToggle, string hostImagePath, string serviceDll)
    {
        string actionNote = canToggle
            ? "Service start type can be changed through the Service Control Manager with restore metadata."
            : "Boot and system drivers are scan-only because changing them can prevent Windows from starting.";
        if (!string.IsNullOrWhiteSpace(serviceDll) && IsSvchostLauncher(hostImagePath))
        {
            return $"{actionNote} FileLocker resolves the service DLL because the service is hosted by svchost.exe.";
        }

        return actionNote;
    }

    private static IEnumerable<StartupEntry> EnumerateScheduledTaskStartupEntries(List<string> warnings)
    {
        var entries = new List<StartupEntry>();
        object? service = null;
        object? rootFolder = null;
        try
        {
            service = CreateScheduleService();
            InvokeComMethod(service, "Connect");
            rootFolder = InvokeComMethod(service, "GetFolder", "\\")
                ?? throw new InvalidOperationException("Task Scheduler root folder could not be opened.");

            int remaining = 1500;
            CollectScheduledTaskEntries(rootFolder, warnings, entries, ref remaining);
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }

        return entries;
    }

    private static void CollectScheduledTaskEntries(object folder, List<string> warnings, List<StartupEntry> entries, ref int remaining)
    {
        if (remaining <= 0)
        {
            return;
        }

        object? tasks = null;
        try
        {
            tasks = InvokeComMethod(folder, "GetTasks", 1);
            int taskCount = GetComIntProperty(tasks, "Count");
            for (int index = 1; index <= taskCount && remaining > 0; index++)
            {
                object? task = null;
                try
                {
                    task = GetComProperty(tasks!, "Item", index);
                    StartupEntry? entry = TryCreateScheduledTaskStartupEntry(task!, warnings);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
                }
                finally
                {
                    ReleaseComObject(task);
                    remaining--;
                }
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(tasks);
        }

        object? folders = null;
        try
        {
            folders = InvokeComMethod(folder, "GetFolders", 0);
            int folderCount = GetComIntProperty(folders, "Count");
            for (int index = 1; index <= folderCount && remaining > 0; index++)
            {
                object? childFolder = null;
                try
                {
                    childFolder = GetComProperty(folders!, "Item", index);
                    CollectScheduledTaskEntries(childFolder!, warnings, entries, ref remaining);
                }
                catch (Exception ex)
                {
                    AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
                }
                finally
                {
                    ReleaseComObject(childFolder);
                }
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(folders);
        }
    }

    private static StartupEntry? TryCreateScheduledTaskStartupEntry(object task, List<string> warnings)
    {
        object? definition = null;
        object? triggers = null;
        object? actions = null;
        object? registrationInfo = null;
        try
        {
            string taskPath = GetComStringProperty(task, "Path");
            if (string.IsNullOrWhiteSpace(taskPath))
            {
                taskPath = GetComStringProperty(task, "Name");
            }

            definition = GetComProperty(task, "Definition");
            if (definition == null)
            {
                return null;
            }

            triggers = GetComProperty(definition, "Triggers");
            int[] triggerTypes = GetScheduledTaskTriggerTypes(triggers);
            if (triggerTypes.Length == 0 || !triggerTypes.Any(IsScheduledTaskTriggerInScope))
            {
                return null;
            }

            string triggerSummary = string.Join(", ", triggerTypes.Select(GetScheduledTaskTriggerLabel).Distinct(StringComparer.OrdinalIgnoreCase));
            actions = GetComProperty(definition, "Actions");
            string command = BuildScheduledTaskCommand(actions);
            StartupCommandResolution resolution = ResolveStartupCommand(command);
            StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
            string id = CreateStableId("startup", "task", taskPath);
            bool taskEnabled = GetComBoolProperty(task, "Enabled", defaultValue: false);
            int state = GetComIntProperty(task, "State");
            string status = taskEnabled ? GetScheduledTaskStateLabel(state) : "Disabled";
            registrationInfo = GetComProperty(definition, "RegistrationInfo");
            string lastModified = GetComStringProperty(registrationInfo, "Date");
            string category = string.IsNullOrWhiteSpace(command)
                ? StartupCategoryAdvanced
                : GetStartupCategory(StartupCategoryApps, resolution);
            bool canToggle = taskEnabled && !string.IsNullOrWhiteSpace(taskPath);
            string notes = taskEnabled
                ? "Scheduled task can be disabled through the Task Scheduler API with restore metadata."
                : "Task is already disabled outside FileLocker.";
            if (string.IsNullOrWhiteSpace(command))
            {
                notes += " FileLocker could not extract an executable task action, so modification is limited.";
                canToggle = false;
            }

            var item = new StartupItem(
                id,
                taskPath.TrimStart('\\'),
                "Scheduled Task",
                taskPath,
                command,
                string.IsNullOrWhiteSpace(resolution.executableResolved) ? null : resolution.executableResolved,
                isEnabled: taskEnabled,
                requiresAdministrator: true,
                canToggle,
                status,
                BuildStartupTargetWarnings(resolution),
                sourceType: "Scheduled Task",
                category,
                scope: "Task Scheduler",
                publisher.publisher,
                publisher.signatureStatus,
                publisher.isMicrosoftSigned,
                commandRaw: command,
                executableResolved: resolution.executableResolved,
                arguments: resolution.arguments,
                workingDirectory: resolution.workingDirectory,
                sourceLocation: taskPath,
                lastModified,
                startupImpact: GetScheduledTaskStartupImpact(triggerTypes),
                confidence: string.IsNullOrWhiteSpace(command) ? "Low" : resolution.confidence,
                riskLevel: AdjustStartupRisk(string.IsNullOrWhiteSpace(command) ? "Medium" : resolution.riskLevel, category, publisher),
                disableMethod: canToggle ? "TaskScheduler" : "ReadOnlyTaskScheduler",
                isReadOnlyManaged: !canToggle,
                backupPayload: triggerSummary,
                notes);
            return new StartupEntry(item, canToggle ? StartupEntryKind.ScheduledTask : StartupEntryKind.ReadOnlyRegistry, hive: null, keyPath: taskPath, valueName: null, registryValueKind: null, registryValue: null, filePath: null);
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"Scheduled Tasks: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseComObject(registrationInfo);
            ReleaseComObject(actions);
            ReleaseComObject(triggers);
            ReleaseComObject(definition);
        }
    }

    private static int[] GetScheduledTaskTriggerTypes(object? triggers)
    {
        if (triggers == null)
        {
            return [];
        }

        int count = Math.Clamp(GetComIntProperty(triggers, "Count"), 0, MaxScheduledTaskComItems);
        var types = new List<int>(count);
        for (int index = 1; index <= count; index++)
        {
            object? trigger = null;
            try
            {
                trigger = GetComProperty(triggers, "Item", index);
                types.Add(GetComIntProperty(trigger, "Type"));
            }
            finally
            {
                ReleaseComObject(trigger);
            }
        }

        return types.ToArray();
    }

    internal static bool IsScheduledTaskTriggerInScope(int triggerType)
    {
        return triggerType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 11;
    }

    internal static string GetScheduledTaskTriggerLabel(int triggerType)
    {
        return triggerType switch
        {
            0 => "Event",
            1 => "Time",
            2 => "Daily",
            3 => "Weekly",
            4 => "Monthly",
            5 => "Monthly day-of-week",
            6 => "Idle",
            7 => "Registration",
            8 => "Boot",
            9 => "Logon",
            11 => "Session change",
            _ => $"Trigger {triggerType.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    private static string GetScheduledTaskStartupImpact(int[] triggerTypes)
    {
        if (triggerTypes.Any(trigger => trigger is 8 or 9 or 11))
        {
            return "High";
        }

        return triggerTypes.Any(trigger => trigger is 0 or 6 or 7) ? "Medium" : "Low";
    }

    private static string GetScheduledTaskStateLabel(int state)
    {
        return state switch
        {
            1 => "Disabled",
            2 => "Queued",
            3 => "Ready",
            4 => "Running",
            _ => "Enabled"
        };
    }

    private static string BuildScheduledTaskCommand(object? actions)
    {
        if (actions == null)
        {
            return string.Empty;
        }

        int count = Math.Clamp(GetComIntProperty(actions, "Count"), 0, MaxScheduledTaskComItems);
        var commands = new List<string>(count);
        for (int index = 1; index <= count; index++)
        {
            object? action = null;
            try
            {
                action = GetComProperty(actions, "Item", index);
                int actionType = GetComIntProperty(action, "Type");
                if (actionType != 0)
                {
                    continue;
                }

                string path = GetComStringProperty(action, "Path");
                string arguments = GetComStringProperty(action, "Arguments");
                commands.Add(CombineCommandAndArguments(path, arguments));
            }
            finally
            {
                ReleaseComObject(action);
            }
        }

        return NormalizeComText(string.Join(" && ", commands.Where(command => !string.IsNullOrWhiteSpace(command))));
    }

    private static string CombineCommandAndArguments(string path, string arguments)
    {
        string normalizedPath = path.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return arguments.Trim();
        }

        string command = normalizedPath.Contains(' ') && !normalizedPath.StartsWith("\"", StringComparison.Ordinal)
            ? $"\"{normalizedPath}\""
            : normalizedPath;
        return string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";
    }

    private static IEnumerable<StartupEntry> EnumerateWmiPermanentEventConsumers(List<string> warnings)
    {
        string powerShell = ResolveCommandFromPath("powershell.exe") ?? ResolveCommandFromPath("pwsh.exe") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(powerShell))
        {
            yield break;
        }

        const string script = "Get-CimInstance -Namespace root/subscription -ClassName __EventConsumer | Select-Object Name,__CLASS,CommandLineTemplate,ExecutablePath,ScriptText | ConvertTo-Json -Compress";
        string output;
        try
        {
            output = RunHiddenProcess(powerShell, $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", timeoutMilliseconds: 5000, warnings, "WMI event consumers");
        }
        catch
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            yield break;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(output);
        }
        catch (JsonException ex)
        {
            AddWarning(warnings, $"WMI event consumers: {ex.Message}");
            yield break;
        }

        IEnumerable<JsonElement> consumers = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.ValueKind == JsonValueKind.Object ? [root] : [];
        int inspectedConsumers = 0;
        foreach (JsonElement consumer in consumers)
        {
            if (inspectedConsumers >= MaxWmiEventConsumers)
            {
                AddWarning(warnings, $"WMI event consumers: scan stopped after {MaxWmiEventConsumers.ToString(CultureInfo.InvariantCulture)} entries.");
                yield break;
            }

            inspectedConsumers++;
            string name = GetJsonString(consumer, "Name");
            string consumerClass = GetJsonString(consumer, "__CLASS");
            string command = FirstNonEmpty(
                GetJsonString(consumer, "CommandLineTemplate"),
                GetJsonString(consumer, "ExecutablePath"),
                GetJsonString(consumer, "ScriptText"));
            string id = CreateStableId("startup", "wmi", consumerClass, name, command);
            StartupCommandResolution resolution = ResolveStartupCommand(command);
            StartupPublisherInfo publisher = GetStartupPublisherInfo(resolution.executableResolved);
            var item = new StartupItem(
                id,
                string.IsNullOrWhiteSpace(name) ? consumerClass : name,
                "WMI permanent event consumer",
                @"root\subscription",
                command,
                string.IsNullOrWhiteSpace(resolution.executableResolved) ? null : resolution.executableResolved,
                isEnabled: true,
                requiresAdministrator: true,
                canToggle: false,
                "Read-only",
                BuildStartupTargetWarnings(resolution),
                sourceType: "WMI",
                category: StartupCategoryAdvanced,
                scope: "System",
                publisher.publisher,
                publisher.signatureStatus,
                publisher.isMicrosoftSigned,
                commandRaw: command,
                executableResolved: resolution.executableResolved,
                arguments: resolution.arguments,
                workingDirectory: resolution.workingDirectory,
                sourceLocation: @"root\subscription",
                lastModified: string.Empty,
                startupImpact: "High",
                confidence: string.IsNullOrWhiteSpace(command) ? "Low" : resolution.confidence,
                riskLevel: "High",
                disableMethod: "ReadOnlyWmi",
                isReadOnlyManaged: true,
                backupPayload: consumerClass,
                notes: "WMI permanent event consumers are powerful autoload hooks and are shown scan-only.");
            yield return new StartupEntry(item, StartupEntryKind.WmiConsumer, hive: null, keyPath: @"root\subscription", valueName: name, registryValueKind: null, registryValue: null, filePath: null);
        }
    }

    private static IEnumerable<StartupEntry> EnumeratePackagedStartupTasks(List<string> warnings)
    {
        string[] roots =
        [
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData"
        ];

        foreach (string keyPath in roots)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            if (key == null)
            {
                continue;
            }

            foreach (string valueName in GetRegistryValueNames(key, $@"HKCU\{keyPath}", warnings).Take(500))
            {
                object? value = key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string command = RegistryValueToDisplayString(value);
                bool looksPackaged = valueName.Contains("!", StringComparison.OrdinalIgnoreCase) ||
                    valueName.Contains("App", StringComparison.OrdinalIgnoreCase) ||
                    keyPath.Contains("AppModel", StringComparison.OrdinalIgnoreCase);
                if (!looksPackaged)
                {
                    continue;
                }

                string id = CreateStableId("startup", "packaged", keyPath, valueName);
                var item = new StartupItem(
                    id,
                    valueName,
                    "Packaged startup task",
                    $@"HKCU\{keyPath}",
                    command,
                    targetPath: null,
                    isEnabled: true,
                    requiresAdministrator: false,
                    canToggle: false,
                    "Read-only",
                    [],
                    sourceType: "Packaged app",
                    category: StartupCategoryApps,
                    scope: "Current user",
                    publisher: string.Empty,
                    signatureStatus: "Unknown",
                    isMicrosoftSigned: false,
                    commandRaw: command,
                    executableResolved: string.Empty,
                    arguments: string.Empty,
                    workingDirectory: string.Empty,
                    sourceLocation: $@"HKCU\{keyPath}\{valueName}",
                    lastModified: string.Empty,
                    startupImpact: "Medium",
                    confidence: "Low",
                    riskLevel: "Low",
                    disableMethod: "ReadOnlyPackagedTask",
                    isReadOnlyManaged: true,
                    backupPayload: command,
                    notes: "Packaged startup registration is shown as a discovery hint; Windows owns the package activation state.");
                yield return new StartupEntry(item, StartupEntryKind.PackagedTask, hive: "HKCU", keyPath, valueName, registryValueKind: null, registryValue: value, filePath: null);
            }
        }
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
        IShellLinkW? shellLink = null;
        try
        {
            Type? shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (shellLinkType == null)
            {
                return null;
            }

            shellLink = (IShellLinkW)Activator.CreateInstance(shellLinkType)!;
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(shortcutPath, 0);
            var targetBuilder = new StringBuilder(1024);
            shellLink.GetPath(targetBuilder, targetBuilder.Capacity, IntPtr.Zero, 0);
            string targetPath = targetBuilder.ToString();
            return RegistryPathNormalizer.Normalize(targetPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shellLink);
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

    private static StartupDisableMetadata CreateFileStartupDisableMetadata(StartupItem item, string disabledPath, string userAction = "Disable")
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
            new StartupFileMetadata(item.location, disabledPath),
            item.sourceType,
            item.category,
            item.scope,
            item.status,
            GetCurrentFileLockerVersion(),
            restoreStatus: "Available",
            failureDetails: string.Empty,
            userAction: userAction,
            resolvedExecutable: GetStartupResolvedTargetSnapshot(item),
            commandStatus: item.status,
            confidence: item.confidence,
            riskLevel: item.riskLevel);
    }

    internal static StartupDisableMetadata CreateScheduledTaskDisableMetadata(StartupItem item, string taskPath, string userAction = "Disable")
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
            file: null,
            item.sourceType,
            item.category,
            item.scope,
            item.status,
            GetCurrentFileLockerVersion(),
            restoreStatus: "Available",
            failureDetails: string.Empty,
            task: new StartupTaskMetadata(taskPath, wasEnabled: true),
            userAction: userAction,
            resolvedExecutable: GetStartupResolvedTargetSnapshot(item),
            commandStatus: item.status,
            confidence: item.confidence,
            riskLevel: item.riskLevel);
    }

    internal static StartupDisableMetadata CreateServiceDisableMetadata(StartupItem item, string serviceName, int originalStartType, string userAction = "Disable")
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
            file: null,
            item.sourceType,
            item.category,
            item.scope,
            item.status,
            GetCurrentFileLockerVersion(),
            restoreStatus: "Available",
            failureDetails: string.Empty,
            task: null,
            service: new StartupServiceMetadata(serviceName, originalStartType),
            userAction: userAction,
            resolvedExecutable: GetStartupResolvedTargetSnapshot(item),
            commandStatus: item.status,
            confidence: item.confidence,
            riskLevel: item.riskLevel);
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

        if (metadata.task != null && string.IsNullOrWhiteSpace(metadata.task.taskPath))
        {
            warnings.Add("The scheduled-task restore metadata is incomplete.");
            canToggle = false;
            status = "Restore unavailable";
        }

        if (metadata.service != null && (string.IsNullOrWhiteSpace(metadata.service.serviceName) || metadata.service.originalStartType is < 0 or > 4))
        {
            warnings.Add("The service restore metadata is incomplete.");
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
            warnings.ToArray(),
            metadata.sourceType,
            metadata.category,
            metadata.scope,
            publisher: string.Empty,
            signatureStatus: "Unknown",
            isMicrosoftSigned: false,
            commandRaw: metadata.command,
            executableResolved: FirstNonEmpty(metadata.resolvedExecutable, metadata.targetPath ?? string.Empty),
            arguments: string.Empty,
            workingDirectory: string.Empty,
            sourceLocation: metadata.file?.originalPath ?? metadata.location,
            lastModified: metadata.disabledAtUtc,
            startupImpact: "None",
            confidence: canToggle ? "High" : "Low",
            riskLevel: "Low",
            disableMethod: metadata.registry != null ? "RegistryValue" : metadata.file != null ? "MoveToDisabledStorage" : metadata.task != null ? "TaskScheduler" : metadata.service != null ? "ServiceControlManager" : "RestoreMetadata",
            isReadOnlyManaged: false,
            backupPayload: metadata.backupPath,
            notes: $"Disabled by FileLocker {metadata.disabledAtUtc}.");
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
            [],
            metadata.sourceType,
            metadata.category,
            metadata.scope,
            commandRaw: metadata.command,
            executableResolved: FirstNonEmpty(metadata.resolvedExecutable, metadata.targetPath ?? string.Empty),
            sourceLocation: metadata.file?.originalPath ?? metadata.location,
            lastModified: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            startupImpact: "Medium",
            confidence: "High",
            riskLevel: "Low",
            disableMethod: metadata.registry != null ? "RegistryValue" : metadata.file != null ? "MoveToDisabledStorage" : metadata.task != null ? "TaskScheduler" : metadata.service != null ? "ServiceControlManager" : "RestoreMetadata",
            backupPayload: metadata.backupPath,
            notes: "Restored from FileLocker metadata.");
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
            return ReadStartupMetadataJson<List<StartupDisableMetadata>>(path) ?? [];
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"FileLocker could not read disabled startup metadata: {ex.Message}");
            return [];
        }
    }

    private static T? ReadStartupMetadataJson<T>(string path)
    {
        string json = BoundedFileReader.ReadAllUtf8Text(
            path,
            MaxStartupMetadataJsonBytes,
            "FileLocker startup metadata is too large to read.");
        return JsonSerializer.Deserialize<T>(json, MetadataJsonOptions);
    }

    private static StartupRestoreRecord ToStartupRestoreRecord(StartupDisableMetadata metadata)
    {
        return new StartupRestoreRecord(
            metadata.id,
            metadata.name,
            metadata.source,
            metadata.location,
            metadata.command,
            metadata.targetPath,
            metadata.disabledAtUtc,
            metadata.backupPath,
            metadata.sourceType,
            metadata.category,
            metadata.scope,
            metadata.originalStatus,
            metadata.fileLockerVersion,
            metadata.restoreStatus,
            metadata.failureDetails,
            metadata.registry != null ? "RegistryValue" :
            metadata.file != null ? "StartupFolderFile" :
            metadata.task != null ? "TaskScheduler" :
            metadata.service != null ? "ServiceControlManager" :
            "Unknown",
            metadata.userAction,
            metadata.resolvedExecutable,
            metadata.commandStatus,
            metadata.confidence,
            metadata.riskLevel);
    }

    private static HashSet<string> LoadIgnoredStartupItemIds(List<string>? warnings = null)
    {
        string path = GetIgnoredStartupItemsPath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return NormalizeStoredStartupItemIds(ReadStartupMetadataJson<string[]>(path));
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"FileLocker could not read ignored startup items: {ex.Message}");
            return [];
        }
    }

    private static void SaveIgnoredStartupItemIds(IReadOnlyCollection<string> ids)
    {
        HashSet<string> normalizedIds = NormalizeStoredStartupItemIds(ids);
        FileWriteService.WriteAllTextAtomically(
            GetIgnoredStartupItemsPath(),
            JsonSerializer.Serialize(normalizedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(), MetadataJsonOptions),
            Encoding.UTF8);
    }

    private static string NormalizeRequiredStartupItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("Select a startup item first.");
        }

        string normalizedId = itemId.Trim();
        if (IsInvalidRequestId(normalizedId))
        {
            throw new InvalidOperationException("The selected startup item id is not valid.");
        }

        return normalizedId;
    }

    private static string NormalizeRequiredInstalledAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("Select an installed app first.");
        }

        string normalizedId = appId.Trim();
        if (IsInvalidRequestId(normalizedId))
        {
            throw new InvalidOperationException("The selected app id is not valid.");
        }

        return normalizedId;
    }

    private static HashSet<string> NormalizeStoredStartupItemIds(IEnumerable<string?>? ids)
    {
        var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids == null)
        {
            return normalizedIds;
        }

        foreach (string? id in ids)
        {
            if (normalizedIds.Count >= MaxRequestIdCount)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string normalizedId = id.Trim();
            if (!IsInvalidRequestId(normalizedId))
            {
                normalizedIds.Add(normalizedId);
            }
        }

        return normalizedIds;
    }

    private static bool IsInvalidRequestId(string value)
    {
        return value.Length > MaxRequestIdChars ||
            value.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format);
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

    private static string GetIgnoredStartupItemsPath()
    {
        return Path.Combine(GetSystemCareDataDirectory(), "startup-ignored.json");
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
        return FileWriteService.ResolveAvailablePath(basePath);
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
        builder.AppendLine($@"[{hiveName}\{FormatRegistryBackupKeyPath(entry.keyPath ?? string.Empty, entry.registryView)}]");
        builder.AppendLine(FormatRegistryValue(entry.valueName ?? string.Empty, entry.registryValue, entry.registryValueKind ?? RegistryValueKind.String));
        builder.AppendLine();

        WriteAllTextAtomically(backupPath, builder.ToString(), Encoding.Unicode);
        return backupPath;
    }

    private static string FormatRegistryBackupKeyPath(string keyPath, RegistryView registryView)
    {
        if (registryView != RegistryView.Registry32 ||
            !Environment.Is64BitOperatingSystem ||
            !keyPath.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase) ||
            keyPath.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase))
        {
            return keyPath;
        }

        return $@"SOFTWARE\WOW6432Node\{keyPath["SOFTWARE\\".Length..]}";
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
        RegistryValueKind valueKind,
        RegistryView registryView = RegistryView.Default)
    {
        string registryViewLabel = GetRegistryViewLabel(registryView);
        return valueKind switch
        {
            RegistryValueKind.MultiString => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, value as string[] ?? [], null, null, null, registryViewLabel),
            RegistryValueKind.DWord => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, value is int intValue ? intValue : 0, null, null, registryViewLabel),
            RegistryValueKind.QWord => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, null, value is long longValue ? longValue : 0, null, registryViewLabel),
            RegistryValueKind.Binary => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), null, null, null, null, value as byte[] ?? [], registryViewLabel),
            _ => new StartupRegistryMetadata(hive, keyPath, valueName, valueKind.ToString(), value?.ToString() ?? string.Empty, null, null, null, null, registryViewLabel)
        };
    }

    internal static void RestoreRegistryStartupItem(StartupRegistryMetadata registry)
    {
        if (!IsApprovedStartupRegistryRestorePath(registry.hive, registry.keyPath))
        {
            throw new InvalidOperationException("Startup restore metadata points outside approved startup registry locations.");
        }

        using RegistryKey root = GetRegistryRoot(registry.hive, ParseRegistryView(registry.registryView));
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

    private static RegistryView ParseRegistryView(string? registryView)
    {
        return registryView switch
        {
            "32-bit" => RegistryView.Registry32,
            "64-bit" => RegistryView.Registry64,
            _ => RegistryView.Default
        };
    }

    internal static bool IsApprovedStartupRegistryRestorePath(string? hive, string? keyPath)
    {
        return (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) &&
            (string.Equals(keyPath?.Trim('\\'), RunKeyPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(keyPath?.Trim('\\'), RunOnceKeyPath, StringComparison.OrdinalIgnoreCase));
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

    internal static void RestoreScheduledTaskItem(StartupTaskMetadata task)
    {
        if (string.IsNullOrWhiteSpace(task.taskPath))
        {
            throw new InvalidOperationException("Scheduled-task restore metadata is incomplete.");
        }

        SetScheduledTaskEnabled(task.taskPath, task.wasEnabled);
    }

    internal static void RestoreServiceStartupItem(StartupServiceMetadata service)
    {
        if (string.IsNullOrWhiteSpace(service.serviceName))
        {
            throw new InvalidOperationException("Service restore metadata is incomplete.");
        }

        if (service.originalStartType is < 0 or > 4)
        {
            throw new InvalidOperationException("Service restore metadata has an invalid start type.");
        }

        ChangeServiceStartType(service.serviceName, service.originalStartType);
    }

    internal static (string FolderPath, string TaskName) SplitScheduledTaskPath(string taskPath)
    {
        string normalized = string.IsNullOrWhiteSpace(taskPath) ? string.Empty : taskPath.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Scheduled task path is empty.");
        }

        normalized = normalized.Replace('/', '\\');
        if (!normalized.StartsWith('\\'))
        {
            normalized = "\\" + normalized;
        }

        int lastSeparator = normalized.LastIndexOf('\\');
        if (lastSeparator <= 0 || lastSeparator == normalized.Length - 1)
        {
            return ("\\", normalized.Trim('\\'));
        }

        return (normalized[..lastSeparator], normalized[(lastSeparator + 1)..]);
    }

    private static void SetScheduledTaskEnabled(string taskPath, bool enabled)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = CreateScheduleService();
            InvokeComMethod(service, "Connect");
            (string folderPath, string taskName) = SplitScheduledTaskPath(taskPath);
            folder = InvokeComMethod(service, "GetFolder", folderPath)
                ?? throw new InvalidOperationException("Task Scheduler folder could not be opened.");
            task = InvokeComMethod(folder, "GetTask", taskName)
                ?? throw new InvalidOperationException("Scheduled task could not be opened.");
            SetComProperty(task, "Enabled", enabled);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FileLocker could not update the scheduled task through Task Scheduler: {NormalizeWarningMessage(ex.Message)}", ex);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static object CreateScheduleService()
    {
        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType == null)
        {
            throw new InvalidOperationException("Task Scheduler is not available on this system.");
        }

        return Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException("Task Scheduler could not be started.");
    }

    private static object? InvokeComMethod(object target, string name, params object[] args)
    {
        return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, binder: null, target, args);
    }

    private static object? GetComProperty(object? target, string name, params object[] args)
    {
        return target?.GetType().InvokeMember(name, BindingFlags.GetProperty, binder: null, target, args);
    }

    private static string GetComStringProperty(object? target, string name)
    {
        object? value = GetComProperty(target, name);
        return NormalizeComText(value?.ToString());
    }

    internal static string NormalizeComText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxComTextChars));
        bool pendingWhitespace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
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
            if (builder.Length >= MaxComTextChars)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static int GetComIntProperty(object? target, string name)
    {
        object? value = GetComProperty(target, name);
        if (value == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static bool GetComBoolProperty(object? target, string name, bool defaultValue)
    {
        object? value = GetComProperty(target, name);
        if (value == null)
        {
            return defaultValue;
        }

        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void SetComProperty(object target, string name, object value)
    {
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, binder: null, target, [value]);
    }

    private static int QueryServiceStartType(string serviceName)
    {
        using SafeServiceHandle manager = OpenServiceControlManager(ScManagerConnect);
        using SafeServiceHandle service = OpenServiceHandle(manager, serviceName, ServiceQueryConfig);
        return QueryServiceConfigStartType(service);
    }

    private static void ChangeServiceStartType(string serviceName, int startType)
    {
        using SafeServiceHandle manager = OpenServiceControlManager(ScManagerConnect);
        using SafeServiceHandle service = OpenServiceHandle(manager, serviceName, ServiceChangeConfig);
        if (!ChangeServiceConfig(
            service,
            ServiceNoChange,
            startType,
            ServiceNoChange,
            lpBinaryPathName: null,
            lpLoadOrderGroup: null,
            lpdwTagId: IntPtr.Zero,
            lpDependencies: null,
            lpServiceStartName: null,
            lpPassword: null,
            lpDisplayName: null))
        {
            throw new InvalidOperationException($"Service Control Manager could not change {serviceName}: {GetLastWin32ErrorMessage()}");
        }
    }

    private static SafeServiceHandle OpenServiceControlManager(int desiredAccess)
    {
        SafeServiceHandle handle = OpenSCManager(null, null, desiredAccess);
        return handle.IsInvalid
            ? throw new InvalidOperationException($"Service Control Manager is unavailable: {GetLastWin32ErrorMessage()}")
            : handle;
    }

    private static SafeServiceHandle OpenServiceHandle(SafeServiceHandle manager, string serviceName, int desiredAccess)
    {
        SafeServiceHandle handle = OpenService(manager, serviceName, desiredAccess);
        return handle.IsInvalid
            ? throw new InvalidOperationException($"Service '{serviceName}' could not be opened: {GetLastWin32ErrorMessage()}")
            : handle;
    }

    private static int QueryServiceConfigStartType(SafeServiceHandle service)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out int bytesNeeded);
        if (bytesNeeded <= 0)
        {
            throw new InvalidOperationException($"Service Control Manager could not read service configuration: {GetLastWin32ErrorMessage()}");
        }

        IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                throw new InvalidOperationException($"Service Control Manager could not read service configuration: {GetLastWin32ErrorMessage()}");
            }

            var config = Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
            return config.dwStartType;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetLastWin32ErrorMessage()
    {
        return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    internal static bool IsApprovedStartupRestorePath(string? path)
    {
        if (!TryGetNormalFullyQualifiedPath(path, out string fullPath))
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
        if (!TryGetNormalFullyQualifiedPath(path, out string fullPath))
        {
            return false;
        }

        return IsUnderAnyPath(fullPath, [GetDisabledStartupItemsDirectory()], includeRoot: false);
    }

    private static bool TryGetNormalFullyQualifiedPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmedPath = path.Trim();
        if (ContainsPathControlCharacter(trimmedPath) ||
            !Path.IsPathFullyQualified(trimmedPath))
        {
            return false;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(trimmedPath);
            string root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
            string pathWithoutRoot = normalizedPath.Length > root.Length ? normalizedPath[root.Length..] : string.Empty;
            if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = normalizedPath;
            return true;
        }
        catch
        {
            return false;
        }
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

                    string displayName = NormalizeInstalledAppText(RegistryStringValue(appKey, "DisplayName"));
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    string publisher = NormalizeInstalledAppText(RegistryStringValue(appKey, "Publisher"));
                    string version = NormalizeInstalledAppText(RegistryStringValue(appKey, "DisplayVersion"));
                    string installDate = NormalizeInstallDate(RegistryStringValue(appKey, "InstallDate"));
                    long estimatedSizeBytes = RegistryLongValue(appKey, "EstimatedSize") * 1024L;
                    string installLocation = RegistryPathNormalizer.Normalize(RegistryStringValue(appKey, "InstallLocation")) ?? string.Empty;
                    string uninstallCommand = NormalizeInstalledAppCommand(RegistryStringValue(appKey, "UninstallString"));
                    string displayIcon = RegistryStringValue(appKey, "DisplayIcon");
                    string? iconDataUri = AppIconExtractor.TryGetIconDataUri(displayIcon, installLocation, uninstallCommand);
                    string keyPath = architecture == "x86" && sourceHive == "HKLM"
                        ? $@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{subKeyName}"
                        : $@"{uninstallKeyPath}\{subKeyName}";

                    app = new InstalledApp(
                        CreateStableId("app", NormalizeAppIdentity(displayName), NormalizeAppIdentity(publisher), NormalizeAppIdentity(version), RegistryPathNormalizer.Normalize(installLocation) ?? string.Empty),
                        displayName,
                        publisher,
                        version,
                        installDate,
                        Math.Max(0, estimatedSizeBytes),
                        FormatFileSize(Math.Max(0, estimatedSizeBytes)),
                        installLocation,
                        uninstallCommand,
                        sourceHive,
                        architecture,
                        requiresAdministrator: sourceHive == "HKLM",
                        canLaunchUninstaller: !string.IsNullOrWhiteSpace(uninstallCommand) && !ContainsSilentUninstallSwitch(uninstallCommand),
                        registryKeyPath: $@"{sourceHive}\{keyPath}",
                        iconDataUri: iconDataUri);
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

            string[] childDirectories = TryEnumerateLeftoverChildDirectories(current, warnings);

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

    private static string[] TryEnumerateLeftoverChildDirectories(string path, List<string> warnings)
    {
        var childDirectories = new List<string>();
        try
        {
            foreach (string child in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (childDirectories.Count >= MaxLeftoverDirectoryChildren)
                {
                    AddWarning(warnings, $"{path}: additional child folders were skipped during leftover scanning.");
                    break;
                }

                childDirectories.Add(child);
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{path}: {ex.Message}");
        }

        return childDirectories.ToArray();
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
        int inspectedItems = 0;
        DeleteLeftoverDirectory(directoryPath, ref inspectedItems);
    }

    private static void DeleteLeftoverDirectory(string directoryPath, ref int inspectedItems)
    {
        foreach (string file in EnumerateLeftoverDeleteChildren(directoryPath, directories: false, ref inspectedItems))
        {
            if (IsReparsePoint(file))
            {
                throw new InvalidOperationException("The selected cleanup path contains a reparse point.");
            }

            DeleteLeftoverFile(file);
        }

        foreach (string childDirectory in EnumerateLeftoverDeleteChildren(directoryPath, directories: true, ref inspectedItems))
        {
            if (IsReparsePoint(childDirectory))
            {
                throw new InvalidOperationException("The selected cleanup path contains a reparse point.");
            }

            DeleteLeftoverDirectory(childDirectory, ref inspectedItems);
        }

        FileCleanupService.ClearReadOnlyAttribute(directoryPath);
        Directory.Delete(directoryPath, recursive: false);
    }

    private static string[] EnumerateLeftoverDeleteChildren(string directoryPath, bool directories, ref int inspectedItems)
    {
        var children = new List<string>();
        IEnumerable<string> childPaths = directories
            ? Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly)
            : Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);

        foreach (string childPath in childPaths)
        {
            if (children.Count >= MaxLeftoverDirectoryChildren || inspectedItems >= MaxLeftoverScanFiles)
            {
                throw new InvalidOperationException("The selected cleanup path is too large to clean safely.");
            }

            inspectedItems++;
            children.Add(childPath);
        }

        return children.ToArray();
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

        while (queue.Count > 0)
        {
            if (inspected >= MaxLeftoverReparseInspectionDirectories)
            {
                return true;
            }

            string current = queue.Dequeue();
            inspected++;

            try
            {
                int childFileCount = 0;
                foreach (string child in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if (childFileCount >= MaxLeftoverDirectoryChildren)
                    {
                        return true;
                    }

                    childFileCount++;
                    if (IsReparsePoint(child))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            try
            {
                int childDirectoryCount = 0;
                foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if (childDirectoryCount >= MaxLeftoverDirectoryChildren)
                    {
                        return true;
                    }

                    childDirectoryCount++;
                    if (IsReparsePoint(child))
                    {
                        return true;
                    }

                    queue.Enqueue(child);
                }
            }
            catch
            {
                continue;
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

        string? targetPath = ParseStartupCommandTargetCandidate(normalized);
        if (targetPath is null)
        {
            return new ProcessCommand(string.Empty, string.Empty);
        }

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

    private static IReadOnlyList<Dictionary<string, string>> ParseCsvTable(string csv)
    {
        string[] lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }

        string[] headers = ParseCsvLine(lines[0]).ToArray();
        var rows = new List<Dictionary<string, string>>();
        foreach (string line in lines.Skip(1))
        {
            string[] values = ParseCsvLine(line).ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length && index < values.Length; index++)
            {
                row[headers[index]] = values[index];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<string> ParseCsvLine(string line)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        yield return current.ToString();
    }

    private static string GetCsvValue(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out string? value) ? value.Trim() : string.Empty;
    }

    private static string RunHiddenProcess(string fileName, string arguments, int timeoutMilliseconds, List<string> warnings, string label)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            if (!process.Start())
            {
                AddWarning(warnings, $"{label} scan could not start.");
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"{label}: {ex.Message}");
            return string.Empty;
        }

        Task<BoundedTextReadResult> outputTask = BoundedTextReader.ReadToEndAsync(process.StandardOutput, MaxHiddenProcessOutputChars);
        Task<BoundedTextReadResult> errorTask = BoundedTextReader.ReadToEndAsync(process.StandardError, MaxWarningMessageChars);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            AddWarning(warnings, $"{label} scan timed out.");
            return string.Empty;
        }

        BoundedTextReadResult output = outputTask.GetAwaiter().GetResult();
        BoundedTextReadResult error = errorTask.GetAwaiter().GetResult();
        if (output.Truncated)
        {
            AddWarning(warnings, $"{label} output exceeded the safe read limit.");
            return string.Empty;
        }

        if (error.Truncated)
        {
            AddWarning(warnings, $"{label}: error output was truncated.");
        }

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error.Text))
        {
            AddWarning(warnings, $"{label}: {SensitiveDataRedactor.RedactMessage(error.Text.Trim())}");
        }

        return output.Text;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => NormalizeComText(property.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => NormalizeComText(property.ToString())
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static StartupPublisherInfo GetStartupPublisherInfo(string? path)
    {
        if (!TryGetNormalFullyQualifiedPath(path, out string fullPath) || !File.Exists(fullPath))
        {
            return new StartupPublisherInfo(string.Empty, "Unknown", isMicrosoftSigned: false);
        }

        string publisher = string.Empty;
        try
        {
            publisher = FileVersionInfo.GetVersionInfo(fullPath).CompanyName?.Trim() ?? string.Empty;
        }
        catch
        {
        }

        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(fullPath);
            string subject = certificate.Subject ?? string.Empty;
            bool microsoftSigned = subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            return new StartupPublisherInfo(
                string.IsNullOrWhiteSpace(publisher) && microsoftSigned ? "Microsoft" : publisher,
                "Signed",
                microsoftSigned);
        }
        catch
        {
            return new StartupPublisherInfo(publisher, "Unsigned or unavailable", isMicrosoftSigned: false);
        }
    }

    private static string GetFileLastModifiedDisplay(string path)
    {
        try
        {
            return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HashSet<string> NormalizeIds(IReadOnlyCollection<string>? ids)
    {
        var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids is not { Count: > 0 })
        {
            return normalizedIds;
        }

        foreach (string? id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string normalizedId = id.Trim();
            if (IsInvalidRequestId(normalizedId))
            {
                throw new InvalidOperationException("One or more selected item ids are not valid.");
            }

            normalizedIds.Add(normalizedId);
            if (normalizedIds.Count > MaxRequestIdCount)
            {
                throw new InvalidOperationException("Too many selected item ids were provided.");
            }
        }

        return normalizedIds;
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
        string normalized = NormalizeInstalledAppText(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : RemoveParenthetical(normalized).Trim().ToLowerInvariant();
    }

    internal static string NormalizeInstalledAppText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        var builder = new StringBuilder(Math.Min(trimmed.Length, MaxInstalledAppTextChars));
        bool pendingSpace = false;
        foreach (char character in trimmed)
        {
            if (builder.Length >= MaxInstalledAppTextChars)
            {
                break;
            }

            if (char.IsControl(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && !char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    internal static string NormalizeInstalledAppCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxInstalledAppCommandChars ||
            trimmed.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return string.Empty;
        }

        return trimmed;
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

    private static string RegistryStringValue(RegistryKey key, string name, bool doNotExpandEnvironmentNames = false)
    {
        RegistryValueOptions options = doNotExpandEnvironmentNames
            ? RegistryValueOptions.DoNotExpandEnvironmentNames
            : RegistryValueOptions.None;
        return key.GetValue(name, defaultValue: null, options)?.ToString() ?? string.Empty;
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
            RegistryValueKind.QWord when value is long longValue => $"{name}=hex(b):{FormatRegistryQWord(longValue)}",
            RegistryValueKind.Binary when value is byte[] bytes => $"{name}=hex:{FormatRegHex(bytes)}",
            RegistryValueKind.MultiString when value is string[] strings => $"{name}=hex(7):{FormatRegHex(Encoding.Unicode.GetBytes(string.Join('\0', strings) + "\0\0"))}",
            RegistryValueKind.ExpandString => $"{name}=hex(2):{FormatRegHex(Encoding.Unicode.GetBytes((value?.ToString() ?? string.Empty) + "\0"))}",
            _ => $"{name}=\"{EscapeRegistryString(value?.ToString() ?? string.Empty)}\""
        };
    }

    private static string FormatRegistryQWord(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return FormatRegHex(bytes);
    }

    private static string FormatRegHex(byte[] bytes)
    {
        return string.Join(",", bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string EscapeRegistryString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static RegistryKey GetRegistryRoot(string? hive, RegistryView registryView = RegistryView.Default)
    {
        RegistryHive registryHive = string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;
        return RegistryKey.OpenBaseKey(registryHive, registryView);
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

    private static string[] GetProtectedStartupRoots()
    {
        return GetBlockedCleanupRoots();
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

    private static string GetCurrentFileLockerVersion()
    {
        return typeof(StartupAppMaintenanceService).Assembly.GetName().Version?.ToString() ?? string.Empty;
    }

    private sealed record StartupEntry(
        StartupItem item,
        StartupEntryKind kind,
        string? hive,
        string? keyPath,
        string? valueName,
        RegistryValueKind? registryValueKind,
        object? registryValue,
        string? filePath,
        RegistryView registryView = RegistryView.Default);

    private enum StartupEntryKind
    {
        Registry,
        File,
        ReadOnlyRegistry,
        Service,
        ScheduledTask,
        WmiConsumer,
        PackagedTask
    }

    private interface IStartupEntryProvider
    {
        IEnumerable<StartupEntry> Enumerate(List<string> warnings);
    }

    private sealed class DelegateStartupEntryProvider(string name, Func<List<string>, IEnumerable<StartupEntry>> enumerate) : IStartupEntryProvider
    {
        public IEnumerable<StartupEntry> Enumerate(List<string> warnings)
        {
            try
            {
                return enumerate(warnings).ToArray();
            }
            catch (Exception ex)
            {
                AddWarning(warnings, $"{name}: {ex.Message}");
                return [];
            }
        }
    }

    private sealed record GroupPolicyScriptSource(
        RegistryHive Hive,
        string HiveName,
        string Scope,
        string KeyPath,
        string Label,
        bool RequiresAdministrator);

    private sealed record RegistryValueSource(RegistryHive Hive, string KeyPath, string Label, string[]? ValueNames);
    private sealed record StartupPublisherInfo(string publisher, string signatureStatus, bool isMicrosoftSigned);
    private sealed record ProcessCommand(string fileName, string arguments);
    private sealed record LeftoverRoot(string path, bool requiresAdministrator);
    private sealed record DirectoryScanSummary(long SizeBytes, int FileCount, int SkippedCount, string[] Warnings);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManager(string? lpMachineName, string? lpDatabaseName, int dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle hSCManager, string lpServiceName, int dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeServiceHandle hService,
        int dwServiceType,
        int dwStartType,
        int dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle hService, IntPtr lpServiceConfig, int cbBufSize, out int pcbBytesNeeded);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryServiceConfigNative
    {
        public int dwServiceType;
        public int dwStartType;
        public int dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public int dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }
}

internal sealed record StartupCommandResolution(
    string commandRaw,
    string executableResolved,
    string arguments,
    string workingDirectory,
    string launcherName,
    string status,
    string confidence,
    string riskLevel,
    string notes);

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
    string[] warnings,
    string sourceType = "Startup",
    string category = "Startup Apps",
    string scope = "",
    string publisher = "",
    string signatureStatus = "Unknown",
    bool isMicrosoftSigned = false,
    string commandRaw = "",
    string executableResolved = "",
    string arguments = "",
    string workingDirectory = "",
    string sourceLocation = "",
    string lastModified = "",
    string startupImpact = "Unknown",
    string confidence = "Medium",
    string riskLevel = "Low",
    string disableMethod = "",
    bool isReadOnlyManaged = false,
    string backupPayload = "",
    string notes = "",
    bool isIgnored = false);

internal sealed record StartupScanResult(
    StartupItem[] items,
    int enabledCount,
    int disabledCount,
    int brokenCount,
    int advancedCount,
    int restoreRecordCount,
    int ignoredCount,
    StartupRestoreRecord[] restoreRecords,
    string[] warnings);

internal sealed record StartupToggleResult(
    StartupItem item,
    bool isEnabled,
    string backupPath,
    string message);

internal sealed record StartupIgnoreResult(
    string itemId,
    bool isIgnored,
    string message);

internal sealed record StartupExportResult(
    string exportPath,
    string fileName,
    string itemId,
    bool fullPathsIncluded = true);

internal sealed record StartupRestoreRecord(
    string id,
    string name,
    string source,
    string location,
    string command,
    string? targetPath,
    string timestampUtc,
    string backupPath,
    string sourceType,
    string category,
    string scope,
    string originalStatus,
    string fileLockerVersion,
    string restoreStatus,
    string failureDetails,
    string restoreMethod,
    string userAction,
    string resolvedExecutable,
    string commandStatus,
    string confidence,
    string riskLevel);

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
    StartupFileMetadata? file,
    string sourceType = "Startup",
    string category = "Startup Apps",
    string scope = "",
    string originalStatus = "",
    string fileLockerVersion = "",
    string restoreStatus = "Available",
    string failureDetails = "",
    StartupTaskMetadata? task = null,
    StartupServiceMetadata? service = null,
    string userAction = "Disable",
    string resolvedExecutable = "",
    string commandStatus = "",
    string confidence = "",
    string riskLevel = "");

internal sealed record StartupRegistryMetadata(
    string hive,
    string keyPath,
    string valueName,
    string valueKind,
    string? stringValue,
    string[]? multiStringValue,
    int? dwordValue,
    long? qwordValue,
    byte[]? binaryValue,
    string registryView = "");

internal sealed record StartupFileMetadata(
    string originalPath,
    string disabledPath);

internal sealed record StartupTaskMetadata(
    string taskPath,
    bool wasEnabled);

internal sealed record StartupServiceMetadata(
    string serviceName,
    int originalStartType);

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
    string registryKeyPath,
    string? iconDataUri);

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

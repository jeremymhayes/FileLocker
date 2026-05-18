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
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class SystemMaintenanceService
{
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;
    private const int MaxCleanupScanFiles = 100_000;
    private static readonly TimeSpan DriveToolTimeout = TimeSpan.FromMinutes(120);

    internal static MaintenanceDriveList GetDrives()
    {
        MaintenanceDrive[] drives = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(ToMaintenanceDrive)
            .OrderBy(drive => drive.rootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MaintenanceDriveList(drives);
    }

    internal static CleanupScanResult ScanCleanup(IReadOnlyCollection<string>? categoryIds)
    {
        HashSet<string> selectedIds = NormalizeCategoryIds(categoryIds);
        CleanupCategory[] categories = GetCleanupDefinitions()
            .Where(definition => selectedIds.Count == 0 || selectedIds.Contains(definition.Id))
            .Select(ScanCleanupCategory)
            .ToArray();

        return new CleanupScanResult(
            categories,
            categories.Sum(category => category.sizeBytes),
            FormatFileSize(categories.Sum(category => category.sizeBytes)),
            categories.Sum(category => category.fileCount),
            categories.Sum(category => category.skippedCount));
    }

    internal static CleanupRunResult RunCleanup(IReadOnlyCollection<string>? categoryIds)
    {
        HashSet<string> selectedIds = NormalizeCategoryIds(categoryIds);
        CleanupDefinition[] definitions = GetCleanupDefinitions()
            .Where(definition => selectedIds.Count == 0 || selectedIds.Contains(definition.Id))
            .ToArray();

        if (definitions.Any(definition => definition.RequiresAdministrator))
        {
            RequireAdministrator("Cleaning protected system locations");
        }

        var cleanedCategories = new List<CleanupCategory>();
        var warnings = new List<string>();
        long freedBytes = 0;
        int deletedFiles = 0;
        int skippedItems = 0;

        foreach (CleanupDefinition definition in definitions)
        {
            CleanupCategory before = ScanCleanupCategory(definition);
            CleanupDeleteSummary deleted = definition.Id == "recycleBin"
                ? EmptyRecycleBin(before)
                : DeleteDirectoryContents(definition);

            freedBytes += deleted.FreedBytes;
            deletedFiles += deleted.DeletedFiles;
            skippedItems += deleted.SkippedItems;
            warnings.AddRange(deleted.Warnings);

            CleanupCategory after = ScanCleanupCategory(definition);
            cleanedCategories.Add(after with
            {
                status = deleted.DeletedFiles > 0 || deleted.FreedBytes > 0
                    ? $"Cleaned {FormatFileSize(deleted.FreedBytes)}"
                    : after.status
            });
        }

        return new CleanupRunResult(
            cleanedCategories.ToArray(),
            freedBytes,
            FormatFileSize(freedBytes),
            deletedFiles,
            skippedItems,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static bool IsRunningAsAdministrator()
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

    internal static async Task<MaintenanceToolResult> OptimizeDriveAsync(string? driveRoot, string? mode)
    {
        string root = NormalizeDriveRoot(driveRoot);
        RequireAdministrator("Drive analysis and optimization");

        string normalizedMode = string.Equals(mode, "optimize", StringComparison.OrdinalIgnoreCase)
            ? "optimize"
            : "analyze";

        string[] args = normalizedMode == "optimize"
            ? [root, "/O"]
            : [root, "/A"];

        ProcessRunResult process = await RunProcessAsync("defrag.exe", args, DriveToolTimeout);
        string title = normalizedMode == "optimize" ? "Drive optimization" : "Drive analysis";
        return new MaintenanceToolResult(
            process.ExitCode == 0,
            title,
            process.ExitCode == 0
                ? $"{title} completed for {root}."
                : $"{title} exited with code {process.ExitCode} for {root}.",
            root,
            process.Output,
            process.StartedAtUtc,
            process.CompletedAtUtc);
    }

    internal static async Task<MaintenanceToolResult> WipeFreeSpaceAsync(string? driveRoot, string? confirmation)
    {
        if (!string.Equals(confirmation, "WIPE FREE SPACE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm the free-space wipe before starting.");
        }

        string root = NormalizeDriveRoot(driveRoot);
        RequireAdministrator("Free-space wiping");

        ProcessRunResult process = await RunProcessAsync("cipher.exe", [$"/w:{root}"], DriveToolTimeout);
        return new MaintenanceToolResult(
            process.ExitCode == 0,
            "Free-space wipe",
            process.ExitCode == 0
                ? $"Free-space wipe completed for {root}."
                : $"Free-space wipe exited with code {process.ExitCode} for {root}.",
            root,
            process.Output,
            process.StartedAtUtc,
            process.CompletedAtUtc);
    }

    internal static RegistryScanResult ScanRegistry()
    {
        var issues = new List<RegistryIssue>();
        var warnings = new List<string>();

        ScanStartupEntries(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", issues, warnings);
        ScanStartupEntries(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", issues, warnings);
        ScanUninstallEntries(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall", issues, warnings);
        ScanUninstallEntries(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Uninstall", issues, warnings);
        ScanUninstallEntries(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", issues, warnings);

        RegistryIssue[] orderedIssues = issues
            .GroupBy(issue => issue.id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(issue => issue.hive, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.displayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RegistryScanResult(
            orderedIssues,
            orderedIssues.Length,
            orderedIssues.Length == 0 ? "No stale bounded registry entries found." : $"{orderedIssues.Length} bounded registry issue(s) found.",
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static RegistryCleanResult CleanRegistry(IReadOnlyCollection<string>? issueIds, string? confirmation)
    {
        if (!string.Equals(confirmation, "FIX REGISTRY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm registry cleanup before cleaning bounded registry entries.");
        }

        HashSet<string> selectedIds = issueIds is { Count: > 0 }
            ? issueIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        RegistryScanResult scan = ScanRegistry();
        RegistryIssue[] selectedIssues = scan.issues
            .Where(issue => issue.canClean && (selectedIds.Count == 0 || selectedIds.Contains(issue.id)))
            .ToArray();

        if (selectedIssues.Any(issue => string.Equals(issue.hive, "HKLM", StringComparison.OrdinalIgnoreCase)))
        {
            RequireAdministrator("Cleaning local-machine registry entries");
        }

        if (selectedIssues.Length == 0)
        {
            return new RegistryCleanResult(0, 0, string.Empty, [], [], scan);
        }

        string backupPath = WriteRegistryBackup(selectedIssues);
        var cleaned = new List<RegistryIssue>();
        var failures = new List<RegistryCleanFailure>();

        foreach (RegistryIssue issue in selectedIssues)
        {
            try
            {
                CleanRegistryIssue(issue);
                cleaned.Add(issue);
            }
            catch (Exception ex)
            {
                failures.Add(new RegistryCleanFailure(issue.id, issue.displayName, ex.Message));
            }
        }

        return new RegistryCleanResult(
            cleaned.Count,
            failures.Count,
            backupPath,
            cleaned.ToArray(),
            failures.ToArray(),
            ScanRegistry());
    }

    private static MaintenanceDrive ToMaintenanceDrive(DriveInfo drive)
    {
        bool isReady = drive.IsReady;
        long totalSizeBytes = isReady ? drive.TotalSize : 0;
        long freeSpaceBytes = isReady ? drive.AvailableFreeSpace : 0;
        return new MaintenanceDrive(
            drive.Name,
            string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
            drive.Name,
            drive.DriveType.ToString(),
            isReady ? drive.DriveFormat : "Unavailable",
            totalSizeBytes,
            FormatFileSize(totalSizeBytes),
            freeSpaceBytes,
            FormatFileSize(freeSpaceBytes),
            isReady);
    }

    private static HashSet<string> NormalizeCategoryIds(IReadOnlyCollection<string>? categoryIds)
    {
        return categoryIds is { Count: > 0 }
            ? categoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static CleanupDefinition[] GetCleanupDefinitions()
    {
        string windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string windowsTemp = string.IsNullOrWhiteSpace(windowsPath)
            ? string.Empty
            : Path.Combine(windowsPath, "Temp");

        return
        [
            new CleanupDefinition(
                "userTemp",
                "Windows",
                "User temporary files",
                "Temporary files created by apps under the current Windows user.",
                Path.GetFullPath(Path.GetTempPath()),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "recycleBin",
                "Windows",
                "Recycle Bin",
                "Files already deleted through Windows and waiting in the Recycle Bin.",
                string.Empty,
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "windowsTemp",
                "Windows",
                "Windows temporary files",
                "System temporary files that are safe to attempt but may require elevation for protected items.",
                windowsTemp,
                SupportsDeletion: true,
                RequiresAdministrator: true,
                DefaultSelected: false),
            new CleanupDefinition(
                "userCrashDumps",
                "Windows",
                "User crash dumps",
                "Crash dump files written by apps after failures.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: false),
            new CleanupDefinition(
                "windowsErrorReports",
                "Windows",
                "Windows error reports",
                "Archived Windows Error Reporting files for the current user.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: false),
            new CleanupDefinition(
                "edgeCache",
                "Browser",
                "Microsoft Edge cache",
                "Cached web content from the default Edge profile. Close Edge for the most complete cleanup.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "edgeCodeCache",
                "Browser",
                "Microsoft Edge code cache",
                "Compiled script cache from the default Edge profile.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Code Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "chromeCache",
                "Browser",
                "Google Chrome cache",
                "Cached web content from the default Chrome profile. Close Chrome for the most complete cleanup.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "chromeCodeCache",
                "Browser",
                "Google Chrome code cache",
                "Compiled script cache from the default Chrome profile.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Code Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: true),
            new CleanupDefinition(
                "discordCache",
                "Applications",
                "Discord cache",
                "Local Discord cache files. Close Discord for the most complete cleanup.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: false),
            new CleanupDefinition(
                "teamsCache",
                "Applications",
                "Microsoft Teams cache",
                "Local Teams cache files from the classic desktop client.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Teams", "Cache"),
                SupportsDeletion: true,
                RequiresAdministrator: false,
                DefaultSelected: false)
        ];
    }

    private static CleanupCategory ScanCleanupCategory(CleanupDefinition definition)
    {
        if (definition.Id == "recycleBin")
        {
            return ScanRecycleBin(definition);
        }

        if (string.IsNullOrWhiteSpace(definition.Path) || !Directory.Exists(definition.Path))
        {
            return new CleanupCategory(
                definition.Id,
                definition.Group,
                definition.Label,
                definition.Description,
                definition.Path,
                0,
                FormatFileSize(0),
                0,
                0,
                false,
                definition.RequiresAdministrator,
                definition.DefaultSelected,
                "Location unavailable",
                [$"{definition.Label} could not be found."]);
        }

        DirectoryScanSummary summary = ScanDirectory(definition.Path);
        return new CleanupCategory(
            definition.Id,
            definition.Group,
            definition.Label,
            definition.Description,
            definition.Path,
            summary.SizeBytes,
            FormatFileSize(summary.SizeBytes),
            summary.FileCount,
            summary.SkippedCount,
            definition.SupportsDeletion,
            definition.RequiresAdministrator,
            definition.DefaultSelected,
            summary.FileCount > 0 ? "Ready to clean" : "Already clean",
            summary.Warnings);
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
            AttributesToSkip = FileAttributes.System
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(rootPath, "*", options))
            {
                if (fileCount >= MaxCleanupScanFiles)
                {
                    warnings.Add($"Scan stopped after {MaxCleanupScanFiles.ToString(CultureInfo.InvariantCulture)} files.");
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
            warnings.Add(ex.Message);
            skippedCount++;
        }

        return new DirectoryScanSummary(sizeBytes, fileCount, skippedCount, warnings.ToArray());
    }

    private static CleanupCategory ScanRecycleBin(CleanupDefinition definition)
    {
        var info = new SHQUERYRBINFO
        {
            cbSize = Marshal.SizeOf<SHQUERYRBINFO>()
        };

        int result = SHQueryRecycleBin(null, ref info);
        if (result != 0)
        {
            return new CleanupCategory(
                definition.Id,
                definition.Group,
                definition.Label,
                definition.Description,
                definition.Path,
                0,
                FormatFileSize(0),
                0,
                1,
                false,
                definition.RequiresAdministrator,
                definition.DefaultSelected,
                "Unable to query",
                [$"Recycle Bin query failed with HRESULT 0x{result:X8}."]);
        }

        return new CleanupCategory(
            definition.Id,
            definition.Group,
            definition.Label,
            definition.Description,
            definition.Path,
            info.i64Size,
            FormatFileSize(info.i64Size),
            (int)Math.Min(info.i64NumItems, int.MaxValue),
            0,
            definition.SupportsDeletion,
            definition.RequiresAdministrator,
            definition.DefaultSelected,
            info.i64NumItems > 0 ? "Ready to clean" : "Already clean",
            []);
    }

    private static CleanupDeleteSummary DeleteDirectoryContents(CleanupDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Path) || !Directory.Exists(definition.Path))
        {
            return new CleanupDeleteSummary(0, 0, 0, [$"{definition.Label} could not be found."]);
        }

        string approvedRoot = Path.GetFullPath(definition.Path);
        if (!IsApprovedCleanupRoot(definition.Id, approvedRoot))
        {
            return new CleanupDeleteSummary(0, 0, 1, [$"{definition.Label} is not an approved cleanup location."]);
        }

        long freedBytes = 0;
        int deletedFiles = 0;
        int skippedItems = 0;
        var warnings = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(approvedRoot, "*", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    long size = info.Exists ? info.Length : 0;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    freedBytes += size;
                    deletedFiles++;
                }
                catch
                {
                    skippedItems++;
                }
            }

            foreach (string directory in Directory.EnumerateDirectories(approvedRoot, "*", options).OrderByDescending(path => path.Length))
            {
                try
                {
                    if (!string.Equals(Path.GetFullPath(directory), approvedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(directory, recursive: false);
                    }
                }
                catch
                {
                    skippedItems++;
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add(ex.Message);
            skippedItems++;
        }

        return new CleanupDeleteSummary(freedBytes, deletedFiles, skippedItems, warnings.ToArray());
    }

    private static CleanupDeleteSummary EmptyRecycleBin(CleanupCategory before)
    {
        uint result = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        if (result != 0)
        {
            return new CleanupDeleteSummary(0, 0, 1, [$"Recycle Bin cleanup failed with HRESULT 0x{result:X8}."]);
        }

        return new CleanupDeleteSummary(before.sizeBytes, before.fileCount, 0, []);
    }

    private static bool IsApprovedCleanupRoot(string categoryId, string path)
    {
        string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        CleanupDefinition? definition = GetCleanupDefinitions()
            .FirstOrDefault(item => string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        string expected = string.IsNullOrWhiteSpace(definition?.Path)
            ? string.Empty
            : Path.GetFullPath(definition.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.IsNullOrWhiteSpace(expected) &&
               string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveRoot(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new InvalidOperationException("Select a drive first.");
        }

        string fullPath = Path.GetFullPath(driveRoot.Trim());
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The selected drive is invalid.");
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            throw new InvalidOperationException("The selected drive is not ready.");
        }

        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            throw new InvalidOperationException("Only fixed or removable drives can be maintained from this workflow.");
        }

        return drive.RootDirectory.FullName;
    }

    private static async Task<ProcessRunResult> RunProcessAsync(string fileName, IReadOnlyCollection<string> arguments, TimeSpan timeout)
    {
        DateTime startedAtUtc = DateTime.UtcNow;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutToken = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutToken.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ProcessRunResult(-1, $"Timed out after {timeout.TotalMinutes:N0} minutes.", startedAtUtc, DateTime.UtcNow);
        }

        string output = string.Join(
            Environment.NewLine,
            new[] { await outputTask, await errorTask }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new ProcessRunResult(process.ExitCode, output.Trim(), startedAtUtc, DateTime.UtcNow);
    }

    private static void ScanStartupEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
            if (key == null)
            {
                return;
            }

            foreach (string valueName in key.GetValueNames())
            {
                string? rawValue = key.GetValue(valueName)?.ToString();
                string? targetPath = ExtractExecutablePath(rawValue);
                if (string.IsNullOrWhiteSpace(targetPath) || !IsMissingPath(targetPath))
                {
                    continue;
                }

                string displayName = string.IsNullOrWhiteSpace(valueName) ? "(Default startup entry)" : valueName;
                issues.Add(CreateRegistryIssue(
                    hive,
                    keyPath,
                    "Startup entry",
                    displayName,
                    targetPath,
                    "Startup entry points to a missing file.",
                    valueName,
                    subKeyName: null));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {ex.Message}");
        }
    }

    private static void ScanUninstallEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
            if (key == null)
            {
                return;
            }

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? subKey = key.OpenSubKey(subKeyName, writable: false);
                    string displayName = subKey?.GetValue("DisplayName")?.ToString() ?? subKeyName;
                    string? uninstallTarget = ExtractExecutablePath(subKey?.GetValue("UninstallString")?.ToString());
                    string? displayTarget = ExtractExecutablePath(subKey?.GetValue("DisplayIcon")?.ToString());
                    string? installLocation = NormalizeRegistryPath(subKey?.GetValue("InstallLocation")?.ToString());
                    string? targetPath = uninstallTarget ?? displayTarget;

                    if (string.IsNullOrWhiteSpace(targetPath) || !IsMissingPath(targetPath))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(installLocation) && !IsMissingPath(installLocation))
                    {
                        continue;
                    }

                    issues.Add(CreateRegistryIssue(
                        hive,
                        keyPath,
                        "Uninstall entry",
                        displayName,
                        targetPath,
                        "Uninstall entry points to a missing application path.",
                        valueName: null,
                        subKeyName));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{hive}\\{keyPath}\\{subKeyName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {ex.Message}");
        }
    }

    private static RegistryIssue CreateRegistryIssue(
        string hive,
        string keyPath,
        string kind,
        string displayName,
        string targetPath,
        string reason,
        string? valueName,
        string? subKeyName)
    {
        string id = CreateRegistryIssueId(hive, keyPath, valueName, subKeyName, kind);
        return new RegistryIssue(
            id,
            hive,
            keyPath,
            valueName,
            subKeyName,
            kind,
            displayName,
            targetPath,
            reason,
            "Medium",
            true);
    }

    private static string CreateRegistryIssueId(string hive, string keyPath, string? valueName, string? subKeyName, string kind)
    {
        string raw = string.Join("|", hive, keyPath, valueName ?? string.Empty, subKeyName ?? string.Empty, kind);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string? ExtractExecutablePath(string? command)
    {
        string? normalized = NormalizeRegistryPath(command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        string lower = normalized.ToLowerInvariant();
        if (lower.StartsWith("msiexec", StringComparison.Ordinal) ||
            lower.StartsWith("rundll32", StringComparison.Ordinal) ||
            lower.StartsWith("{", StringComparison.Ordinal))
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

        foreach (string extension in new[] { ".exe", ".bat", ".cmd", ".com", ".msi" })
        {
            int index = normalized.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized[..(index + extension.Length)].Trim().Trim('"');
            }
        }

        return null;
    }

    private static string? NormalizeRegistryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return expanded.Trim('"').Trim();
    }

    private static bool IsMissingPath(string path)
    {
        string normalized = path.Trim().Trim('"');
        return !File.Exists(normalized) && !Directory.Exists(normalized);
    }

    private static string WriteRegistryBackup(IReadOnlyCollection<RegistryIssue> issues)
    {
        string backupDirectory = Path.Combine(GetAppDataDirectory(), "RegistryBackups");
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, $"FileLocker-RegistryBackup-{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        var builder = new StringBuilder();
        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.AppendLine();

        foreach (RegistryIssue issue in issues)
        {
            AppendRegistryIssueBackup(builder, issue);
        }

        File.WriteAllText(backupPath, builder.ToString(), Encoding.Unicode);
        return backupPath;
    }

    private static void AppendRegistryIssueBackup(StringBuilder builder, RegistryIssue issue)
    {
        RegistryKey root = GetRegistryRoot(issue.hive);
        string hiveName = GetRegistryHiveName(issue.hive);

        if (!string.IsNullOrWhiteSpace(issue.valueName))
        {
            using RegistryKey? key = root.OpenSubKey(issue.keyPath, writable: false);
            object? value = key?.GetValue(issue.valueName);
            RegistryValueKind kind = key?.GetValueKind(issue.valueName) ?? RegistryValueKind.String;
            builder.AppendLine($"[{hiveName}\\{issue.keyPath}]");
            builder.AppendLine(FormatRegistryValue(issue.valueName, value, kind));
            builder.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(issue.subKeyName))
        {
            using RegistryKey? key = root.OpenSubKey($@"{issue.keyPath}\{issue.subKeyName}", writable: false);
            if (key != null)
            {
                AppendRegistryKeySnapshot(builder, key, $@"{hiveName}\{issue.keyPath}\{issue.subKeyName}");
            }
        }
    }

    private static void AppendRegistryKeySnapshot(StringBuilder builder, RegistryKey key, string fullPath)
    {
        builder.AppendLine($"[{fullPath}]");
        foreach (string valueName in key.GetValueNames())
        {
            object? value = key.GetValue(valueName);
            RegistryValueKind kind = key.GetValueKind(valueName);
            builder.AppendLine(FormatRegistryValue(valueName, value, kind));
        }

        builder.AppendLine();

        foreach (string subKeyName in key.GetSubKeyNames())
        {
            using RegistryKey? subKey = key.OpenSubKey(subKeyName, writable: false);
            if (subKey != null)
            {
                AppendRegistryKeySnapshot(builder, subKey, $@"{fullPath}\{subKeyName}");
            }
        }
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

    private static void CleanRegistryIssue(RegistryIssue issue)
    {
        RegistryKey root = GetRegistryRoot(issue.hive);

        if (!string.IsNullOrWhiteSpace(issue.valueName))
        {
            using RegistryKey? key = root.OpenSubKey(issue.keyPath, writable: true);
            key?.DeleteValue(issue.valueName, throwOnMissingValue: false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(issue.subKeyName))
        {
            using RegistryKey? key = root.OpenSubKey(issue.keyPath, writable: true);
            key?.DeleteSubKeyTree(issue.subKeyName, throwOnMissingSubKey: false);
        }
    }

    private static RegistryKey GetRegistryRoot(string hive)
    {
        return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? Registry.LocalMachine
            : Registry.CurrentUser;
    }

    private static string GetRegistryHiveName(string hive)
    {
        return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            ? "HKEY_LOCAL_MACHINE"
            : "HKEY_CURRENT_USER";
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

    private static string GetAppDataDirectory()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker");
        Directory.CreateDirectory(path);
        return path;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    private sealed record CleanupDefinition(
        string Id,
        string Group,
        string Label,
        string Description,
        string Path,
        bool SupportsDeletion,
        bool RequiresAdministrator,
        bool DefaultSelected);

    private sealed record DirectoryScanSummary(long SizeBytes, int FileCount, int SkippedCount, string[] Warnings);

    private sealed record CleanupDeleteSummary(long FreedBytes, int DeletedFiles, int SkippedItems, string[] Warnings);

    private sealed record ProcessRunResult(int ExitCode, string Output, DateTime StartedAtUtc, DateTime CompletedAtUtc);
}

internal sealed record MaintenanceDrive(
    string id,
    string name,
    string rootPath,
    string driveType,
    string driveFormat,
    long totalSizeBytes,
    string totalSizeDisplay,
    long freeSpaceBytes,
    string freeSpaceDisplay,
    bool isReady);

internal sealed record MaintenanceDriveList(MaintenanceDrive[] drives);

internal sealed record CleanupCategory(
    string id,
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

internal sealed record CleanupScanResult(
    CleanupCategory[] categories,
    long totalBytes,
    string totalDisplay,
    int totalFiles,
    int skippedItems);

internal sealed record CleanupRunResult(
    CleanupCategory[] categories,
    long freedBytes,
    string freedDisplay,
    int deletedFiles,
    int skippedItems,
    string[] warnings);

internal sealed record MaintenanceToolResult(
    bool ok,
    string title,
    string message,
    string driveRoot,
    string output,
    DateTime startedAtUtc,
    DateTime completedAtUtc);

internal sealed record RegistryIssue(
    string id,
    string hive,
    string keyPath,
    string? valueName,
    string? subKeyName,
    string kind,
    string displayName,
    string targetPath,
    string reason,
    string severity,
    bool canClean);

internal sealed record RegistryScanResult(
    RegistryIssue[] issues,
    int issueCount,
    string status,
    string[] warnings);

internal sealed record RegistryCleanFailure(
    string issueId,
    string displayName,
    string message);

internal sealed record RegistryCleanResult(
    int cleanedCount,
    int failedCount,
    string backupPath,
    RegistryIssue[] cleanedIssues,
    RegistryCleanFailure[] failures,
    RegistryScanResult scan);

using Microsoft.Win32;
using System.Buffers.Binary;
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
    private const int MaxCleanupChildDirectories = 1_000;
    private const int MaxCleanupOperationWarnings = 8;
    private const int MaxRegistryScanWarnings = 32;
    private const int MaxRegistryClassKeys = 5_000;
    private const int MaxMaintenanceToolOutputChars = 16 * 1024;
    private const int MaxWarningMessageChars = 2048;
    private const int MaxRequestIdCount = 500;
    private const int MaxRequestIdChars = 256;
    internal static readonly TimeSpan DriveToolTimeout = TimeSpan.FromMinutes(120);
    private static readonly string[] RegistryExecutableExtensions = [".exe", ".bat", ".cmd", ".com", ".msi", ".dll", ".ocx", ".cpl", ".scr", ".chm", ".hlp"];

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

    internal static CleanupRunResult RunCleanup(IReadOnlyCollection<string>? categoryIds, string? confirmation)
    {
        if (!string.Equals(confirmation, "CLEAN SELECTED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm cleanup before deleting selected cleanup categories.");
        }

        HashSet<string> selectedIds = NormalizeCategoryIds(categoryIds);
        if (selectedIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one cleanup category.");
        }

        CleanupDefinition[] definitions = GetCleanupDefinitions()
            .Where(definition => selectedIds.Contains(definition.Id))
            .ToArray();

        if (definitions.Length != selectedIds.Count)
        {
            throw new InvalidOperationException("The selected cleanup categories are no longer available.");
        }

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
            NormalizeWarnings(warnings, MaxCleanupOperationWarnings, "Additional cleanup warnings were omitted."));
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

    internal static void RequireAdministratorForBridge(string operationName) => RequireAdministrator(operationName);

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
        ScanApplicationPathEntries(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths", issues, warnings);
        ScanApplicationPathEntries(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\App Paths", issues, warnings);
        ScanApplicationPathEntries(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths", issues, warnings);
        ScanComServerEntries(Registry.CurrentUser, "HKCU", @"Software\Classes\CLSID", issues, warnings);
        ScanComServerEntries(Registry.LocalMachine, "HKLM", @"Software\Classes\CLSID", issues, warnings);
        ScanComServerEntries(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Classes\CLSID", issues, warnings);
        ScanFileExtensionEntries(Registry.CurrentUser, "HKCU", @"Software\Classes", issues, warnings);
        ScanFileExtensionEntries(Registry.LocalMachine, "HKLM", @"Software\Classes", issues, warnings);
        ScanSharedDllEntries(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", issues, warnings);
        ScanSharedDllEntries(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", issues, warnings);
        ScanHelpFileReferences(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\Help", issues, warnings);
        ScanHelpFileReferences(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\Help", issues, warnings);

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
            NormalizeWarnings(warnings, MaxRegistryScanWarnings, "Additional registry scan warnings were omitted."));
    }

    internal static RegistryCleanResult CleanRegistry(IReadOnlyCollection<string>? issueIds, string? confirmation)
    {
        if (!string.Equals(confirmation, "FIX REGISTRY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirm registry cleanup before cleaning bounded registry entries.");
        }

        HashSet<string> selectedIds = NormalizeSelectedIds(issueIds);
        if (selectedIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one registry issue.");
        }

        RegistryScanResult scan = ScanRegistry();
        RegistryIssue[] selectedIssues = scan.issues
            .Where(issue => issue.canClean && selectedIds.Contains(issue.id))
            .ToArray();

        if (selectedIssues.Length != selectedIds.Count)
        {
            throw new InvalidOperationException("The selected registry issues are no longer available.");
        }

        if (selectedIssues.Any(issue => string.Equals(issue.hive, "HKLM", StringComparison.OrdinalIgnoreCase)))
        {
            RequireAdministrator("Cleaning local-machine registry entries");
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
                failures.Add(new RegistryCleanFailure(
                    issue.id,
                    issue.displayName,
                    NormalizeWarningMessage(ex.Message)));
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
        DriveMediaInfo media = !isReady
            ? new DriveMediaInfo("Unknown", "Unsupported", "The drive is not ready.")
            : drive.DriveType == DriveType.Removable
                ? DriveMediaTypeDetector.Removable()
                : DriveMediaTypeDetector.DetectForDriveRoot(drive.Name);

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
            isReady,
            media.mediaType,
            media.mediaDetectionStatus,
            media.mediaDescription);
    }

    private static HashSet<string> NormalizeCategoryIds(IReadOnlyCollection<string>? categoryIds)
    {
        return NormalizeSelectedIds(categoryIds);
    }

    private static HashSet<string> NormalizeSelectedIds(IReadOnlyCollection<string>? ids)
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
            if (normalizedId.Length > MaxRequestIdChars)
            {
                throw new InvalidOperationException("One or more selected item ids are not valid.");
            }

            if (normalizedId.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
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

    private static CleanupDefinition[] GetCleanupDefinitions()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localLow = GetLocalLowPath(userProfile);
        string systemDrive = GetSystemDriveRoot(windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] chromiumProfiles = GetChromiumProfileDirectories(local, roaming);
        string[] firefoxLocalProfiles = GetFirefoxProfileDirectories(local);
        string[] firefoxRoamingProfiles = GetFirefoxProfileDirectories(roaming);

        return
        [
            CleanupItem("userTemp", "Windows", "User Temporary Files", "Temporary files created by apps under the current Windows user.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Temporary files and folders in your user temp folder."], ["Personal files outside the temp folder."], "Safe to clean regularly.", [DirectoryTarget(Path.GetFullPath(Path.GetTempPath()))]),
            CleanupItem("systemTemp", "Windows", "System Temporary Files", "System temporary files created by Windows and installers.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Temporary files under the Windows temp folder."], ["Live system files and protected Windows folders."], "Review before cleaning because protected files may be skipped.", [DirectoryTarget(CombinePath(windows, "Temp"))]),
            CleanupItem("recycleBin", "Windows", "Recycle Bin", "Files already deleted through Windows and waiting in the Recycle Bin.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Current Recycle Bin contents."], ["Already-deleted traces in drive free space until Free-Space Sanitizer is run."], "Clean this first, then use Free-Space Sanitizer for a 3-pass overwrite of deleted-file traces.", []),
            CleanupItem("thumbnailCache", "Windows", "Thumbnail Cache", "Cached image and video thumbnails used by File Explorer previews.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Explorer thumbnail cache database files."], ["Your photos, videos, and documents."], "Safe to clean. Windows rebuilds thumbnails as needed.", [DirectoryTarget(CombinePath(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", recursive: false)]),
            CleanupItem("iconCache", "Windows", "Icon Cache", "Cached icon databases used by File Explorer and the Start menu.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Explorer icon cache database files."], ["Apps, shortcuts, and pinned items."], "Safe to clean. Windows rebuilds icons as needed.", [DirectoryTarget(CombinePath(local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", recursive: false)]),
            CleanupItem("windowsErrorReports", "Windows", "Windows Error Reports", "Archived Windows Error Reporting files for apps and system components.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Archived error report files."], ["Installed applications and personal files."], "Safe to clean after you no longer need crash diagnostics.", [DirectoryTarget(CombinePath(local, "Microsoft", "Windows", "WER")), DirectoryTarget(CombinePath(programData, "Microsoft", "Windows", "WER"))]),
            CleanupItem("userCrashDumps", "Windows", "User Crash Dumps", "Crash dump files written by apps after failures.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["User-mode crash dump files."], ["Application settings and documents."], "Review if you are actively troubleshooting app crashes.", [DirectoryTarget(CombinePath(local, "CrashDumps"))]),
            CleanupItem("systemMemoryDumps", "Windows", "System Memory Dumps", "Large crash dump files created by Windows after system failures.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Windows memory dump and minidump files."], ["Current system restore points and user data."], "Review if you are troubleshooting blue screens.", [FileTarget(CombinePath(windows, "MEMORY.DMP")), DirectoryTarget(CombinePath(windows, "Minidump"), "*.dmp", recursive: false)]),
            UnsupportedCleanupItem("windowsUpdateCleanup", "Windows", "Windows Update Cleanup", "Old Windows update payloads that require Windows servicing APIs to remove safely.", "Advanced", "Use Windows Settings or DISM for component cleanup.", ["Superseded Windows update payloads."], ["Rollback and servicing state."]),
            CleanupItem("windowsUpgradeLogs", "Windows", "Windows Upgrade Logs", "Logs left behind after Windows setup or feature updates.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Windows setup and upgrade log files."], ["Rollback files and previous Windows installations."], "Review after upgrades are complete and working.", [DirectoryTarget(CombinePath(windows, "Panther")), DirectoryTarget(CombinePath(systemDrive, "$WINDOWS.~BT", "Sources", "Panther"))]),
            CleanupItem("deliveryOptimization", "Windows", "Delivery Optimization Files", "Peer download cache used by Windows Update delivery optimization.", "Safe", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Delivery Optimization cache files."], ["Installed updates and Windows components."], "Safe to clean; Windows may download needed content again.", [DirectoryTarget(CombinePath(programData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"))]),
            CleanupItem("directXShaderCache", "Windows", "DirectX Shader Cache", "Graphics shader cache rebuilt by games and graphics apps.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["DirectX and graphics shader cache files."], ["Games, saves, mods, and graphics drivers."], "Safe to clean; apps may rebuild shaders on next launch.", [DirectoryTarget(CombinePath(local, "D3DSCache")), DirectoryTarget(CombinePath(local, "NVIDIA", "DXCache")), DirectoryTarget(CombinePath(local, "NVIDIA", "GLCache"))]),
            CleanupItem("microsoftStoreCache", "Windows", "Microsoft Store Cache", "Local Microsoft Store cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Microsoft Store local cache files."], ["Installed Store apps and app data."], "Safe to clean when Store is closed.", [DirectoryTarget(CombinePath(local, "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe", "LocalCache"))]),
            CleanupItem("downloadedProgramFiles", "Windows", "Downloaded Program Files", "Legacy downloaded ActiveX and program files cache.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Legacy downloaded program cache files."], ["Modern app installs and personal downloads."], "Review on older systems that still use this cache.", [DirectoryTarget(CombinePath(windows, "Downloaded Program Files"))]),
            CleanupItem("windowsLogFiles", "Windows", "Windows Log Files", "Windows setup and servicing logs.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Windows log files in bounded log folders."], ["Event logs and live system state."], "Review if you are actively diagnosing Windows issues.", [DirectoryTarget(CombinePath(windows, "Logs"), "*.log"), DirectoryTarget(CombinePath(windows, "Temp"), "*.log")]),
            UnsupportedCleanupItem("dnsCache", "Windows", "DNS Cache", "Cached DNS entries maintained by Windows networking.", "Review", "This requires a flush command instead of file cleanup.", ["In-memory DNS resolver cache."], ["Network settings and saved Wi-Fi profiles."]),
            UnsupportedCleanupItem("clipboardHistory", "Privacy", "Clipboard History", "Windows clipboard history and synced clipboard data.", "Privacy", "Windows does not expose a safe file-only cleanup path here.", ["Clipboard history entries."], ["Current clipboard contents and files."]),
            CleanupItem("fontCache", "Windows", "Font Cache", "Windows font cache files rebuilt by the font cache service.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Font cache data files."], ["Installed fonts."], "Review because some files may be locked until services restart.", [DirectoryTarget(CombinePath(windows, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"), "FontCache*.dat", recursive: false)]),
            CleanupItem("prefetchFiles", "Windows", "Prefetch Files", "Application prefetch files used to speed up app launch.", "Review", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Windows prefetch cache files."], ["Applications and user files."], "Review because Windows rebuilds this performance cache over time.", [DirectoryTarget(CombinePath(windows, "Prefetch"), "*.pf", recursive: false)]),

            CleanupItem("browserCache", "Browsers", "Browser Cache", "Cached web content from supported browser profiles.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Web cache files from Edge, Chrome, Brave, Firefox, Opera, Vivaldi, and Chromium profiles."], ["Cookies, passwords, autofill, Local Storage, IndexedDB, and saved sessions."], "Safe to clean with browsers closed.", MergeTargets(TargetsForDirectories(chromiumProfiles, "Cache"), TargetsForDirectories(firefoxLocalProfiles, "cache2"))),
            CleanupItem("browserCodeCache", "Browsers", "Code Cache", "Compiled JavaScript cache from Chromium-based browsers.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Chromium code cache files."], ["Browser history, cookies, passwords, and site data."], "Safe to clean with browsers closed.", TargetsForDirectories(chromiumProfiles, "Code Cache")),
            CleanupItem("browserGpuCache", "Browsers", "GPU Cache", "Browser GPU process cache.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["GPU cache files from Chromium-based browsers."], ["Browser profiles and sign-in data."], "Safe to clean; browsers recreate it.", TargetsForDirectories(chromiumProfiles, "GPUCache")),
            CleanupItem("browserShaderCache", "Browsers", "Shader Cache", "Browser graphics shader cache.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Shader and graphics cache folders."], ["Browser profile settings and site data."], "Safe to clean; graphics shaders may rebuild.", MergeTargets(TargetsForDirectories(chromiumProfiles, "ShaderCache"), TargetsForDirectories(chromiumProfiles, "GrShaderCache"), TargetsForDirectories(chromiumProfiles, "DawnCache"))),
            CleanupItem("browserServiceWorkerCache", "Browsers", "Service Worker Cache", "Offline web app cache created by service workers.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Service worker cache storage."], ["Cookies, passwords, Local Storage, and IndexedDB."], "Review because some web apps may redownload offline assets.", TargetsForDirectories(chromiumProfiles, "Service Worker", "CacheStorage")),
            CleanupItem("browserFavicons", "Browsers", "Favicons", "Cached website icons shown in browser tabs and history.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Favicon cache databases."], ["Browsing history, cookies, and saved passwords."], "Safe to clean; icons reload as sites are visited.", MergeTargets(TargetsForFiles(chromiumProfiles, "Favicons"), TargetsForFiles(firefoxRoamingProfiles, "favicons.sqlite"))),
            CleanupItem("browserDownloadHistory", "Browsers", "Download History", "Download history stored in browser history databases.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Browser history databases that include download history."], ["Downloaded files on disk."], "Privacy cleanup. This may remove browser history records too.", MergeTargets(TargetsForFiles(chromiumProfiles, "History"), TargetsForFiles(firefoxRoamingProfiles, "places.sqlite"))),
            CleanupItem("browserHistory", "Browsers", "Browsing History", "Visited-site history stored by supported browsers.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Browser history databases."], ["Bookmarks, passwords, autofill, Local Storage, and IndexedDB."], "Privacy cleanup. Close browsers before cleaning.", MergeTargets(TargetsForFiles(chromiumProfiles, "History"), TargetsForFiles(firefoxRoamingProfiles, "places.sqlite"))),
            CleanupItem("browserCookies", "Browsers", "Cookies", "Browser cookies that keep you signed in to websites.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Cookie database files."], ["Saved passwords and autofill data."], "Privacy cleanup. This can sign you out of websites.", MergeTargets(TargetsForFiles(chromiumProfiles, "Network", "Cookies"), TargetsForFiles(chromiumProfiles, "Cookies"), TargetsForFiles(firefoxRoamingProfiles, "cookies.sqlite"))),
            UnsupportedCleanupItem("browserSitePermissions", "Browsers", "Site Permissions", "Per-site browser permissions such as camera, location, and notifications.", "Privacy", "Resetting this safely requires browser-specific database edits.", ["Site permission records."], ["Cookies, passwords, and browser preferences."]),
            UnsupportedCleanupItem("browserExtensionCache", "Browsers", "Extension Cache", "Temporary extension data from browser add-ons.", "Review", "Extension storage can include required extension state, so FileLocker leaves it untouched.", ["Extension cache where safely separable."], ["Extension code, settings, and user data."]),
            CleanupItem("browserCrashReports", "Browsers", "Crash Reports", "Browser crash reports and crashpad uploads.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Browser crash reports."], ["Browser settings and browsing data."], "Safe after you no longer need browser crash diagnostics.", MergeTargets(TargetsForDirectories(chromiumProfiles, "Crashpad", "reports"), TargetsForDirectories(firefoxRoamingProfiles, "crashes"))),

            CleanupItem("discordCache", "Applications", "Discord Cache", "Local Discord cache files. Close Discord for the most complete cleanup.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Discord cache, code cache, and GPU cache files."], ["Discord login data and settings."], "Safe to clean with Discord closed.", [DirectoryTarget(CombinePath(roaming, "discord", "Cache")), DirectoryTarget(CombinePath(roaming, "discord", "Code Cache")), DirectoryTarget(CombinePath(roaming, "discord", "GPUCache"))]),
            CleanupItem("teamsCache", "Applications", "Microsoft Teams Cache", "Local Teams cache files from classic and new Teams.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Teams cache files."], ["Teams accounts and chat data stored in the cloud."], "Safe to clean with Teams closed.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Teams", "Cache")), DirectoryTarget(CombinePath(local, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams"))]),
            CleanupItem("slackCache", "Applications", "Slack Cache", "Slack desktop cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Slack cache, code cache, and GPU cache files."], ["Slack workspace sign-in and messages."], "Safe to clean with Slack closed.", [DirectoryTarget(CombinePath(roaming, "Slack", "Cache")), DirectoryTarget(CombinePath(roaming, "Slack", "Code Cache")), DirectoryTarget(CombinePath(roaming, "Slack", "GPUCache"))]),
            CleanupItem("spotifyCache", "Applications", "Spotify Cache", "Spotify local cache and browser cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Spotify cache files."], ["Downloaded playlists and account settings where stored separately."], "Safe to clean with Spotify closed.", [DirectoryTarget(CombinePath(local, "Spotify", "Storage")), DirectoryTarget(CombinePath(local, "Spotify", "Browser", "Cache"))]),
            CleanupItem("zoomLogs", "Applications", "Zoom Logs", "Zoom diagnostic log files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Zoom log files."], ["Meetings, recordings, and account settings."], "Safe after you no longer need Zoom diagnostics.", [DirectoryTarget(CombinePath(roaming, "Zoom", "logs"))]),
            CleanupItem("obsLogs", "Applications", "OBS Logs", "OBS Studio log files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["OBS log files."], ["Scenes, profiles, recordings, and plugins."], "Safe after you no longer need OBS logs.", [DirectoryTarget(CombinePath(roaming, "obs-studio", "logs"))]),
            CleanupItem("adobeReaderCache", "Applications", "Adobe Reader Cache", "Adobe Reader local cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Adobe Reader cache files."], ["PDF documents and Adobe account data."], "Safe to clean with Adobe Reader closed.", [DirectoryTarget(CombinePath(localLow, "Adobe", "Acrobat", "DC", "Cache")), DirectoryTarget(CombinePath(local, "Adobe", "Acrobat", "DC", "Cache"))]),
            CleanupItem("officeRecent", "Applications", "Microsoft Office Recent Files", "Recent-document shortcuts shown by Microsoft Office.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Office recent document shortcuts."], ["Office documents themselves."], "Privacy cleanup. This does not delete documents.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Office", "Recent"))]),
            CleanupItem("onedriveLogs", "Applications", "OneDrive Logs/Temp Sync Files", "OneDrive diagnostic logs and bounded temp sync cache.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["OneDrive logs and temp sync cache files."], ["Cloud files, placeholders, and sync settings."], "Review if OneDrive is not actively troubleshooting sync issues.", [DirectoryTarget(CombinePath(local, "Microsoft", "OneDrive", "logs")), DirectoryTarget(CombinePath(local, "Microsoft", "OneDrive", "setup", "logs")), DirectoryTarget(CombinePath(local, "Microsoft", "OneDrive", "Temp"))]),
            CleanupItem("cloudflareWarpLogs", "Applications", "Cloudflare WARP Logs", "Cloudflare WARP diagnostic log files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Cloudflare WARP log files."], ["WARP settings and tunnel configuration."], "Safe after you no longer need WARP diagnostics.", [DirectoryTarget(CombinePath(programData, "Cloudflare", "warp", "logs")), DirectoryTarget(CombinePath(local, "Cloudflare", "Cloudflare WARP", "logs"))]),
            CleanupItem("nvidiaCache", "Applications", "NVIDIA App/GeForce Cache", "NVIDIA graphics and app cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["NVIDIA graphics cache files."], ["Drivers, game profiles, and screenshots."], "Safe to clean; shaders may rebuild.", [DirectoryTarget(CombinePath(local, "NVIDIA", "DXCache")), DirectoryTarget(CombinePath(local, "NVIDIA", "GLCache")), DirectoryTarget(CombinePath(local, "NVIDIA Corporation", "NV_Cache"))]),
            CleanupItem("amdCache", "Applications", "AMD Software Cache", "AMD graphics software cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["AMD graphics cache files."], ["Drivers and Radeon settings."], "Safe to clean; shaders may rebuild.", [DirectoryTarget(CombinePath(local, "AMD", "DxCache")), DirectoryTarget(CombinePath(local, "AMD", "DxcCache")), DirectoryTarget(CombinePath(local, "AMD", "GLCache"))]),
            CleanupItem("intelDsaCache", "Applications", "Intel Driver Support Assistant Cache", "Intel Driver & Support Assistant cache and logs.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Intel DSA cache and log files."], ["Installed drivers."], "Safe after scans or updates are complete.", [DirectoryTarget(CombinePath(programData, "Intel", "DSA", "Logs")), DirectoryTarget(CombinePath(local, "Intel", "Driver & Support Assistant"))]),
            CleanupItem("javaCache", "Applications", "Java Cache", "Java deployment cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Java deployment cache files."], ["Installed Java runtimes and applications."], "Safe to clean; Java apps may redownload cached content.", [DirectoryTarget(CombinePath(localLow, "Sun", "Java", "Deployment", "cache"))]),
            UnsupportedCleanupItem("archiveRecentHistory", "Applications", "7-Zip/WinRAR Recent History", "Recent archive history stored by compression tools.", "Privacy", "This is stored in application settings or registry values, not a safe file-only cache.", ["Recent archive history."], ["Archives and app settings."]),
            CleanupItem("notepadPlusPlusBackup", "Applications", "Notepad++ Backup/Session Files", "Notepad++ backup and session recovery files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Notepad++ backup and session files."], ["Saved documents and installed plugins."], "Review because this may remove unsaved recovery copies.", [DirectoryTarget(CombinePath(roaming, "Notepad++", "backup")), FileTarget(CombinePath(roaming, "Notepad++", "session.xml"))]),

            CleanupItem("steamDownloadCache", "Gaming", "Steam Download Cache", "Steam partial download cache in common install locations.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Steam downloading cache folders."], ["Installed games, saves, mods, screenshots, and configs."], "Review while Steam is closed; active downloads may need to restart.", [DirectoryTarget(CombinePath(programFilesX86, "Steam", "steamapps", "downloading")), DirectoryTarget(CombinePath(programFiles, "Steam", "steamapps", "downloading"))]),
            CleanupItem("steamShaderCache", "Gaming", "Steam Shader Cache", "Steam shader pre-cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Steam shader cache files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe to clean; Steam may rebuild shaders.", [DirectoryTarget(CombinePath(programFilesX86, "Steam", "steamapps", "shadercache")), DirectoryTarget(CombinePath(programFiles, "Steam", "steamapps", "shadercache"))]),
            CleanupItem("epicWebCache", "Gaming", "Epic Games Launcher Web Cache", "Epic Games Launcher web cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Epic launcher web cache files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe to clean with Epic Games Launcher closed.", [DirectoryTarget(CombinePath(local, "EpicGamesLauncher", "Saved", "webcache")), DirectoryTarget(CombinePath(local, "EpicGamesLauncher", "Saved", "webcache_4147"))]),
            CleanupItem("battleNetCache", "Gaming", "Battle.net Cache", "Battle.net launcher cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Battle.net cache files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe to clean with Battle.net closed.", [DirectoryTarget(CombinePath(programData, "Battle.net", "Cache")), DirectoryTarget(CombinePath(local, "Battle.net", "Cache"))]),
            CleanupItem("eaAppCache", "Gaming", "EA App Cache", "EA desktop app cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["EA app cache files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe to clean with EA App closed.", [DirectoryTarget(CombinePath(local, "Electronic Arts", "EA Desktop", "cache")), DirectoryTarget(CombinePath(local, "EADesktop", "cache"))]),
            CleanupItem("riotClientLogs", "Gaming", "Riot Client Logs", "Riot Client diagnostic log files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Riot Client log files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe after you no longer need Riot diagnostics.", [DirectoryTarget(CombinePath(local, "Riot Games", "Riot Client", "Logs"))]),
            CleanupItem("ubisoftConnectCache", "Gaming", "Ubisoft Connect Cache", "Ubisoft Connect launcher cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Ubisoft launcher cache files."], ["Installed games, saves, mods, screenshots, and configs."], "Safe to clean with Ubisoft Connect closed.", [DirectoryTarget(CombinePath(local, "Ubisoft Game Launcher", "cache")), DirectoryTarget(CombinePath(programData, "Ubisoft", "Ubisoft Game Launcher", "cache"))]),
            CleanupItem("minecraftLogs", "Gaming", "Minecraft Launcher Logs/Crash Reports", "Minecraft logs and crash reports.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Minecraft log and crash-report files."], ["Worlds, saves, resource packs, screenshots, and configs."], "Safe after you no longer need logs.", [DirectoryTarget(CombinePath(roaming, ".minecraft", "logs")), DirectoryTarget(CombinePath(roaming, ".minecraft", "crash-reports"))]),
            CleanupItem("robloxLogsCache", "Gaming", "Roblox Logs/Cache", "Roblox local logs and cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["Roblox logs and cache files."], ["Roblox Studio projects and user-created content."], "Safe to clean with Roblox closed.", [DirectoryTarget(CombinePath(local, "Roblox", "logs")), DirectoryTarget(CombinePath(local, "Roblox", "http")), DirectoryTarget(CombinePath(local, "Temp", "Roblox"))]),
            CleanupItem("curseForgeOverwolfCache", "Gaming", "CurseForge/Overwolf Cache", "CurseForge and Overwolf cache files.", "Safe", supportsDeletion: true, requiresAdministrator: false, defaultSelected: true, ["CurseForge and Overwolf cache files."], ["Mods, worlds, profiles, screenshots, and configs."], "Safe to clean with launchers closed.", [DirectoryTarget(CombinePath(local, "Overwolf", "Cache")), DirectoryTarget(CombinePath(roaming, "CurseForge", "Cache"))]),

            CleanupItem("vscodeCache", "Developer Tools", "VS Code Logs/Cache", "Visual Studio Code logs and cache files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["VS Code logs and cache files."], ["Projects, extensions, settings, and unsaved work."], "Review because VS Code may rebuild extension and workspace caches.", [DirectoryTarget(CombinePath(roaming, "Code", "logs")), DirectoryTarget(CombinePath(roaming, "Code", "Cache")), DirectoryTarget(CombinePath(roaming, "Code", "CachedData")), DirectoryTarget(CombinePath(roaming, "Code", "GPUCache"))]),
            CleanupItem("cursorCache", "Developer Tools", "Cursor Logs/Cache", "Cursor editor logs and cache files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Cursor logs and cache files."], ["Projects, extensions, settings, and unsaved work."], "Review because Cursor may rebuild extension and workspace caches.", [DirectoryTarget(CombinePath(roaming, "Cursor", "logs")), DirectoryTarget(CombinePath(roaming, "Cursor", "Cache")), DirectoryTarget(CombinePath(roaming, "Cursor", "CachedData")), DirectoryTarget(CombinePath(roaming, "Cursor", "GPUCache"))]),
            CleanupItem("visualStudioCache", "Developer Tools", "Visual Studio Logs/Component Cache", "Visual Studio logs and component model cache.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Visual Studio logs and component model cache folders."], ["Solutions, source code, workloads, and extensions."], "Review because Visual Studio may rebuild caches on next launch.", MergeTargets([DirectoryTarget(CombinePath(local, "Microsoft", "VisualStudio", "ComponentModelCache"))], TargetsForChildDirectories(CombinePath(local, "Microsoft", "VisualStudio"), "ComponentModelCache"), TargetsForChildDirectories(CombinePath(local, "Microsoft", "VisualStudio"), "Cache"))),
            CleanupItem("jetbrainsCache", "Developer Tools", "JetBrains Logs/Cache", "JetBrains IDE logs and cache files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["JetBrains IDE logs and cache files."], ["Projects, settings, plugins, and indexes outside cache folders."], "Review because IDEs may rebuild indexes and caches.", MergeTargets(TargetsForChildDirectories(CombinePath(local, "JetBrains"), "log"), TargetsForChildDirectories(CombinePath(local, "JetBrains"), "caches"), TargetsForChildDirectories(CombinePath(roaming, "JetBrains"), "log"))),
            CleanupItem("npmCache", "Developer Tools", "npm Cache", "npm package manager cache.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["npm package cache files."], ["Projects and package-lock files."], "Review because dependencies may need to download again.", [DirectoryTarget(CombinePath(local, "npm-cache"))]),
            CleanupItem("yarnCache", "Developer Tools", "Yarn Cache", "Yarn package manager cache.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Yarn package cache files."], ["Projects and lockfiles."], "Review because dependencies may need to download again.", [DirectoryTarget(CombinePath(local, "Yarn", "Cache")), DirectoryTarget(CombinePath(userProfile, ".cache", "yarn"))]),
            CleanupItem("pipCache", "Developer Tools", "pip Cache", "Python pip package cache.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["pip package cache files."], ["Python environments and projects."], "Review because packages may need to download again.", [DirectoryTarget(CombinePath(local, "pip", "Cache")), DirectoryTarget(CombinePath(userProfile, ".cache", "pip"))]),
            CleanupItem("gradleCache", "Developer Tools", "Gradle Build Cache", "Gradle build cache files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Gradle build cache folders."], ["Gradle projects and wrapper files."], "Review because builds may take longer next time.", [DirectoryTarget(CombinePath(userProfile, ".gradle", "caches", "build-cache-1")), DirectoryTarget(CombinePath(userProfile, ".gradle", "caches", "modules-2", "files-2.1"))]),
            CleanupItem("nugetCache", "Developer Tools", "NuGet Cache", "NuGet HTTP and plugin cache files.", "Review", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["NuGet HTTP and plugin cache files."], ["Project files and global packages."], "Review because packages may need to download again.", [DirectoryTarget(CombinePath(local, "NuGet", "v3-cache")), DirectoryTarget(CombinePath(local, "NuGet", "plugins-cache")), DirectoryTarget(CombinePath(local, "NuGet", "Scratch"))]),
            UnsupportedCleanupItem("dockerBuildCache", "Developer Tools", "Docker Build Cache/Dangling Images", "Docker build cache and dangling images.", "Advanced", "Docker cleanup requires Docker Engine commands, not file deletion.", ["Docker build cache and dangling images."], ["Images, volumes, containers, and bind-mounted data."]),

            CleanupItem("recentFiles", "Privacy", "Recent Files", "Recently opened file shortcuts shown by Windows.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Recent file shortcut entries."], ["Original files and folders."], "Privacy cleanup. This does not delete your documents.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Windows", "Recent"), "*.lnk", recursive: false)]),
            CleanupItem("quickAccessHistory", "Privacy", "Quick Access History", "Quick Access and File Explorer automatic destination history.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Quick Access automatic and custom destination files."], ["Pinned items and original files where stored separately."], "Privacy cleanup. Recent suggestions may be rebuilt.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Windows", "Recent", "AutomaticDestinations")), DirectoryTarget(CombinePath(roaming, "Microsoft", "Windows", "Recent", "CustomDestinations"))]),
            CleanupItem("jumpLists", "Privacy", "Jump Lists", "Windows taskbar and Start menu jump list history.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Jump list destination files."], ["Applications and original files."], "Privacy cleanup. Jump lists may repopulate as apps are used.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Windows", "Recent", "AutomaticDestinations")), DirectoryTarget(CombinePath(roaming, "Microsoft", "Windows", "Recent", "CustomDestinations"))]),
            UnsupportedCleanupItem("runDialogHistory", "Privacy", "Run Dialog History", "Commands typed into the Windows Run dialog.", "Privacy", "Run dialog history is stored in registry values and is not cleaned by the file-only engine.", ["Run dialog command history."], ["Programs and files."]),
            UnsupportedCleanupItem("windowsSearchHistory", "Privacy", "Windows Search History", "Search terms and search UI history.", "Privacy", "Windows Search history is not exposed as a safe file-only cleanup target.", ["Windows search history entries."], ["Search index and files."]),
            UnsupportedCleanupItem("explorerAddressHistory", "Privacy", "Explorer Address Bar History", "Paths typed into File Explorer address bars.", "Privacy", "Explorer address history is stored in registry values and is not cleaned by the file-only engine.", ["Explorer address bar history."], ["Folders and pinned locations."]),
            UnsupportedCleanupItem("networkShareHistory", "Privacy", "Network Share History", "Recently used network share history.", "Privacy", "Network share history is registry-backed and is not cleaned by the file-only engine.", ["Network share history."], ["Mapped drives and credentials."]),
            CleanupItem("remoteDesktopCache", "Privacy", "Remote Desktop Cache", "Remote Desktop bitmap and connection cache files.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Remote Desktop cache files."], ["Saved RDP files and credentials."], "Privacy cleanup. Close Remote Desktop clients first.", [DirectoryTarget(CombinePath(local, "Microsoft", "Terminal Server Client", "Cache")), DirectoryTarget(CombinePath(local, "Microsoft", "Terminal Server Client", "Cache2"))]),
            UnsupportedCleanupItem("mediaPlayerHistory", "Privacy", "Media Player History", "Media playback history from Windows media apps.", "Privacy", "Media history is app-specific and not safely available as a file-only cache.", ["Media playback history."], ["Media files and libraries."]),
            CleanupItem("officeRecentDocuments", "Privacy", "Office Recent Documents", "Recent document shortcuts shown by Microsoft Office.", "Privacy", supportsDeletion: true, requiresAdministrator: false, defaultSelected: false, ["Office recent document shortcuts."], ["Office documents themselves."], "Privacy cleanup. This does not delete documents.", [DirectoryTarget(CombinePath(roaming, "Microsoft", "Office", "Recent"))]),

            UnsupportedCleanupItem("windowsOld", "Advanced", "Windows.old / Previous Windows Installation", "Previous Windows installation files used for rollback.", "Advanced", "Use Windows Storage Settings for rollback-aware removal.", ["Previous Windows installation files."], ["Current Windows installation."]),
            UnsupportedCleanupItem("oldRestorePoints", "Advanced", "Old System Restore Points", "Older restore points maintained by System Protection.", "Advanced", "Restore points require Windows system APIs, not file deletion.", ["Old restore point data."], ["Newest restore point and current system state."]),
            UnsupportedCleanupItem("shadowCopies", "Advanced", "Shadow Copies", "Volume shadow copies used for restore and backup.", "Risky", "Shadow copies require explicit VSS commands and are not cleaned by FileLocker.", ["Volume shadow copies."], ["Current files and backups outside VSS."]),
            UnsupportedCleanupItem("hibernationFile", "Advanced", "Hibernation File", "The hiberfil.sys file used by Windows hibernation and Fast Startup.", "Advanced", "This requires power configuration changes rather than file deletion.", ["Hibernation file."], ["Sleep settings unless changed separately."]),
            UnsupportedCleanupItem("componentCleanup", "Advanced", "Windows Component Cleanup", "Windows component store cleanup.", "Advanced", "Use DISM or Windows Settings for servicing-safe cleanup.", ["Superseded component store payloads."], ["Current Windows components."]),
            CleanupItem("softwareDistributionDownload", "Advanced", "SoftwareDistribution Download Cache", "Windows Update download cache.", "Advanced", supportsDeletion: true, requiresAdministrator: true, defaultSelected: false, ["Windows Update downloaded payload cache."], ["Installed updates and component store state."], "Advanced. Use only when Windows Update is not actively running.", [DirectoryTarget(CombinePath(windows, "SoftwareDistribution", "Download"))]),
            UnsupportedCleanupItem("driverStoreOldPackages", "Advanced", "DriverStore Old Packages", "Older driver packages in the Windows Driver Store.", "Risky", "DriverStore cleanup requires driver inventory APIs, not raw file deletion.", ["Old driver packages."], ["Current drivers and rollback state."]),
            UnsupportedCleanupItem("installerOrphanCache", "Advanced", "Installer Orphan Cache", "Potential orphaned Windows Installer cache entries.", "Risky", "Windows Installer cache should not be cleaned by path guesses.", ["Confirmed orphaned installer cache files."], ["Installer repair and uninstall data."]),
            UnsupportedCleanupItem("eventLogs", "Advanced", "Event Logs", "Windows Event Log files.", "Risky", "Event logs require event log APIs and are not deleted as files.", ["Event log records."], ["Current logging configuration."]),
            UnsupportedCleanupItem("browserSavedPasswords", "Advanced", "Browser Saved Passwords", "Saved browser passwords.", "Risky", "FileLocker never deletes saved passwords by default.", ["Saved browser password stores."], ["Browser profiles and other site data."]),
            UnsupportedCleanupItem("browserAutofill", "Advanced", "Browser Autofill Data", "Browser autofill profiles and form data.", "Risky", "Autofill data is intentionally left untouched.", ["Autofill databases."], ["Passwords, cookies, and bookmarks."]),
            UnsupportedCleanupItem("cloudSyncPlaceholders", "Advanced", "Cloud Sync Placeholders", "Cloud storage placeholder files.", "Risky", "Placeholder cleanup must be handled by the sync provider.", ["Cloud sync placeholders."], ["Cloud files and sync state."])
        ];
    }

    private static CleanupDefinition CleanupItem(
        string id,
        string group,
        string label,
        string description,
        string safetyLevel,
        bool supportsDeletion,
        bool requiresAdministrator,
        bool defaultSelected,
        string[] removes,
        string[] keeps,
        string recommendation,
        CleanupTarget[] targets,
        string? unavailableReason = null)
    {
        CleanupTarget[] normalizedTargets = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .GroupBy(target => $"{target.Path}|{target.SearchPattern}|{target.Recursive}|{target.IsFile}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return new CleanupDefinition(
            id,
            group,
            label,
            description,
            safetyLevel,
            normalizedTargets,
            supportsDeletion,
            requiresAdministrator,
            defaultSelected,
            removes,
            keeps,
            recommendation,
            unavailableReason);
    }

    private static CleanupDefinition UnsupportedCleanupItem(
        string id,
        string group,
        string label,
        string description,
        string safetyLevel,
        string unavailableReason,
        string[] removes,
        string[] keeps)
    {
        return CleanupItem(
            id,
            group,
            label,
            description,
            safetyLevel,
            supportsDeletion: false,
            requiresAdministrator: false,
            defaultSelected: false,
            removes,
            keeps,
            recommendation: unavailableReason,
            targets: [],
            unavailableReason);
    }

    private static CleanupCategory ScanCleanupCategory(CleanupDefinition definition)
    {
        if (definition.Id == "recycleBin")
        {
            return ScanRecycleBin(definition);
        }

        string primaryPath = definition.Targets.FirstOrDefault()?.Path ?? string.Empty;
        string[] locations = FormatCleanupLocations(definition);
        if (!definition.SupportsDeletion)
        {
            return CreateCleanupCategory(
                definition,
                primaryPath,
                sizeBytes: 0,
                sizeDisplay: "Unknown size",
                fileCount: 0,
                skippedCount: 0,
                isEnabled: false,
                sizeKnown: false,
                status: "Unavailable",
                warnings: [],
                locations,
                unavailableReason: definition.UnavailableReason ?? "This cleaner is not supported by the current file-only cleanup engine.");
        }

        DirectoryScanSummary summary = ScanCleanupTargets(definition);
        if (!summary.LocationFound)
        {
            return CreateCleanupCategory(
                definition,
                primaryPath,
                sizeBytes: 0,
                sizeDisplay: "Unknown size",
                fileCount: 0,
                skippedCount: summary.SkippedCount,
                isEnabled: false,
                sizeKnown: false,
                status: "Not found",
                warnings: summary.Warnings,
                locations,
                unavailableReason: "No matching cleanup locations were found.");
        }

        return CreateCleanupCategory(
            definition,
            primaryPath,
            summary.SizeBytes,
            FormatFileSize(summary.SizeBytes),
            summary.FileCount,
            summary.SkippedCount,
            isEnabled: summary.FileCount > 0,
            sizeKnown: true,
            status: summary.FileCount > 0 ? "Ready to clean" : "Already clean",
            summary.Warnings,
            locations,
            unavailableReason: string.Empty);
    }

    private static CleanupCategory CreateCleanupCategory(
        CleanupDefinition definition,
        string path,
        long sizeBytes,
        string sizeDisplay,
        int fileCount,
        int skippedCount,
        bool isEnabled,
        bool sizeKnown,
        string status,
        string[] warnings,
        string[] locations,
        string unavailableReason)
    {
        return new CleanupCategory(
            definition.Id,
            definition.Group,
            definition.Label,
            definition.Description,
            path,
            sizeBytes,
            sizeDisplay,
            fileCount,
            skippedCount,
            isEnabled,
            definition.RequiresAdministrator,
            definition.DefaultSelected,
            status,
            warnings,
            definition.SafetyLevel,
            sizeKnown,
            locations,
            definition.Removes,
            definition.Keeps,
            definition.Recommendation,
            unavailableReason);
    }

    private static DirectoryScanSummary ScanCleanupTargets(CleanupDefinition definition)
    {
        List<string> warnings = [];
        IReadOnlyList<string> files = EnumerateCleanupFiles(definition, warnings, out int skippedCount, out bool locationFound);
        long sizeBytes = 0;
        int fileCount = 0;

        foreach (string file in files)
        {
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

        return new DirectoryScanSummary(sizeBytes, fileCount, skippedCount, warnings.ToArray(), locationFound);
    }

    private static IReadOnlyList<string> EnumerateCleanupFiles(CleanupDefinition definition, List<string> warnings, out int skippedCount, out bool locationFound)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        skippedCount = 0;
        locationFound = false;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        foreach (CleanupTarget target in definition.Targets)
        {
            try
            {
                string targetPath = Path.GetFullPath(target.Path);
                if (target.IsFile)
                {
                    if (File.Exists(targetPath) && !IsReparsePoint(targetPath))
                    {
                        locationFound = true;
                        files.Add(targetPath);
                    }

                    continue;
                }

                if (!Directory.Exists(targetPath) || IsReparsePoint(targetPath))
                {
                    continue;
                }

                locationFound = true;
                options.RecurseSubdirectories = target.Recursive;
                foreach (string file in Directory.EnumerateFiles(targetPath, target.SearchPattern, options))
                {
                    if (files.Count >= MaxCleanupScanFiles)
                    {
                        AddCleanupWarning(warnings, $"Scan stopped after {MaxCleanupScanFiles.ToString(CultureInfo.InvariantCulture)} files.");
                        return files.ToArray();
                    }

                    files.Add(file);
                }
            }
            catch (Exception ex)
            {
                skippedCount++;
                AddCleanupWarning(warnings, $"{target.Path}: {ex.Message}");
            }
        }

        return files.ToArray();
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
            return CreateCleanupCategory(
                definition,
                string.Empty,
                sizeBytes: 0,
                sizeDisplay: "Unknown size",
                fileCount: 0,
                skippedCount: 1,
                isEnabled: false,
                sizeKnown: false,
                status: "Unable to query",
                warnings: [$"Recycle Bin query failed with HRESULT 0x{result:X8}."],
                locations: ["Recycle Bin"],
                unavailableReason: "Recycle Bin could not be queried.");
        }

        return CreateCleanupCategory(
            definition,
            string.Empty,
            info.i64Size,
            FormatFileSize(info.i64Size),
            (int)Math.Min(info.i64NumItems, int.MaxValue),
            skippedCount: 0,
            isEnabled: info.i64NumItems > 0,
            sizeKnown: true,
            status: info.i64NumItems > 0 ? "Ready to clean" : "Already clean",
            warnings: [],
            locations: ["Recycle Bin"],
            unavailableReason: string.Empty);
    }

    private static CleanupDeleteSummary DeleteDirectoryContents(CleanupDefinition definition)
    {
        if (!definition.SupportsDeletion)
        {
            return new CleanupDeleteSummary(0, 0, 1, [$"{definition.Label} is not supported by the current cleanup engine."]);
        }

        long freedBytes = 0;
        int deletedFiles = 0;
        int skippedItems = 0;
        List<string> warnings = [];
        IReadOnlyList<string> files = EnumerateCleanupFiles(definition, warnings, out int scanSkipped, out bool locationFound);
        skippedItems += scanSkipped;

        if (!locationFound)
        {
            return new CleanupDeleteSummary(0, 0, skippedItems, warnings.ToArray());
        }

        foreach (string file in files)
        {
            try
            {
                if (!IsApprovedCleanupPath(definition.Id, file))
                {
                    skippedItems++;
                    AddCleanupWarning(warnings, $"{file}: not an approved cleanup path.");
                    continue;
                }

                var info = new FileInfo(file);
                long size = info.Exists ? info.Length : 0;
                FileCleanupService.ClearReadOnlyAttribute(file);
                File.Delete(file);
                freedBytes += size;
                deletedFiles++;
            }
            catch (Exception ex)
            {
                skippedItems++;
                AddCleanupWarning(warnings, $"{file}: {ex.Message}");
            }
        }

        DeleteEmptyCleanupDirectories(definition, warnings, ref skippedItems);
        return new CleanupDeleteSummary(freedBytes, deletedFiles, skippedItems, warnings.ToArray());
    }

    private static void DeleteEmptyCleanupDirectories(CleanupDefinition definition, List<string> warnings, ref int skippedItems)
    {
        foreach (CleanupTarget target in definition.Targets.Where(target => !target.IsFile && target.Recursive && string.Equals(target.SearchPattern, "*", StringComparison.Ordinal)))
        {
            try
            {
                string root = Path.GetFullPath(target.Path);
                if (!Directory.Exists(root) || IsReparsePoint(root))
                {
                    continue;
                }

                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                var directories = new List<string>();
                foreach (string directory in Directory.EnumerateDirectories(root, "*", options))
                {
                    if (directories.Count >= MaxCleanupScanFiles)
                    {
                        skippedItems++;
                        AddCleanupWarning(warnings, $"Empty-directory cleanup stopped after {MaxCleanupScanFiles.ToString(CultureInfo.InvariantCulture)} folders.");
                        break;
                    }

                    directories.Add(directory);
                }

                foreach (string directory in directories.OrderByDescending(path => path.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory, recursive: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        skippedItems++;
                        AddCleanupWarning(warnings, $"{directory}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                skippedItems++;
                AddCleanupWarning(warnings, $"{target.Path}: {ex.Message}");
            }
        }
    }

    private static CleanupTarget DirectoryTarget(string path, string searchPattern = "*", bool recursive = true)
    {
        return new CleanupTarget(path, string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern, recursive, IsFile: false);
    }

    private static CleanupTarget FileTarget(string path)
    {
        return new CleanupTarget(path, "*", Recursive: false, IsFile: true);
    }

    private static CleanupTarget[] MergeTargets(params CleanupTarget[][] targetGroups)
    {
        return targetGroups
            .SelectMany(group => group)
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .GroupBy(target => $"{target.Path}|{target.SearchPattern}|{target.Recursive}|{target.IsFile}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static CleanupTarget[] TargetsForDirectories(IEnumerable<string> roots, params string[] relativeParts)
    {
        return roots
            .Select(root => DirectoryTarget(CombinePath(root, relativeParts)))
            .ToArray();
    }

    private static CleanupTarget[] TargetsForFiles(IEnumerable<string> roots, params string[] relativeParts)
    {
        return roots
            .Select(root => FileTarget(CombinePath(root, relativeParts)))
            .ToArray();
    }

    private static CleanupTarget[] TargetsForChildDirectories(string root, string childDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var targets = new List<CleanupTarget>();
        foreach (string child in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (targets.Count >= MaxCleanupChildDirectories)
            {
                break;
            }

            string target = CombinePath(child, childDirectoryName);
            if (Directory.Exists(target))
            {
                targets.Add(DirectoryTarget(target));
            }
        }

        return targets.ToArray();
    }

    private static string[] GetChromiumProfileDirectories(string local, string roaming)
    {
        string[] userDataRoots =
        [
            CombinePath(local, "Microsoft", "Edge", "User Data"),
            CombinePath(local, "Google", "Chrome", "User Data"),
            CombinePath(local, "BraveSoftware", "Brave-Browser", "User Data"),
            CombinePath(local, "Chromium", "User Data"),
            CombinePath(local, "Vivaldi", "User Data")
        ];
        string[] operaProfiles =
        [
            CombinePath(roaming, "Opera Software", "Opera Stable"),
            CombinePath(roaming, "Opera Software", "Opera GX Stable")
        ];

        var profiles = new List<string>();
        foreach (string root in userDataRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Directory.Exists(root))
            {
                profiles.Add(CombinePath(root, "Default"));
                continue;
            }

            var profileDirectories = new List<string>();
            foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (profileDirectories.Count >= MaxCleanupChildDirectories)
                {
                    break;
                }

                string name = Path.GetFileName(path);
                if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                {
                    profileDirectories.Add(path);
                }
            }

            profiles.AddRange(profileDirectories.Count > 0 ? profileDirectories : [CombinePath(root, "Default")]);
        }

        profiles.AddRange(operaProfiles.Where(path => !string.IsNullOrWhiteSpace(path)));
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] GetFirefoxProfileDirectories(string root)
    {
        string profilesRoot = CombinePath(root, "Mozilla", "Firefox", "Profiles");
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
        {
            return [CombinePath(profilesRoot, "default-release")];
        }

        return Directory.EnumerateDirectories(profilesRoot, "*", SearchOption.TopDirectoryOnly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCleanupChildDirectories)
            .ToArray();
    }

    private static string[] FormatCleanupLocations(CleanupDefinition definition)
    {
        if (definition.Targets.Length == 0)
        {
            return [];
        }

        return definition.Targets
            .Select(target =>
            {
                if (target.IsFile || string.Equals(target.SearchPattern, "*", StringComparison.Ordinal))
                {
                    return target.Path;
                }

                return CombinePath(target.Path, target.SearchPattern);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CombinePath(string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        string path = root;
        foreach (string part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                path = Path.Combine(path, part);
            }
        }

        return path;
    }

    private static string GetLocalLowPath(string userProfile)
    {
        return string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "AppData", "LocalLow");
    }

    private static string GetSystemDriveRoot(string windowsPath)
    {
        string? root = string.IsNullOrWhiteSpace(windowsPath)
            ? Path.GetPathRoot(Environment.SystemDirectory)
            : Path.GetPathRoot(windowsPath);
        return string.IsNullOrWhiteSpace(root) ? string.Empty : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static void AddCleanupWarning(List<string> warnings, string message)
    {
        string warning = NormalizeWarningMessage(message);
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        if (warnings.Count < MaxCleanupOperationWarnings)
        {
            warnings.Add(warning);
            return;
        }

        if (warnings.Count == MaxCleanupOperationWarnings)
        {
            warnings.Add("Additional cleanup items were skipped.");
        }
    }

    internal static string NormalizeWarningMessage(string? message)
    {
        return SensitiveDataRedactor.RedactMessage(message, MaxWarningMessageChars, " Warning truncated.");
    }

    private static string[] NormalizeWarnings(IEnumerable<string> warnings, int maxWarnings, string overflowWarning)
    {
        string[] normalized = warnings
            .Select(NormalizeWarningMessage)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length <= maxWarnings)
        {
            return normalized;
        }

        return normalized.Take(maxWarnings).Concat([overflowWarning]).ToArray();
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

    private static CleanupDeleteSummary EmptyRecycleBin(CleanupCategory before)
    {
        CleanupDeleteSummary secureDeleted = SecureDeleteRecycleBinContents();
        uint result = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        var warnings = new List<string>(secureDeleted.Warnings);
        int skippedItems = secureDeleted.SkippedItems;
        if (result != 0)
        {
            skippedItems++;
            AddCleanupWarning(warnings, $"Recycle Bin cleanup failed with HRESULT 0x{result:X8}.");
            return new CleanupDeleteSummary(secureDeleted.FreedBytes, secureDeleted.DeletedFiles, skippedItems, warnings.ToArray());
        }

        return new CleanupDeleteSummary(
            Math.Max(before.sizeBytes, secureDeleted.FreedBytes),
            Math.Max(before.fileCount, secureDeleted.DeletedFiles),
            skippedItems,
            warnings.ToArray());
    }

    private static CleanupDeleteSummary SecureDeleteRecycleBinContents()
    {
        long freedBytes = 0;
        int deletedFiles = 0;
        int skippedItems = 0;
        List<string> warnings = [];

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                string recycleBinRoot = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(recycleBinRoot) || IsReparsePoint(recycleBinRoot))
                {
                    continue;
                }

                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                foreach (string file in Directory.EnumerateFiles(recycleBinRoot, "*", options))
                {
                    if (string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (deletedFiles + skippedItems >= MaxCleanupScanFiles)
                    {
                        skippedItems++;
                        AddCleanupWarning(warnings, $"Recycle Bin secure delete stopped after {MaxCleanupScanFiles.ToString(CultureInfo.InvariantCulture)} files.");
                        break;
                    }

                    try
                    {
                        freedBytes += SecureDeleteCleanupFile(file, passes: 3);
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        skippedItems++;
                        AddCleanupWarning(warnings, $"{file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                skippedItems++;
                AddCleanupWarning(warnings, $"{drive.Name}$Recycle.Bin: {ex.Message}");
            }
        }

        return new CleanupDeleteSummary(freedBytes, deletedFiles, skippedItems, warnings.ToArray());
    }

    private static long SecureDeleteCleanupFile(string filePath, int passes)
    {
        FileInfo info = new(filePath);
        if (!info.Exists)
        {
            return 0;
        }

        FileAttributes originalAttributes = info.Attributes;
        if ((originalAttributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
        {
            throw new IOException("Secure delete does not overwrite file reparse points.");
        }

        long fileSize = info.Length;
        try
        {
            FileCleanupService.ClearReadOnlyAttribute(filePath);
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                try
                {
                    int normalizedPasses = Math.Clamp(passes, 1, 35);
                    for (int pass = 0; pass < normalizedPasses; pass++)
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        long written = 0;
                        while (written < fileSize)
                        {
                            int toWrite = (int)Math.Min(buffer.Length, fileSize - written);
                            FillSecureDeleteBuffer(buffer.AsSpan(0, toWrite), pass);
                            stream.Write(buffer, 0, toWrite);
                            written += toWrite;
                        }

                        stream.Flush(flushToDisk: true);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }

            File.Delete(filePath);
            return fileSize;
        }
        catch
        {
            RestoreFileAttributesBestEffort(filePath, originalAttributes);
            throw;
        }
    }

    private static void FillSecureDeleteBuffer(Span<byte> buffer, int pass)
    {
        if (pass == 0)
        {
            buffer.Clear();
            return;
        }

        if (pass == 1)
        {
            buffer.Fill(0xFF);
            return;
        }

        RandomNumberGenerator.Fill(buffer);
    }

    private static void RestoreFileAttributesBestEffort(string filePath, FileAttributes attributes)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, attributes);
            }
        }
        catch
        {
            // Best effort after a cleanup failure; the original exception is more useful to callers.
        }
    }

    private static bool IsApprovedCleanupPath(string categoryId, string path)
    {
        string normalized = Path.GetFullPath(path);
        CleanupDefinition? definition = GetCleanupDefinitions()
            .FirstOrDefault(item => string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            return false;
        }

        foreach (CleanupTarget target in definition.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Path))
            {
                continue;
            }

            string expected = Path.GetFullPath(target.Path);
            if (target.IsFile)
            {
                if (string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            string normalizedRoot = expected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string NormalizeDriveRoot(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new InvalidOperationException("Select a drive first.");
        }

        string normalizedDriveRoot = driveRoot.Trim();
        if (normalizedDriveRoot.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            throw new InvalidOperationException("The selected drive is invalid.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(normalizedDriveRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("The selected drive is invalid.", ex);
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The selected drive is invalid.");
        }

        string normalizedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(normalizedFullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected drive is invalid.");
        }

        string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
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

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {fileName}.");
        }

        Task<BoundedTextReadResult> outputTask = BoundedTextReader.ReadToEndAsync(process.StandardOutput, MaxMaintenanceToolOutputChars);
        Task<BoundedTextReadResult> errorTask = BoundedTextReader.ReadToEndAsync(process.StandardError, MaxMaintenanceToolOutputChars);

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

        BoundedTextReadResult stdout = await outputTask;
        BoundedTextReadResult stderr = await errorTask;
        string output = string.Join(
            Environment.NewLine,
            new[] { stdout.Text, stderr.Text }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (stdout.Truncated || stderr.Truncated)
        {
            output = string.IsNullOrWhiteSpace(output)
                ? "Output truncated."
                : $"{output}{Environment.NewLine}Output truncated.";
        }

        return new ProcessRunResult(process.ExitCode, NormalizeMaintenanceToolOutput(output), startedAtUtc, DateTime.UtcNow);
    }

    internal static string NormalizeMaintenanceToolOutput(string? output)
    {
        return SensitiveDataRedactor.RedactMessage(output, MaxMaintenanceToolOutputChars, " Output truncated.");
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
                    "Invalid File or Folder",
                    displayName,
                    targetPath,
                    "Startup entry points to a missing file.",
                    valueName,
                    subKeyName: null,
                    severity: "High",
                    category: "Startup"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
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
                    if (subKey == null || RegistryIntValue(subKey, "SystemComponent") == 1)
                    {
                        continue;
                    }

                    string displayName = RegistryStringValue(subKey, "DisplayName");
                    string uninstallCommand = RegistryStringValue(subKey, "UninstallString", doNotExpandEnvironmentNames: true);
                    string quietUninstallCommand = RegistryStringValue(subKey, "QuietUninstallString", doNotExpandEnvironmentNames: true);
                    string displayIcon = RegistryStringValue(subKey, "DisplayIcon", doNotExpandEnvironmentNames: true);
                    string? uninstallTarget = ExtractExecutablePath(uninstallCommand);
                    string? quietUninstallTarget = ExtractExecutablePath(quietUninstallCommand);
                    string? displayTarget = ExtractExecutablePath(displayIcon);
                    string? installLocation = RegistryPathNormalizer.Normalize(RegistryStringValue(subKey, "InstallLocation", doNotExpandEnvironmentNames: true));
                    string friendlyName = string.IsNullOrWhiteSpace(displayName) ? subKeyName : displayName;

                    if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uninstallCommand) && string.IsNullOrWhiteSpace(quietUninstallCommand))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Orphaned Registry Key",
                            friendlyName,
                            "Missing display name and uninstall command",
                            "Uninstall registry key has no visible application metadata and no uninstall command.",
                            valueName: null,
                            subKeyName,
                            severity: "Low",
                            category: "Uninstall"));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uninstallCommand) && string.IsNullOrWhiteSpace(quietUninstallCommand) &&
                        string.IsNullOrWhiteSpace(displayIcon) && string.IsNullOrWhiteSpace(installLocation))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Invalid Uninstall Entry",
                            friendlyName,
                            "Missing uninstall string",
                            "Uninstall entry is incomplete and cannot launch a visible uninstaller.",
                            valueName: null,
                            subKeyName,
                            severity: "High",
                            category: "Uninstall"));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(uninstallTarget) && IsMissingPath(uninstallTarget))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Invalid Uninstall Entry",
                            friendlyName,
                            uninstallTarget,
                            "Uninstall command points to a missing application file.",
                            valueName: null,
                            subKeyName,
                            severity: "High",
                            category: "Uninstall"));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(quietUninstallTarget) && IsMissingPath(quietUninstallTarget))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Invalid Uninstall Entry",
                            friendlyName,
                            quietUninstallTarget,
                            "Quiet uninstall command points to a missing application file.",
                            valueName: null,
                            subKeyName,
                            severity: "High",
                            category: "Uninstall"));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(displayTarget) && IsMissingPath(displayTarget) &&
                        (string.IsNullOrWhiteSpace(installLocation) || IsMissingPath(installLocation)))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Invalid File or Folder",
                            friendlyName,
                            displayTarget,
                            "Uninstall entry references a missing display icon or application folder.",
                            valueName: null,
                            subKeyName,
                            severity: "High",
                            category: "Uninstall"));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{hive}\\{keyPath}\\{subKeyName}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
        }
    }

    private static void ScanApplicationPathEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
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
                    if (subKey == null)
                    {
                        continue;
                    }

                    string defaultCommand = RegistryStringValue(subKey, string.Empty, doNotExpandEnvironmentNames: true);
                    string? defaultTarget = ExtractExecutablePath(defaultCommand);
                    if (!string.IsNullOrWhiteSpace(defaultTarget) && IsMissingPath(defaultTarget))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            keyPath,
                            "Invalid Application Path",
                            subKeyName,
                            defaultTarget,
                            "Application Paths entry points to a missing executable.",
                            valueName: null,
                            subKeyName,
                            severity: "Medium",
                            category: "Application Paths"));
                        continue;
                    }

                    string? pathValue = RegistryPathNormalizer.Normalize(RegistryStringValue(subKey, "Path", doNotExpandEnvironmentNames: true));
                    if (!string.IsNullOrWhiteSpace(pathValue) && IsMissingPath(pathValue))
                    {
                        issues.Add(CreateRegistryIssue(
                            hive,
                            $@"{keyPath}\{subKeyName}",
                            "Invalid Application Path",
                            subKeyName,
                            pathValue,
                            "Application Paths search directory does not exist.",
                            valueName: "Path",
                            subKeyName: null,
                            severity: "Medium",
                            category: "Application Paths"));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{hive}\\{keyPath}\\{subKeyName}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
        }
    }

    private static void ScanComServerEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
    {
        try
        {
            using RegistryKey? clsidKey = root.OpenSubKey(keyPath, writable: false);
            if (clsidKey == null)
            {
                return;
            }

            foreach (string classId in clsidKey.GetSubKeyNames().Take(MaxRegistryClassKeys))
            {
                try
                {
                    using RegistryKey? classKey = clsidKey.OpenSubKey(classId, writable: false);
                    if (classKey == null)
                    {
                        continue;
                    }

                    string displayName = RegistryStringValue(classKey, string.Empty);
                    string friendlyName = string.IsNullOrWhiteSpace(displayName) ? classId : displayName;
                    ScanComServerSubKey(root, hive, keyPath, classId, friendlyName, "InprocServer32", issues);
                    ScanComServerSubKey(root, hive, keyPath, classId, friendlyName, "LocalServer32", issues);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{hive}\\{keyPath}\\{classId}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
        }
    }

    private static void ScanComServerSubKey(
        RegistryKey root,
        string hive,
        string clsidRootPath,
        string classId,
        string displayName,
        string serverSubKeyName,
        List<RegistryIssue> issues)
    {
        string classKeyPath = $@"{clsidRootPath}\{classId}";
        using RegistryKey? serverKey = root.OpenSubKey($@"{classKeyPath}\{serverSubKeyName}", writable: false);
        string? targetPath = ExtractExecutablePath(RegistryStringValue(serverKey, string.Empty, doNotExpandEnvironmentNames: true));
        if (string.IsNullOrWhiteSpace(targetPath) || !IsMissingPath(targetPath))
        {
            return;
        }

        issues.Add(CreateRegistryIssue(
            hive,
            classKeyPath,
            "Invalid ActiveX / COM",
            displayName,
            targetPath,
            $"{serverSubKeyName} server path points to a missing file.",
            valueName: null,
            subKeyName: serverSubKeyName,
            severity: "Medium",
            category: "ActiveX / COM"));
    }

    private static void ScanFileExtensionEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
    {
        try
        {
            using RegistryKey? classesKey = root.OpenSubKey(keyPath, writable: false);
            if (classesKey == null)
            {
                return;
            }

            foreach (string extension in classesKey.GetSubKeyNames().Where(name => name.StartsWith(".", StringComparison.Ordinal)).Take(MaxRegistryClassKeys))
            {
                try
                {
                    using RegistryKey? extensionKey = classesKey.OpenSubKey(extension, writable: false);
                    if (extensionKey == null)
                    {
                        continue;
                    }

                    string progId = RegistryStringValue(extensionKey, string.Empty);
                    if (string.IsNullOrWhiteSpace(progId))
                    {
                        if (extensionKey.GetValueNames().Length == 0 && extensionKey.GetSubKeyNames().Length == 0)
                        {
                            issues.Add(CreateRegistryIssue(
                                hive,
                                keyPath,
                                "Unused File Extension",
                                extension,
                                "No application associated",
                                "File extension registration is empty and has no associated program.",
                                valueName: null,
                                subKeyName: extension,
                                severity: "Low",
                                category: "File Types"));
                        }

                        continue;
                    }

                    if (RegistryClassKeyExists(progId))
                    {
                        continue;
                    }

                    issues.Add(CreateRegistryIssue(
                        hive,
                        keyPath,
                        "Unused File Extension",
                        extension,
                        progId,
                        "File extension points to a missing ProgID registration.",
                        valueName: null,
                        subKeyName: extension,
                        severity: "Medium",
                        category: "File Types"));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{hive}\\{keyPath}\\{extension}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
        }
    }

    private static void ScanSharedDllEntries(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
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
                string? targetPath = RegistryPathNormalizer.Normalize(valueName);
                if (string.IsNullOrWhiteSpace(targetPath) || !HasRegistryPathExtension(targetPath) || !IsMissingPath(targetPath))
                {
                    continue;
                }

                issues.Add(CreateRegistryIssue(
                    hive,
                    keyPath,
                    "Missing Shared DLL",
                    Path.GetFileName(targetPath),
                    targetPath,
                    "SharedDLLs reference points to a file that no longer exists.",
                    valueName,
                    subKeyName: null,
                    severity: "Low",
                    category: "Other"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
        }
    }

    private static void ScanHelpFileReferences(RegistryKey root, string hive, string keyPath, List<RegistryIssue> issues, List<string> warnings)
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
                string? helpPath = ResolveHelpReferencePath(valueName, key.GetValue(valueName)?.ToString());
                if (string.IsNullOrWhiteSpace(helpPath) || !IsMissingPath(helpPath))
                {
                    continue;
                }

                issues.Add(CreateRegistryIssue(
                    hive,
                    keyPath,
                    "Invalid Help File Reference",
                    string.IsNullOrWhiteSpace(valueName) ? Path.GetFileName(helpPath) : valueName,
                    helpPath,
                    "Help file registration points to a missing .hlp or .chm file.",
                    valueName,
                    subKeyName: null,
                    severity: "Low",
                    category: "Other"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{hive}\\{keyPath}: {SensitiveDataRedactor.RedactMessage(ex.Message)}");
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
        string? subKeyName,
        string severity = "Medium",
        string? category = null,
        bool canClean = true)
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
            severity,
            canClean,
            string.IsNullOrWhiteSpace(category) ? kind : category);
    }

    private static string CreateRegistryIssueId(string hive, string keyPath, string? valueName, string? subKeyName, string kind)
    {
        string raw = string.Join("|", hive, keyPath, valueName ?? string.Empty, subKeyName ?? string.Empty, kind);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string RegistryStringValue(RegistryKey? key, string name, bool doNotExpandEnvironmentNames = false)
    {
        if (key == null)
        {
            return string.Empty;
        }

        RegistryValueOptions options = doNotExpandEnvironmentNames
            ? RegistryValueOptions.DoNotExpandEnvironmentNames
            : RegistryValueOptions.None;
        object? value = key.GetValue(name, defaultValue: null, options);
        return value switch
        {
            string stringValue => stringValue.Trim(),
            string[] strings => string.Join(";", strings).Trim(),
            _ => value?.ToString()?.Trim() ?? string.Empty
        };
    }

    private static int RegistryIntValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue && longValue >= int.MinValue => (int)longValue,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0
        };
    }

    private static bool RegistryClassKeyExists(string progId)
    {
        string normalized = progId.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('\\'))
        {
            return true;
        }

        return RegistrySubKeyExists(Registry.CurrentUser, $@"Software\Classes\{normalized}") ||
            RegistrySubKeyExists(Registry.LocalMachine, $@"Software\Classes\{normalized}") ||
            RegistrySubKeyExists(Registry.LocalMachine, $@"Software\WOW6432Node\Classes\{normalized}");
    }

    private static bool RegistrySubKeyExists(RegistryKey root, string keyPath)
    {
        using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
        return key != null;
    }

    private static bool HasRegistryPathExtension(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            return RegistryExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string? ResolveHelpReferencePath(string valueName, string? rawValue)
    {
        try
        {
            string normalizedValue = RegistryPathNormalizer.Normalize(rawValue) ?? string.Empty;
            string normalizedName = RegistryPathNormalizer.Normalize(valueName) ?? valueName;

            if (TryNormalizeFullyQualifiedRegistryPath(normalizedValue, out string fullValuePath) &&
                IsHelpFilePath(fullValuePath))
            {
                return fullValuePath;
            }

            if (!IsHelpFilePath(normalizedName))
            {
                return null;
            }

            if (TryNormalizeFullyQualifiedRegistryPath(normalizedName, out string fullNamePath))
            {
                return fullNamePath;
            }

            if (Path.IsPathRooted(normalizedName) ||
                Path.GetFileName(normalizedName) != normalizedName ||
                !TryNormalizeFullyQualifiedRegistryPath(normalizedValue, out string fullBasePath))
            {
                return null;
            }

            string directory = IsHelpFilePath(fullBasePath)
                ? Path.GetDirectoryName(fullBasePath) ?? string.Empty
                : fullBasePath;
            if (!TryNormalizeFullyQualifiedRegistryPath(directory, out string fullDirectory))
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(fullDirectory, normalizedName));
            string normalizedDirectory = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(normalizedDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizeFullyQualifiedRegistryPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            string trimmed = path.Trim().Trim('"');
            if (trimmed.Length == 0 ||
                trimmed.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
                !Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            fullPath = Path.GetFullPath(trimmed);
            return !ContainsAlternateDataStreamToken(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool ContainsAlternateDataStreamToken(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        string pathWithoutRoot = path.Length > root.Length ? path[root.Length..] : string.Empty;
        return pathWithoutRoot.Contains(':', StringComparison.Ordinal);
    }

    private static bool IsHelpFilePath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".hlp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".chm", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractExecutablePath(string? command)
    {
        string? normalized = RegistryPathNormalizer.Normalize(command);
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

        int executableEndIndex = FindRegistryExecutablePathEnd(normalized);
        return executableEndIndex > 0
            ? normalized[..executableEndIndex].Trim().Trim('"')
            : null;
    }

    private static int FindRegistryExecutablePathEnd(string value)
    {
        int bestEndIndex = -1;
        foreach (string extension in RegistryExecutableExtensions)
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
                if (IsRegistryCommandPathBoundary(value, endIndex))
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

    private static bool IsRegistryCommandPathBoundary(string value, int index)
    {
        return index >= value.Length ||
            char.IsWhiteSpace(value[index]) ||
            value[index] is '"' or ',';
    }

    internal static bool IsMissingPath(string path)
    {
        if (!TryNormalizeFullyQualifiedRegistryPath(path, out string normalized))
        {
            return false;
        }

        return !File.Exists(normalized) && !Directory.Exists(normalized);
    }

    private static string WriteRegistryBackup(IReadOnlyCollection<RegistryIssue> issues)
    {
        string backupDirectory = Path.Combine(GetAppDataDirectory(), "RegistryBackups");
        Directory.CreateDirectory(backupDirectory);
        string backupPath = FileWriteService.ResolveAvailablePath(Path.Combine(backupDirectory, $"FileLocker-RegistryBackup-{DateTime.Now:yyyyMMdd-HHmmss}.reg"));

        var builder = new StringBuilder();
        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.AppendLine();

        foreach (RegistryIssue issue in issues)
        {
            AppendRegistryIssueBackup(builder, issue);
        }

        WriteAllTextAtomically(backupPath, builder.ToString(), Encoding.Unicode);
        return backupPath;
    }

    private static void WriteAllTextAtomically(string path, string contents, Encoding encoding)
    {
        FileWriteService.WriteAllTextAtomically(path, contents, encoding);
    }

    private static void AppendRegistryIssueBackup(StringBuilder builder, RegistryIssue issue)
    {
        RegistryKey root = GetRegistryRoot(issue.hive);
        string hiveName = GetRegistryHiveName(issue.hive);

        if (issue.valueName != null)
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

    private static void CleanRegistryIssue(RegistryIssue issue)
    {
        RegistryKey root = GetRegistryRoot(issue.hive);

        if (issue.valueName != null)
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

    internal static string FormatFileSizeForDisplay(long bytes) => FormatFileSize(bytes);

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
        string SafetyLevel,
        CleanupTarget[] Targets,
        bool SupportsDeletion,
        bool RequiresAdministrator,
        bool DefaultSelected,
        string[] Removes,
        string[] Keeps,
        string Recommendation,
        string? UnavailableReason);

    private sealed record CleanupTarget(string Path, string SearchPattern, bool Recursive, bool IsFile);

    private sealed record DirectoryScanSummary(long SizeBytes, int FileCount, int SkippedCount, string[] Warnings, bool LocationFound);

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
    bool isReady,
    string mediaType,
    string mediaDetectionStatus,
    string mediaDescription);

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
    string[] warnings,
    string safetyLevel,
    bool sizeKnown,
    string[] locations,
    string[] removes,
    string[] keeps,
    string recommendation,
    string unavailableReason);

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
    bool canClean,
    string category);

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

using Microsoft.Win32;

namespace FileLocker.Tests;

public sealed class StartupAppMaintenanceServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor\\App\\app.exe\" --minimized", "C:\\Program Files\\Vendor\\App\\app.exe")]
    [InlineData("C:\\Program Files\\Vendor\\App\\app.exe --minimized", "C:\\Program Files\\Vendor\\App\\app.exe")]
    [InlineData("C:\\Tools\\vendor.exe.data\\App\\app.exe --minimized", "C:\\Tools\\vendor.exe.data\\App\\app.exe")]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF}", "msiexec.exe")]
    public void ParseStartupCommandTargetPath_ExtractsExecutableTarget(string command, string expected)
    {
        string? targetPath = StartupAppMaintenanceService.ParseStartupCommandTargetPath(command);

        Assert.Equal(expected, targetPath);
    }

    [Fact]
    public void DeduplicateInstalledApps_PrefersHigherQualityRegistryEntry()
    {
        var x86Duplicate = new InstalledApp(
            "x86",
            "Example App",
            "Example Publisher",
            "1.0",
            "2026-01-01",
            0,
            "0 B",
            string.Empty,
            string.Empty,
            "HKLM",
            "x86",
            requiresAdministrator: true,
            canLaunchUninstaller: false,
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Example");
        var x64Duplicate = x86Duplicate with
        {
            id = "x64",
            estimatedSizeBytes = 1024,
            estimatedSizeDisplay = "1.0 KB",
            installLocation = @"C:\Program Files\Example App",
            uninstallCommand = @"""C:\Program Files\Example App\uninstall.exe""",
            architecture = "x64",
            canLaunchUninstaller = true,
            registryKeyPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Example"
        };
        var unique = x86Duplicate with
        {
            id = "unique",
            displayName = "Other App",
            publisher = "Other Publisher"
        };

        InstalledApp[] apps = StartupAppMaintenanceService.DeduplicateInstalledApps([x86Duplicate, x64Duplicate, unique]);

        Assert.Equal(2, apps.Length);
        Assert.Contains(apps, app => app.id == "x64");
        Assert.DoesNotContain(apps, app => app.id == "x86");
        Assert.Contains(apps, app => app.id == "unique");
    }

    [Fact]
    public void CreateRegistryStartupDisableMetadata_CapturesRestoreFields()
    {
        var item = new StartupItem(
            "startup-id",
            "Example Startup",
            "HKCU Run",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            @"""%LOCALAPPDATA%\Example\app.exe"" --start",
            @"C:\Users\tester\AppData\Local\Example\app.exe",
            isEnabled: true,
            requiresAdministrator: false,
            canToggle: true,
            "Enabled",
            []);

        StartupDisableMetadata metadata = StartupAppMaintenanceService.CreateRegistryStartupDisableMetadata(
            item,
            "HKCU",
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            "Example Startup",
            @"""%LOCALAPPDATA%\Example\app.exe"" --start",
            RegistryValueKind.ExpandString,
            @"C:\Backups\startup.reg");

        Assert.Equal("startup-id", metadata.id);
        Assert.Equal(item.command, metadata.command);
        Assert.Equal(@"C:\Backups\startup.reg", metadata.backupPath);
        Assert.NotNull(metadata.registry);
        Assert.Equal("HKCU", metadata.registry!.hive);
        Assert.Equal("Example Startup", metadata.registry.valueName);
        Assert.Equal(nameof(RegistryValueKind.ExpandString), metadata.registry.valueKind);
        Assert.Equal(item.command, metadata.registry.stringValue);
    }

    [Fact]
    public void IsApprovedAppLeftoverPath_AllowsOnlyAppDataAndProgramDataRoots()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(localAppData, "Vendor", "App", "Cache")));
        Assert.True(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(roamingAppData, "Vendor", "App", "Logs")));
        Assert.True(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(programData, "Vendor", "App", "Temp")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(localAppData, "Temp")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(roamingAppData, "Cache")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(programData, "Logs")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(localAppData));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(windows, "Temp", "Vendor")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(programFiles, "Vendor", "App", "Cache")));
    }

    [Theory]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF}", false)]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF} /qn", true)]
    [InlineData("\"C:\\Program Files\\Vendor\\uninstall.exe\" /S", true)]
    [InlineData("\"C:\\Program Files\\Vendor\\uninstall.exe\" --silent", true)]
    [InlineData("\"C:\\Program Files\\Vendor\\uninstall.exe\" /quiet=true", true)]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF} /qb!", true)]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF} /norestart", false)]
    public void ContainsSilentUninstallSwitch_FlagsQuietCommands(string command, bool expected)
    {
        bool actual = StartupAppMaintenanceService.ContainsSilentUninstallSwitch(command);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Cache", true)]
    [InlineData("Code Cache", true)]
    [InlineData("GPUCache", true)]
    [InlineData("shader-cache", true)]
    [InlineData("temp files", true)]
    [InlineData("crash dumps", true)]
    [InlineData("Catalog", false)]
    [InlineData("Dialog", false)]
    [InlineData("Templates", false)]
    public void IsSafeLeftoverDirectoryName_AvoidsBroadSubstringMatches(string name, bool expected)
    {
        bool actual = StartupAppMaintenanceService.IsSafeLeftoverDirectoryName(name);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsVisibleStartupFolderFile_FiltersHiddenFilesWithoutWarnings()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string visiblePath = Path.Combine(root, "visible.lnk");
        string hiddenPath = Path.Combine(root, "hidden.lnk");

        try
        {
            File.WriteAllText(visiblePath, "visible");
            File.WriteAllText(hiddenPath, "hidden");
            File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
            var warnings = new List<string>();

            Assert.True(StartupAppMaintenanceService.IsVisibleStartupFolderFile(visiblePath, "Startup folder", warnings));
            Assert.False(StartupAppMaintenanceService.IsVisibleStartupFolderFile(hiddenPath, "Startup folder", warnings));
            Assert.Empty(warnings);
        }
        finally
        {
            if (File.Exists(hiddenPath))
            {
                File.SetAttributes(hiddenPath, FileAttributes.Normal);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void GetAvailableDisabledStartupFilePath_AddsSuffixWhenDisabledCopyExists()
    {
        string originalPath = Path.Combine(Path.GetTempPath(), "Example.lnk");
        string firstPath = StartupAppMaintenanceService.GetAvailableDisabledStartupFilePath("startup-test", originalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        File.WriteAllText(firstPath, "existing");

        try
        {
            string secondPath = StartupAppMaintenanceService.GetAvailableDisabledStartupFilePath("startup-test", originalPath);

            Assert.EndsWith("-1.lnk", secondPath);
            Assert.NotEqual(firstPath, secondPath);
        }
        finally
        {
            File.Delete(firstPath);
        }
    }

    [Fact]
    public void RestoreRegistryStartupItem_RefusesToOverwriteExistingValue()
    {
        string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!;
        key.SetValue(valueName, "new-command", RegistryValueKind.String);

        try
        {
            var metadata = new StartupRegistryMetadata(
                "HKCU",
                keyPath,
                valueName,
                nameof(RegistryValueKind.String),
                "old-command",
                multiStringValue: null,
                dwordValue: null,
                qwordValue: null,
                binaryValue: null);

            IOException ex = Assert.Throws<IOException>(() => StartupAppMaintenanceService.RestoreRegistryStartupItem(metadata));

            Assert.Contains("already exists", ex.Message);
            Assert.Equal("new-command", key.GetValue(valueName));
        }
        finally
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void RestoreRegistryStartupItem_RefusesPathOutsideRunKey()
    {
        string keyPath = $@"Software\FileLocker.Tests\StartupRestore\{Guid.NewGuid():N}";

        try
        {
            var metadata = new StartupRegistryMetadata(
                "HKCU",
                keyPath,
                "Example",
                nameof(RegistryValueKind.String),
                "command",
                multiStringValue: null,
                dwordValue: null,
                qwordValue: null,
                binaryValue: null);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                StartupAppMaintenanceService.RestoreRegistryStartupItem(metadata));

            Assert.Contains("outside approved startup registry locations", ex.Message);
            using RegistryKey? createdKey = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.Null(createdKey);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void IsApprovedStartupRestorePath_AllowsOnlyDirectStartupFolderChildren()
    {
        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string commonStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        string outsideFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(startupFolder, "Example.lnk")));
        Assert.True(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(commonStartupFolder, "Example.lnk")));
        Assert.False(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(startupFolder, "Nested", "Example.lnk")));
        Assert.False(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(outsideFolder, "Example.lnk")));
    }

    [Fact]
    public void RestoreStartupFolderItem_RefusesOriginalPathOutsideStartupFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string originalPath = Path.Combine(root, "outside", "Example.lnk");
        string disabledPath = StartupAppMaintenanceService.GetAvailableDisabledStartupFilePath($"startup-test-{Guid.NewGuid():N}", originalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
        File.WriteAllText(disabledPath, "shortcut placeholder");

        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                StartupAppMaintenanceService.RestoreStartupFolderItem(new StartupFileMetadata(originalPath, disabledPath)));

            Assert.Contains("outside approved Startup folders", ex.Message);
            Assert.True(File.Exists(disabledPath));
            Assert.False(File.Exists(originalPath));
        }
        finally
        {
            if (File.Exists(disabledPath))
            {
                File.Delete(disabledPath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RestoreStartupFolderItem_RefusesDisabledPathOutsideManagedStorage()
    {
        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string disabledPath = Path.Combine(root, "disabled.lnk");
        string originalPath = Path.Combine(startupFolder, $"FileLocker.Tests.{Guid.NewGuid():N}.lnk");
        File.WriteAllText(disabledPath, "shortcut placeholder");

        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                StartupAppMaintenanceService.RestoreStartupFolderItem(new StartupFileMetadata(originalPath, disabledPath)));

            Assert.Contains("disabled-startup storage", ex.Message);
            Assert.True(File.Exists(disabledPath));
            Assert.False(File.Exists(originalPath));
        }
        finally
        {
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteLeftoverPath_RemovesNestedDirectoryTreeAndReadOnlyFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "cache", "nested");
        Directory.CreateDirectory(nested);
        string readOnlyFile = Path.Combine(nested, "cache.tmp");
        File.WriteAllText(readOnlyFile, "cache");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);

        StartupAppMaintenanceService.DeleteLeftoverPath(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DeleteLeftoverPath_RemovesReadOnlyDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "cache", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "cache.tmp"), "cache");
        File.SetAttributes(nested, File.GetAttributes(nested) | FileAttributes.ReadOnly);
        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.ReadOnly);

        try
        {
            StartupAppMaintenanceService.DeleteLeftoverPath(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.SetAttributes(root, FileAttributes.Normal);
                foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteLeftoverPath_PreservesHiddenAttributeWhenDeleteFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string hiddenFile = Path.Combine(root, "locked.tmp");
        File.WriteAllText(hiddenFile, "cache");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        try
        {
            using var lockStream = new FileStream(hiddenFile, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Throws<IOException>(() => StartupAppMaintenanceService.DeleteLeftoverPath(root));

            Assert.True((File.GetAttributes(hiddenFile) & FileAttributes.Hidden) == FileAttributes.Hidden);
        }
        finally
        {
            if (File.Exists(hiddenFile))
            {
                File.SetAttributes(hiddenFile, FileAttributes.Normal);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteLeftoverPath_RestoresReadOnlyAttributeWhenFileDeleteFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string readOnlyFile = Path.Combine(root, "locked.tmp");
        File.WriteAllText(readOnlyFile, "cache");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);

        try
        {
            using var lockStream = new FileStream(readOnlyFile, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Throws<IOException>(() => StartupAppMaintenanceService.DeleteLeftoverPath(root));

            Assert.True((File.GetAttributes(readOnlyFile) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
        }
        finally
        {
            if (File.Exists(readOnlyFile))
            {
                File.SetAttributes(readOnlyFile, FileAttributes.Normal);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteLeftoverPath_RejectsPathsOutsideApprovedCleanupRoots()
    {
        string blockedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp",
            $"FileLocker.Tests-{Guid.NewGuid():N}");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.DeleteLeftoverPath(blockedPath));

        Assert.Contains("outside approved app cleanup roots", ex.Message);
    }

    [Fact]
    public void DeleteLeftoverPath_RejectsMissingApprovedPath()
    {
        string missingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker.Tests",
            Guid.NewGuid().ToString("N"),
            "Cache");

        DirectoryNotFoundException ex = Assert.Throws<DirectoryNotFoundException>(() =>
            StartupAppMaintenanceService.DeleteLeftoverPath(missingPath));

        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public void CleanAppLeftovers_RejectsPartiallyUnknownCategorySelectionWithoutDeletingKnownCategory()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string subKeyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileLocker.Tests.{suffix}";
        string displayName = $"FileLocker Test App {suffix}";
        string appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), displayName);
        string cachePath = Path.Combine(appRoot, "Cache");
        string cacheFile = Path.Combine(cachePath, "cache.tmp");
        Directory.CreateDirectory(cachePath);
        File.WriteAllText(cacheFile, "cache");

        using RegistryKey appKey = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true)!;
        appKey.SetValue("DisplayName", displayName, RegistryValueKind.String);
        appKey.SetValue("Publisher", "FileLocker Tests", RegistryValueKind.String);
        appKey.SetValue("DisplayVersion", "1.0", RegistryValueKind.String);

        try
        {
            InstalledApp app = Assert.Single(
                StartupAppMaintenanceService.ScanInstalledApps().apps,
                item => string.Equals(item.displayName, displayName, StringComparison.Ordinal));
            AppLeftoverCategory category = Assert.Single(
                StartupAppMaintenanceService.ScanAppLeftovers([app.id]).categories,
                item => string.Equals(item.appId, app.id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.path, cachePath, StringComparison.OrdinalIgnoreCase));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                StartupAppMaintenanceService.CleanAppLeftovers([app.id], [category.id, "missing-category"], "CLEAN LEFTOVERS"));

            Assert.Contains("app leftover categories are no longer available", ex.Message);
            Assert.True(Directory.Exists(cachePath));
            Assert.True(File.Exists(cacheFile));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);

            if (Directory.Exists(appRoot))
            {
                Directory.Delete(appRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ToDisabledStartupItem_MissingStoredShortcutIsNotToggleable()
    {
        var metadata = new StartupDisableMetadata(
            "startup-id",
            "Example Startup",
            "Startup folder",
            @"C:\Users\tester\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Example.lnk",
            @"C:\Users\tester\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Example.lnk",
            targetPath: null,
            requiresAdministrator: false,
            DateTimeOffset.UtcNow.ToString("O"),
            backupPath: string.Empty,
            registry: null,
            new StartupFileMetadata(
                @"C:\Users\tester\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Example.lnk",
                Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing.lnk")));

        StartupItem item = StartupAppMaintenanceService.ToDisabledStartupItem(metadata);

        Assert.False(item.canToggle);
        Assert.Equal("Restore unavailable", item.status);
        Assert.Contains(item.warnings, warning => warning.Contains("could not be found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToDisabledStartupItem_UnapprovedRestorePathIsNotToggleable()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string disabledPath = Path.Combine(root, "disabled.lnk");
        string originalPath = Path.Combine(root, "outside", "Example.lnk");
        File.WriteAllText(disabledPath, "shortcut placeholder");

        try
        {
            var metadata = new StartupDisableMetadata(
                "startup-id",
                "Example Startup",
                "Startup folder",
                originalPath,
                originalPath,
                targetPath: null,
                requiresAdministrator: false,
                DateTimeOffset.UtcNow.ToString("O"),
                backupPath: string.Empty,
                registry: null,
                new StartupFileMetadata(originalPath, disabledPath));

            StartupItem item = StartupAppMaintenanceService.ToDisabledStartupItem(metadata);

            Assert.False(item.canToggle);
            Assert.Equal("Restore unavailable", item.status);
            Assert.Contains(item.warnings, warning => warning.Contains("outside approved Startup folders", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ToDisabledStartupItem_UnmanagedDisabledPathIsNotToggleable()
    {
        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string disabledPath = Path.Combine(root, "disabled.lnk");
        string originalPath = Path.Combine(startupFolder, $"FileLocker.Tests.{Guid.NewGuid():N}.lnk");
        File.WriteAllText(disabledPath, "shortcut placeholder");

        try
        {
            var metadata = new StartupDisableMetadata(
                "startup-id",
                "Example Startup",
                "Startup folder",
                originalPath,
                originalPath,
                targetPath: null,
                requiresAdministrator: false,
                DateTimeOffset.UtcNow.ToString("O"),
                backupPath: string.Empty,
                registry: null,
                new StartupFileMetadata(originalPath, disabledPath));

            StartupItem item = StartupAppMaintenanceService.ToDisabledStartupItem(metadata);

            Assert.False(item.canToggle);
            Assert.Equal("Restore unavailable", item.status);
            Assert.Contains(item.warnings, warning => warning.Contains("disabled-startup storage", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

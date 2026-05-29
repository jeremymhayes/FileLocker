using Microsoft.Win32;
using System.Text.Json;

namespace FileLocker.Tests;

public sealed class StartupAppMaintenanceServiceTests
{
    private static readonly object StartupMetadataTestLock = new();

    [Fact]
    public void NormalizeWarningMessage_RedactsAndCapsWarnings()
    {
        string message = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\Documents\\secret.txt {new string('A', 4096)}";

        string normalized = StartupAppMaintenanceService.NormalizeWarningMessage(message);

        Assert.True(normalized.Length <= 2048);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), normalized);
        Assert.Contains("%USERPROFILE%", normalized);
        Assert.EndsWith("Warning truncated.", normalized);
    }

    [Fact]
    public void ScanAppLeftovers_BoundsMissingSelectionWarnings()
    {
        string[] missingIds = Enumerable.Range(0, 150)
            .Select(index => $"missing-filelocker-test-app-{index:D3}")
            .ToArray();

        AppLeftoverScanResult result = StartupAppMaintenanceService.ScanAppLeftovers(missingIds);

        Assert.True(result.warnings.Length <= 100);
        Assert.All(result.warnings, warning => Assert.True(warning.Length <= 2048));
    }

    [Fact]
    public void ScanAppLeftovers_RejectsOversizedSelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.ScanAppLeftovers([new string('A', 257)]));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeInstalledAppText_ReplacesControlsAndCapsText()
    {
        string normalized = StartupAppMaintenanceService.NormalizeInstalledAppText(
            $" Vendor\r\nApp\t{new string('A', 700)} ");

        Assert.StartsWith("Vendor App ", normalized);
        Assert.DoesNotContain('\r', normalized);
        Assert.DoesNotContain('\n', normalized);
        Assert.DoesNotContain('\t', normalized);
        Assert.Equal(512, normalized.Length);
    }

    [Fact]
    public void NormalizeInstalledAppText_ReplacesUnicodeFormatCharacters()
    {
        string normalized = StartupAppMaintenanceService.NormalizeInstalledAppText("Vendor\u202EApp");

        Assert.Equal("Vendor App", normalized);
    }

    [Fact]
    public void NormalizeComText_ReplacesControlsAndCapsText()
    {
        string normalized = StartupAppMaintenanceService.NormalizeComText(
            $" Task\r\nAction\t{new string('A', StartupAppMaintenanceService.MaxComTextChars + 50)} ");

        Assert.StartsWith("Task Action ", normalized);
        Assert.DoesNotContain('\r', normalized);
        Assert.DoesNotContain('\n', normalized);
        Assert.DoesNotContain('\t', normalized);
        Assert.Equal(StartupAppMaintenanceService.MaxComTextChars, normalized.Length);
    }

    [Fact]
    public void NormalizeComText_ReplacesUnicodeFormatCharacters()
    {
        string normalized = StartupAppMaintenanceService.NormalizeComText("Task\u202EAction");

        Assert.Equal("Task Action", normalized);
    }

    [Fact]
    public void SetStartupIgnored_RejectsOversizedItemId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.SetStartupIgnored(new string('A', 257), ignored: true));

        Assert.Contains("startup item id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetStartupIgnored_RejectsControlCharacterItemId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.SetStartupIgnored("startup\r\nitem", ignored: true));

        Assert.Contains("startup item id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetStartupEnabled_RejectsOversizedItemId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.SetStartupEnabled(new string('A', 257), enabled: false));

        Assert.Contains("startup item id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetStartupEnabled_RejectsControlCharacterItemId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.SetStartupEnabled("startup\r\nitem", enabled: false));

        Assert.Contains("startup item id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetStartupEnabled_RejectsUnicodeFormatCharacterItemId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.SetStartupEnabled("startup\u202Eitem", enabled: false));

        Assert.Contains("startup item id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchUninstaller_RejectsOversizedAppId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.LaunchUninstaller(new string('A', 257), "UNINSTALL"));

        Assert.Contains("app id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchUninstaller_RejectsControlCharacterAppId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.LaunchUninstaller("app\tid", "UNINSTALL"));

        Assert.Contains("app id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchUninstaller_RejectsUnicodeFormatCharacterAppId()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            StartupAppMaintenanceService.LaunchUninstaller("app\u202Eid", "UNINSTALL"));

        Assert.Contains("app id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor\\App\\app.exe\" --minimized", "C:\\Program Files\\Vendor\\App\\app.exe")]
    [InlineData("C:\\Program Files\\Vendor\\App\\app.exe --minimized", "C:\\Program Files\\Vendor\\App\\app.exe")]
    [InlineData("C:\\Tools\\vendor.exe.data\\App\\app.exe --minimized", "C:\\Tools\\vendor.exe.data\\App\\app.exe")]
    [InlineData("C:\\Program Files\\Vendor\\Shell Extension\\handler.dll", "C:\\Program Files\\Vendor\\Shell Extension\\handler.dll")]
    [InlineData("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF}", "msiexec.exe")]
    public void ParseStartupCommandTargetPath_ExtractsExecutableTarget(string command, string expected)
    {
        string? targetPath = StartupAppMaintenanceService.ParseStartupCommandTargetPath(command);

        Assert.Equal(expected, targetPath);
    }

    [Fact]
    public void ParseStartupCommandTargetPath_RejectsControlCharactersBeforeEnvironmentExpansion()
    {
        string? targetPath = StartupAppMaintenanceService.ParseStartupCommandTargetPath(
            "\"C:\\FileLocker\0Bad\\startup.exe\" --quiet");

        Assert.Null(targetPath);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor\\App.exe:stream\" --quiet")]
    [InlineData("C:\\Program Files\\Vendor\\App.exe:stream --quiet")]
    public void ParseStartupCommandTargetPath_RejectsAlternateDataStreamExecutableTargets(string command)
    {
        string? targetPath = StartupAppMaintenanceService.ParseStartupCommandTargetPath(command);

        Assert.Null(targetPath);
    }

    [Fact]
    public void ParseStartupCommandTargetPath_KeepsLauncherWhenLaterArgumentLooksLikeAlternateDataStream()
    {
        string? targetPath = StartupAppMaintenanceService.ParseStartupCommandTargetPath(
            "cmd.exe /c type C:\\Temp\\payload.exe:stream");

        Assert.Equal("cmd.exe", targetPath);
    }

    [Theory]
    [InlineData("cmd.exe /c start app.exe", "SuspiciousLauncher", "Medium")]
    [InlineData("powershell.exe -File .\\startup.ps1", "SuspiciousLauncher", "Medium")]
    [InlineData("missing-filelocker-startup-test.exe --run", "UnresolvedCommand", "Medium")]
    [InlineData(@"C:Windows\System32\notepad.exe", "UnresolvedCommand", "Medium")]
    public void ResolveStartupCommand_ClassifiesLauncherAndUnresolvedCommands(string command, string expectedStatus, string expectedRisk)
    {
        StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand(command);

        Assert.Equal(expectedStatus, resolution.status);
        Assert.Equal(expectedRisk, resolution.riskLevel);
    }

    [Fact]
    public void ResolveStartupCommand_ExpandsEnvironmentVariables()
    {
        string command = "\"%SystemRoot%\\System32\\notepad.exe\"";

        StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand(command);

        Assert.EndsWith(@"\System32\notepad.exe", resolution.executableResolved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Valid", resolution.status);
        Assert.Equal("High", resolution.confidence);
    }

    [Fact]
    public void ResolveStartupCommand_TreatsMalformedPathAsUnresolved()
    {
        string command = "\"C:\\FileLocker\0Bad\\startup.exe\" --quiet";

        StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand(command);

        Assert.Equal("UnresolvedCommand", resolution.status);
        Assert.Equal("Medium", resolution.riskLevel);
    }

    [Fact]
    public void ResolveStartupCommand_TreatsAlternateDataStreamExecutableAsUnresolved()
    {
        string command = "\"C:\\Program Files\\Vendor\\App.exe:stream\" --quiet";

        StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand(command);

        Assert.Equal("UnresolvedCommand", resolution.status);
        Assert.Equal("Medium", resolution.riskLevel);
        Assert.NotEqual(@"C:\Program", resolution.executableResolved);
    }

    [Fact]
    public void ResolveStartupCommand_TreatsMalformedEnvironmentPathAsUnresolved()
    {
        string variableName = $"FILELOCKER_TEST_BAD_STARTUP_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "C:\\FileLocker\r\nBad");

        try
        {
            StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand($"%{variableName}%\\startup.exe");

            Assert.Equal("UnresolvedCommand", resolution.status);
            Assert.Equal("Medium", resolution.riskLevel);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void ResolveStartupCommand_ClassifiesMissingProtectedTargetAsSystemProtected()
    {
        string command = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            $"missing-filelocker-startup-test-{Guid.NewGuid():N}.exe");

        StartupCommandResolution resolution = StartupAppMaintenanceService.ResolveStartupCommand(command);

        Assert.Equal("SystemProtected", resolution.status);
        Assert.Equal("Low", resolution.riskLevel);
        Assert.Contains("protected", resolution.notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsProtectedStartupPath_RecognizesWindowsAndProgramFilesRoots()
    {
        string windowsTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "example.exe");
        string tempTarget = Path.Combine(Path.GetTempPath(), "example.exe");

        Assert.True(StartupAppMaintenanceService.IsProtectedStartupPath(windowsTarget));
        Assert.False(StartupAppMaintenanceService.IsProtectedStartupPath(tempTarget));
    }

    [Theory]
    [InlineData(@"C:Windows\System32\example.exe")]
    [InlineData(@"Windows\System32\example.exe")]
    public void IsProtectedStartupPath_RejectsRelativePaths(string path)
    {
        Assert.False(StartupAppMaintenanceService.IsProtectedStartupPath(path));
    }

    [Fact]
    public void IsProtectedStartupPath_RejectsUnicodeFormatCharacters()
    {
        string windowsTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "example" + "\u202E" + ".exe");

        Assert.False(StartupAppMaintenanceService.IsProtectedStartupPath(windowsTarget));
    }

    [Fact]
    public void GetServiceStartupCommand_PrefersServiceDllForSvchostHostedService()
    {
        string keyPath = $@"Software\FileLocker.Tests\Services\{Guid.NewGuid():N}";
        const string serviceDll = @"%SystemRoot%\System32\filelocker-test-service.dll";

        try
        {
            using RegistryKey serviceKey = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!;
            serviceKey.SetValue("ImagePath", @"%SystemRoot%\System32\svchost.exe -k netsvcs", RegistryValueKind.ExpandString);
            using (RegistryKey parameters = serviceKey.CreateSubKey("Parameters", writable: true)!)
            {
                parameters.SetValue("ServiceDll", serviceDll, RegistryValueKind.ExpandString);
            }

            string command = StartupAppMaintenanceService.GetServiceStartupCommand(serviceKey);

            Assert.Equal(serviceDll, command);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void GetServiceStartupCommand_KeepsImagePathForStandaloneService()
    {
        string keyPath = $@"Software\FileLocker.Tests\Services\{Guid.NewGuid():N}";
        const string imagePath = @"""C:\Program Files\Vendor\Service\service.exe"" --service";

        try
        {
            using RegistryKey serviceKey = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!;
            serviceKey.SetValue("ImagePath", imagePath, RegistryValueKind.String);
            using (RegistryKey parameters = serviceKey.CreateSubKey("Parameters", writable: true)!)
            {
                parameters.SetValue("ServiceDll", @"%SystemRoot%\System32\ignored.dll", RegistryValueKind.ExpandString);
            }

            string command = StartupAppMaintenanceService.GetServiceStartupCommand(serviceKey);

            Assert.Equal(imagePath, command);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ScanStartup_AggregatesCurrentUserRunProviderEntry()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!;
        key.SetValue(valueName, @"""%SystemRoot%\System32\notepad.exe""", RegistryValueKind.ExpandString);

        try
        {
            StartupScanResult scan = StartupAppMaintenanceService.ScanStartup();

            StartupItem item = Assert.Single(scan.items, candidate =>
                string.Equals(candidate.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.name, valueName, StringComparison.Ordinal));
            Assert.Equal("Registry", item.sourceType);
            Assert.Equal("Startup Apps", item.category);
            Assert.Equal("Current user", item.scope);
            Assert.Equal("Enabled", item.status);
            Assert.Equal("High", item.confidence);
            Assert.True(item.canToggle);
        }
        finally
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void ScanStartup_AggregatesGroupPolicyLogonScriptAsReadOnly()
    {
        string testRoot = $@"Software\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon\FileLocker.Tests.{Guid.NewGuid():N}";
        string scriptKeyPath = $@"{testRoot}\0";
        RegistryKey? key;
        try
        {
            key = Registry.CurrentUser.CreateSubKey(scriptKeyPath, writable: true);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.NotNull(key);
        using (key)
        {
            key.SetValue("Script", @"%SystemRoot%\System32\notepad.exe", RegistryValueKind.ExpandString);
            key.SetValue("Parameters", "--policy-test", RegistryValueKind.String);
        }

        try
        {
            StartupScanResult scan = StartupAppMaintenanceService.ScanStartup();

            StartupItem item = Assert.Single(scan.items, candidate =>
                string.Equals(candidate.sourceType, "Group Policy script", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.location, $@"HKCU\{scriptKeyPath}", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("PolicyManaged", item.status);
            Assert.Equal("Current user", item.scope);
            Assert.True(item.isReadOnlyManaged);
            Assert.False(item.canToggle);
            Assert.Contains("--policy-test", item.commandRaw, StringComparison.Ordinal);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ScanStartup_AggregatesPolicyRunEntriesAsPolicyManaged()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
        RegistryKey? key;
        try
        {
            key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.NotNull(key);
        using (key)
        {
            key.SetValue(valueName, @"""%SystemRoot%\System32\notepad.exe""", RegistryValueKind.ExpandString);
        }

        try
        {
            StartupScanResult scan = StartupAppMaintenanceService.ScanStartup();

            StartupItem item = Assert.Single(scan.items, candidate =>
                string.Equals(candidate.source, "HKCU Policy Run", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.name, valueName, StringComparison.Ordinal));
            Assert.Equal("PolicyManaged", item.status);
            Assert.True(item.isReadOnlyManaged);
            Assert.False(item.canToggle);
        }
        finally
        {
            using RegistryKey? cleanupKey = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            cleanupKey?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void ScanStartup_AggregatesCurrentUserExplorerContextMenuHandlerAsReadOnly()
    {
        string handlerName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        string handlerPath = $@"Software\Classes\*\shellex\ContextMenuHandlers\{handlerName}";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(handlerPath, writable: true)!;
        key.SetValue(string.Empty, "{00000000-0000-0000-0000-000000000000}", RegistryValueKind.String);

        try
        {
            StartupScanResult scan = StartupAppMaintenanceService.ScanStartup();

            StartupItem item = Assert.Single(scan.items, candidate =>
                string.Equals(candidate.source, "Explorer context menu handler", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.name, handlerName, StringComparison.Ordinal));
            Assert.Equal("Advanced Startup Hooks", item.category);
            Assert.Equal("Registry", item.sourceType);
            Assert.True(item.isReadOnlyManaged);
            Assert.False(item.canToggle);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(handlerPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void SetStartupEnabled_DisablesAndRestoresCurrentUserRunEntry()
    {
        lock (StartupMetadataTestLock)
        {
            string metadataPath = GetStartupDisabledMetadataPath();
            byte[]? originalMetadata = File.Exists(metadataPath) ? File.ReadAllBytes(metadataPath) : null;
            string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
            string command = @"""%SystemRoot%\System32\notepad.exe"" --startup-flow-test";
            string? backupPath = null;
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
                {
                    key.SetValue(valueName, command, RegistryValueKind.ExpandString);
                }

                StartupItem enabledItem = Assert.Single(
                    StartupAppMaintenanceService.ScanStartup().items,
                    item => string.Equals(item.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.name, valueName, StringComparison.Ordinal));

                StartupToggleResult disabled = StartupAppMaintenanceService.SetStartupEnabled(enabledItem.id, enabled: false);
                backupPath = disabled.backupPath;

                Assert.False(disabled.isEnabled);
                Assert.False(disabled.item.isEnabled);
                Assert.Equal("Disabled by FileLocker", disabled.item.status);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!)
                {
                    Assert.Null(key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }

                StartupScanResult disabledScan = StartupAppMaintenanceService.ScanStartup();
                StartupItem disabledItem = Assert.Single(disabledScan.items, item => string.Equals(item.id, enabledItem.id, StringComparison.OrdinalIgnoreCase));
                Assert.False(disabledItem.isEnabled);
                Assert.True(disabledItem.canToggle);
                Assert.Contains(disabledScan.restoreRecords, record =>
                    string.Equals(record.id, enabledItem.id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.userAction, "Disable", StringComparison.OrdinalIgnoreCase));

                StartupToggleResult restored = StartupAppMaintenanceService.SetStartupEnabled(enabledItem.id, enabled: true);

                Assert.True(restored.isEnabled);
                Assert.Equal("Restored", restored.item.status);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!)
                {
                    Assert.Equal(command, key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }

                StartupScanResult restoredScan = StartupAppMaintenanceService.ScanStartup();
                Assert.Contains(restoredScan.items, item =>
                    string.Equals(item.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.name, valueName, StringComparison.Ordinal));
                Assert.DoesNotContain(restoredScan.restoreRecords, record => string.Equals(record.id, enabledItem.id, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
                {
                    key?.DeleteValue(valueName, throwOnMissingValue: false);
                }

                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                RestoreStartupDisabledMetadata(metadataPath, originalMetadata);
            }
        }
    }

    [Fact]
    public void ScanStartup_IgnoresOversizedDisabledStartupMetadata()
    {
        lock (StartupMetadataTestLock)
        {
            string metadataPath = GetStartupDisabledMetadataPath();
            byte[]? originalMetadata = File.Exists(metadataPath) ? File.ReadAllBytes(metadataPath) : null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
                File.WriteAllText(metadataPath, new string(' ', 1_048_577));

                StartupScanResult scan = StartupAppMaintenanceService.ScanStartup();

                Assert.Contains(scan.warnings, warning => warning.Contains("too large", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                RestoreStartupDisabledMetadata(metadataPath, originalMetadata);
            }
        }
    }

    [Fact]
    public void SetStartupEnabled_PersistsRestoreFailureDetails()
    {
        lock (StartupMetadataTestLock)
        {
            string metadataPath = GetStartupDisabledMetadataPath();
            byte[]? originalMetadata = File.Exists(metadataPath) ? File.ReadAllBytes(metadataPath) : null;
            string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
            string command = @"""%SystemRoot%\System32\notepad.exe"" --restore-failure-test";
            string conflictingCommand = @"""%SystemRoot%\System32\calc.exe"" --restore-conflict-test";
            string? backupPath = null;
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
                {
                    key.SetValue(valueName, command, RegistryValueKind.ExpandString);
                }

                StartupItem enabledItem = Assert.Single(
                    StartupAppMaintenanceService.ScanStartup().items,
                    item => string.Equals(item.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.name, valueName, StringComparison.Ordinal));

                StartupToggleResult disabled = StartupAppMaintenanceService.SetStartupEnabled(enabledItem.id, enabled: false);
                backupPath = disabled.backupPath;

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
                {
                    key.SetValue(valueName, conflictingCommand, RegistryValueKind.String);
                }

                IOException ex = Assert.Throws<IOException>(() =>
                    StartupAppMaintenanceService.SetStartupEnabled(enabledItem.id, enabled: true));
                Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);

                StartupRestoreRecord record = Assert.Single(
                    StartupAppMaintenanceService.ScanStartup().restoreRecords,
                    item => string.Equals(item.id, enabledItem.id, StringComparison.OrdinalIgnoreCase));
                Assert.Equal("Failed", record.restoreStatus);
                Assert.Contains("already exists", record.failureDetails, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
                {
                    key?.DeleteValue(valueName, throwOnMissingValue: false);
                }

                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                RestoreStartupDisabledMetadata(metadataPath, originalMetadata);
            }
        }
    }

    [Fact]
    public void RemoveBrokenStartupItem_RequiresConfirmationAndPreservesRestorePath()
    {
        lock (StartupMetadataTestLock)
        {
            string metadataPath = GetStartupDisabledMetadataPath();
            byte[]? originalMetadata = File.Exists(metadataPath) ? File.ReadAllBytes(metadataPath) : null;
            string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
            string missingTarget = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing-startup-target.exe");
            string command = $@"""{missingTarget}"" --broken-flow-test";
            string? backupPath = null;
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
                {
                    key.SetValue(valueName, command, RegistryValueKind.String);
                }

                StartupItem brokenItem = Assert.Single(
                    StartupAppMaintenanceService.ScanStartup().items,
                    item => string.Equals(item.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.name, valueName, StringComparison.Ordinal));
                Assert.Equal("Broken Startup Items", brokenItem.category);
                Assert.True(brokenItem.canToggle);

                InvalidOperationException confirmationError = Assert.Throws<InvalidOperationException>(() =>
                    StartupAppMaintenanceService.RemoveBrokenStartupItem(brokenItem.id, "REMOVE"));
                Assert.Contains("Confirm broken startup removal", confirmationError.Message);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!)
                {
                    Assert.Equal(command, key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }

                StartupToggleResult removed = StartupAppMaintenanceService.RemoveBrokenStartupItem(brokenItem.id, "REMOVE BROKEN STARTUP");
                backupPath = removed.backupPath;

                Assert.False(removed.isEnabled);
                Assert.Contains("removed from active startup", removed.message, StringComparison.OrdinalIgnoreCase);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!)
                {
                    Assert.Null(key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }

                StartupScanResult removedScan = StartupAppMaintenanceService.ScanStartup();
                Assert.Contains(removedScan.restoreRecords, record =>
                    string.Equals(record.id, brokenItem.id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.restoreStatus, "Available", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.userAction, "RemoveBroken", StringComparison.OrdinalIgnoreCase));

                StartupToggleResult restored = StartupAppMaintenanceService.SetStartupEnabled(brokenItem.id, enabled: true);
                Assert.True(restored.isEnabled);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!)
                {
                    Assert.Equal(command, key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }
            }
            finally
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
                {
                    key?.DeleteValue(valueName, throwOnMissingValue: false);
                }

                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                RestoreStartupDisabledMetadata(metadataPath, originalMetadata);
            }
        }
    }

    [Fact]
    public void ExportStartupItemDetails_WritesNormalizedStartupItemPayload()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        string command = @"""%SystemRoot%\System32\notepad.exe"" --export-flow-test";
        string? exportPath = null;
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue(valueName, command, RegistryValueKind.ExpandString);
            }

            StartupItem item = Assert.Single(
                StartupAppMaintenanceService.ScanStartup().items,
                candidate => string.Equals(candidate.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.name, valueName, StringComparison.Ordinal));

            StartupExportResult export = StartupAppMaintenanceService.ExportStartupItemDetails(item.id);
            exportPath = export.exportPath;

            Assert.Equal(item.id, export.itemId);
            Assert.True(export.fullPathsIncluded);
            Assert.StartsWith("FileLocker-StartupItem-", export.fileName, StringComparison.Ordinal);
            Assert.True(File.Exists(exportPath));

            StartupItem? exportedItem = JsonSerializer.Deserialize<StartupItem>(File.ReadAllText(exportPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(exportedItem);
            Assert.Equal(item.id, exportedItem!.id);
            Assert.Equal(valueName, exportedItem.name);
            Assert.Equal("Registry", exportedItem.sourceType);
            Assert.Equal("Startup Apps", exportedItem.category);
            Assert.Equal(command, exportedItem.commandRaw);
        }
        finally
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }

            if (!string.IsNullOrWhiteSpace(exportPath) && File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }

    [Fact]
    public void ExportStartupItemDetails_RedactsFullPathsWhenRequested()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        string targetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker.Tests",
            Guid.NewGuid().ToString("N"),
            "redacted-startup-target.exe");
        string command = $@"""{targetPath}"" --redacted-export-test";
        string? exportPath = null;
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, "test executable placeholder");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue(valueName, command, RegistryValueKind.String);
            }

            StartupItem item = Assert.Single(
                StartupAppMaintenanceService.ScanStartup().items,
                candidate => string.Equals(candidate.source, "HKCU Run", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.name, valueName, StringComparison.Ordinal));

            StartupExportResult export = StartupAppMaintenanceService.ExportStartupItemDetails(item.id, includeFullPaths: false);
            exportPath = export.exportPath;

            Assert.False(export.fullPathsIncluded);
            string json = File.ReadAllText(exportPath);
            Assert.DoesNotContain(targetPath, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetPath.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%LOCALAPPDATA%", json, StringComparison.OrdinalIgnoreCase);

            StartupItem? exportedItem = JsonSerializer.Deserialize<StartupItem>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(exportedItem);
            Assert.Contains("%LOCALAPPDATA%", exportedItem!.commandRaw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("redacted-startup-target.exe", exportedItem.commandRaw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetDirectoryName(targetPath)!, exportedItem.commandRaw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }

            if (!string.IsNullOrWhiteSpace(exportPath) && File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }

            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory) && Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }
    }

    private static string GetStartupDisabledMetadataPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker",
            "SystemCare",
            "startup-disabled.json");
    }

    private static void RestoreStartupDisabledMetadata(string metadataPath, byte[]? originalMetadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        if (originalMetadata == null)
        {
            File.Delete(metadataPath);
            return;
        }

        File.WriteAllBytes(metadataPath, originalMetadata);
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
            @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Example",
            iconDataUri: null);
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
            @"C:\Backups\startup.reg",
            RegistryView.Registry32);

        Assert.Equal("startup-id", metadata.id);
        Assert.Equal(item.command, metadata.command);
        Assert.Equal(@"C:\Backups\startup.reg", metadata.backupPath);
        Assert.Equal("Startup", metadata.sourceType);
        Assert.Equal("Startup Apps", metadata.category);
        Assert.Equal("Available", metadata.restoreStatus);
        Assert.Equal("Disable", metadata.userAction);
        Assert.Equal(item.targetPath, metadata.resolvedExecutable);
        Assert.Equal(item.status, metadata.commandStatus);
        Assert.Equal(item.confidence, metadata.confidence);
        Assert.Equal(item.riskLevel, metadata.riskLevel);
        Assert.False(string.IsNullOrWhiteSpace(metadata.fileLockerVersion));
        Assert.NotNull(metadata.registry);
        Assert.Equal("HKCU", metadata.registry!.hive);
        Assert.Equal("Example Startup", metadata.registry.valueName);
        Assert.Equal(nameof(RegistryValueKind.ExpandString), metadata.registry.valueKind);
        Assert.Equal(item.command, metadata.registry.stringValue);
        Assert.Equal("32-bit", metadata.registry.registryView);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Example\Task", @"\Microsoft\Windows\Example", "Task")]
    [InlineData(@"RootTask", @"\", "RootTask")]
    [InlineData(@"\RootTask", @"\", "RootTask")]
    public void SplitScheduledTaskPath_ReturnsFolderAndTaskName(string path, string expectedFolder, string expectedTask)
    {
        (string folder, string task) = StartupAppMaintenanceService.SplitScheduledTaskPath(path);

        Assert.Equal(expectedFolder, folder);
        Assert.Equal(expectedTask, task);
    }

    [Fact]
    public void CreateScheduledTaskDisableMetadata_CapturesRestoreFields()
    {
        var item = new StartupItem(
            "task-id",
            "Example Task",
            "Scheduled Task",
            @"\Example Task",
            @"""C:\Tools\example.exe""",
            @"C:\Tools\example.exe",
            isEnabled: true,
            requiresAdministrator: true,
            canToggle: true,
            "Ready",
            [],
            sourceType: "Scheduled Task",
            scope: "Task Scheduler",
            disableMethod: "TaskScheduler");

        StartupDisableMetadata metadata = StartupAppMaintenanceService.CreateScheduledTaskDisableMetadata(item, @"\Example Task");

        Assert.Null(metadata.registry);
        Assert.Null(metadata.file);
        Assert.NotNull(metadata.task);
        Assert.Equal(@"\Example Task", metadata.task!.taskPath);
        Assert.True(metadata.task.wasEnabled);
        Assert.Equal("Scheduled Task", metadata.sourceType);
        Assert.Equal("Available", metadata.restoreStatus);
        Assert.Equal("Disable", metadata.userAction);
        Assert.Equal(item.targetPath, metadata.resolvedExecutable);
        Assert.Equal(item.status, metadata.commandStatus);
    }

    [Theory]
    [InlineData(0, "Event")]
    [InlineData(1, "Time")]
    [InlineData(2, "Daily")]
    [InlineData(3, "Weekly")]
    [InlineData(4, "Monthly")]
    [InlineData(5, "Monthly day-of-week")]
    [InlineData(6, "Idle")]
    [InlineData(7, "Registration")]
    [InlineData(8, "Boot")]
    [InlineData(9, "Logon")]
    [InlineData(11, "Session change")]
    public void ScheduledTaskTriggerScope_IncludesNamedStartupControlTriggers(int triggerType, string expectedLabel)
    {
        Assert.True(StartupAppMaintenanceService.IsScheduledTaskTriggerInScope(triggerType));
        Assert.Equal(expectedLabel, StartupAppMaintenanceService.GetScheduledTaskTriggerLabel(triggerType));
    }

    [Fact]
    public void ScheduledTaskTriggerScope_ExcludesUnknownCustomTriggers()
    {
        Assert.False(StartupAppMaintenanceService.IsScheduledTaskTriggerInScope(12));
        Assert.Equal("Trigger 12", StartupAppMaintenanceService.GetScheduledTaskTriggerLabel(12));
    }

    [Fact]
    public void CreateServiceDisableMetadata_CapturesOriginalStartType()
    {
        var item = new StartupItem(
            "service-id",
            "Example Service",
            "Automatic service",
            @"HKLM\SYSTEM\CurrentControlSet\Services\Example",
            @"""C:\Tools\service.exe""",
            @"C:\Tools\service.exe",
            isEnabled: true,
            requiresAdministrator: true,
            canToggle: true,
            "Enabled",
            [],
            sourceType: "Service",
            scope: "System",
            disableMethod: "ServiceControlManager");

        StartupDisableMetadata metadata = StartupAppMaintenanceService.CreateServiceDisableMetadata(item, "Example", 2);

        Assert.Null(metadata.registry);
        Assert.Null(metadata.file);
        Assert.NotNull(metadata.service);
        Assert.Equal("Example", metadata.service!.serviceName);
        Assert.Equal(2, metadata.service.originalStartType);
        Assert.Equal("Service", metadata.sourceType);
        Assert.Equal("Available", metadata.restoreStatus);
        Assert.Equal("Disable", metadata.userAction);
        Assert.Equal(item.targetPath, metadata.resolvedExecutable);
        Assert.Equal(item.status, metadata.commandStatus);
    }

    [Fact]
    public void StartupDisableMetadata_SerializesTaskAndServiceRestorePayloads()
    {
        var metadata = new StartupDisableMetadata(
            "startup-id",
            "Example",
            "Scheduled Task",
            @"\Example",
            @"""C:\Tools\example.exe""",
            @"C:\Tools\example.exe",
            requiresAdministrator: true,
            DateTimeOffset.UtcNow.ToString("O"),
            backupPath: string.Empty,
            registry: null,
            file: null,
            sourceType: "Scheduled Task",
            category: "Startup Apps",
            scope: "Task Scheduler",
            originalStatus: "Ready",
            fileLockerVersion: "1.2.3.4",
            restoreStatus: "Available",
            failureDetails: string.Empty,
            task: new StartupTaskMetadata(@"\Example", wasEnabled: true),
            service: new StartupServiceMetadata("ExampleService", originalStartType: 2),
            userAction: "Disable",
            resolvedExecutable: @"C:\Tools\example.exe",
            commandStatus: "Ready",
            confidence: "High",
            riskLevel: "Low");

        string json = JsonSerializer.Serialize(metadata);
        StartupDisableMetadata? restored = JsonSerializer.Deserialize<StartupDisableMetadata>(json);

        Assert.NotNull(restored);
        Assert.Equal("Scheduled Task", restored!.sourceType);
        Assert.Equal(@"\Example", restored.task!.taskPath);
        Assert.True(restored.task.wasEnabled);
        Assert.Equal("ExampleService", restored.service!.serviceName);
        Assert.Equal(2, restored.service.originalStartType);
        Assert.Equal("Available", restored.restoreStatus);
        Assert.Equal("Disable", restored.userAction);
        Assert.Equal(@"C:\Tools\example.exe", restored.resolvedExecutable);
        Assert.Equal("Ready", restored.commandStatus);
        Assert.Equal("High", restored.confidence);
        Assert.Equal("Low", restored.riskLevel);
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
    [InlineData(@"Vendor\App\Cache")]
    [InlineData(@"C:Vendor\App\Cache")]
    public void IsApprovedAppLeftoverPath_RejectsRelativePaths(string path)
    {
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(path));
    }

    [Fact]
    public void IsApprovedAppLeftoverPath_RejectsUnsafePathText()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(localAppData, "Vendor", "App:stream", "Cache")));
        Assert.False(StartupAppMaintenanceService.IsApprovedAppLeftoverPath(Path.Combine(localAppData, "Vendor", "App" + "\u202E", "Cache")));
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

    [Fact]
    public void NormalizeInstalledAppCommand_TrimsSafeCommand()
    {
        string command = StartupAppMaintenanceService.NormalizeInstalledAppCommand("  msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF}  ");

        Assert.Equal("msiexec.exe /x {01234567-89AB-CDEF-0123-456789ABCDEF}", command);
    }

    [Fact]
    public void NormalizeInstalledAppCommand_DropsControlCharacterCommand()
    {
        string command = StartupAppMaintenanceService.NormalizeInstalledAppCommand("uninstall.exe\r\n--interactive");

        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void NormalizeInstalledAppCommand_DropsUnicodeFormatCharacterCommand()
    {
        string command = StartupAppMaintenanceService.NormalizeInstalledAppCommand("uninstall\u202E.exe");

        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void NormalizeInstalledAppCommand_DropsOversizedCommand()
    {
        string command = StartupAppMaintenanceService.NormalizeInstalledAppCommand(new string('A', StartupAppMaintenanceService.MaxComTextChars + 1));

        Assert.Equal(string.Empty, command);
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
    public void RestoreRegistryStartupItem_UsesStoredRegistryView()
    {
        string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        const string command = @"""C:\Tools\view-specific.exe""";
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using RegistryKey key = root.CreateSubKey(keyPath, writable: true)!;
        key.DeleteValue(valueName, throwOnMissingValue: false);

        try
        {
            var metadata = new StartupRegistryMetadata(
                "HKCU",
                keyPath,
                valueName,
                nameof(RegistryValueKind.String),
                command,
                multiStringValue: null,
                dwordValue: null,
                qwordValue: null,
                binaryValue: null,
                registryView: "32-bit");

            StartupAppMaintenanceService.RestoreRegistryStartupItem(metadata);

            Assert.Equal(command, key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames));
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

    [Theory]
    [InlineData(@"Startup\Example.lnk")]
    [InlineData(@"C:Startup\Example.lnk")]
    public void IsApprovedStartupRestorePath_RejectsRelativePaths(string path)
    {
        Assert.False(StartupAppMaintenanceService.IsApprovedStartupRestorePath(path));
    }

    [Fact]
    public void IsApprovedStartupRestorePath_RejectsUnsafePathText()
    {
        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        Assert.False(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(startupFolder, "Example.lnk:stream")));
        Assert.False(StartupAppMaintenanceService.IsApprovedStartupRestorePath(Path.Combine(startupFolder, "Example" + "\u202E" + ".lnk")));
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

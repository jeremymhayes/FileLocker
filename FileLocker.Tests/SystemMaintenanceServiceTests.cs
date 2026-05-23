using Microsoft.Win32;

namespace FileLocker.Tests;

public sealed class SystemMaintenanceServiceTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void NormalizeDriveRoot_RejectsBlankDriveRoot()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.NormalizeDriveRoot(" "));

        Assert.Equal("Select a drive first.", ex.Message);
    }

    [Fact]
    public void NormalizeDriveRoot_RejectsMalformedDriveRoot()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.NormalizeDriveRoot("C:\\bad\0drive"));

        Assert.Equal("The selected drive is invalid.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(EmptyRegistrySelections))]
    public void CleanRegistry_RequiresExplicitIssueSelection(IReadOnlyCollection<string>? issueIds)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry(issueIds, "FIX REGISTRY"));

        Assert.Contains("Select at least one registry issue", ex.Message);
    }

    public static IEnumerable<object?[]> EmptyRegistrySelections()
    {
        yield return [null];
        yield return [Array.Empty<string>()];
        yield return [new[] { string.Empty, "   " }];
    }

    [Fact]
    public void ScanCleanup_TrimsCategorySelections()
    {
        CleanupScanResult scan = SystemMaintenanceService.ScanCleanup([" recycleBin "]);

        CleanupCategory category = Assert.Single(scan.categories);
        Assert.Equal("recycleBin", category.id);
    }

    [Fact]
    public void RunCleanup_RequiresExplicitConfirmation()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.RunCleanup(["recycleBin"], confirmation: null));

        Assert.Contains("Confirm cleanup", ex.Message);
    }

    [Fact]
    public void RunCleanup_RejectsUnknownCategorySelection()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.RunCleanup(["missing-category"], "CLEAN SELECTED"));

        Assert.Contains("cleanup categories are no longer available", ex.Message);
    }

    [Fact]
    public void RunCleanup_RejectsPartiallyUnknownCategorySelection()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.RunCleanup(["recycleBin", "missing-category"], "CLEAN SELECTED"));

        Assert.Contains("cleanup categories are no longer available", ex.Message);
    }

    [Fact]
    public void CleanRegistry_RejectsUnknownIssueSelection()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry(["missing-issue"], "FIX REGISTRY"));

        Assert.Contains("registry issues are no longer available", ex.Message);
    }

    [Fact]
    public void CleanRegistry_RejectsPartiallyUnknownIssueSelectionWithoutDeletingKnownIssue()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        string missingPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing.exe");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)!;
        key.SetValue(valueName, $"\"{missingPath}\"", RegistryValueKind.String);

        try
        {
            RegistryIssue issue = SystemMaintenanceService.ScanRegistry().issues.Single(issue =>
                string.Equals(issue.hive, "HKCU", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(issue.keyPath, RunKeyPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(issue.valueName, valueName, StringComparison.Ordinal));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => SystemMaintenanceService.CleanRegistry([issue.id, "missing-issue"], "FIX REGISTRY"));

            Assert.Contains("registry issues are no longer available", ex.Message);
            Assert.Equal($"\"{missingPath}\"", key.GetValue(valueName));
        }
        finally
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void ScanRegistry_DoesNotSplitStartupCommandAtExeTextInsideDirectoryName()
    {
        string valueName = $"FileLocker.Tests.{Guid.NewGuid():N}";
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string appDirectory = Path.Combine(root, "vendor.exe.data", "App");
        string appPath = Path.Combine(appDirectory, "app.exe");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)!;
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(appPath, "test executable placeholder");
        key.SetValue(valueName, $"{appPath} --minimized", RegistryValueKind.String);

        try
        {
            RegistryScanResult scan = SystemMaintenanceService.ScanRegistry();

            Assert.DoesNotContain(scan.issues, issue =>
                string.Equals(issue.hive, "HKCU", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(issue.keyPath, RunKeyPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(issue.valueName, valueName, StringComparison.Ordinal));
        }
        finally
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

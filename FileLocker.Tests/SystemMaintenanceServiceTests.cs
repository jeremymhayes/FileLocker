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
    }

    [Fact]
    public void NormalizeDriveRoot_RejectsControlCharacterDriveRoot()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.NormalizeDriveRoot("C:\\bad\r\ndrive"));

        Assert.Equal("The selected drive is invalid.", ex.Message);
    }

    [Fact]
    public void NormalizeDriveRoot_RejectsUnicodeFormatCharacterDriveRoot()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.NormalizeDriveRoot("C:\\bad\u202Edrive"));

        Assert.Equal("The selected drive is invalid.", ex.Message);
    }

    [Fact]
    public void NormalizeDriveRoot_RejectsNonRootDrivePath()
    {
        string currentRoot = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.NormalizeDriveRoot(Path.Combine(currentRoot, "Windows")));

        Assert.Equal("The selected drive is invalid.", ex.Message);
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
    public void ScanCleanup_RejectsOversizedCategorySelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SystemMaintenanceService.ScanCleanup([new string('A', 257)]));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanCleanup_RejectsControlCharacterCategorySelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SystemMaintenanceService.ScanCleanup(["recycle\r\nBin"]));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanCleanup_RejectsUnicodeFormatCharacterCategorySelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SystemMaintenanceService.ScanCleanup(["recycle\u202EBin"]));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunCleanup_RejectsTooManyCategorySelectionIds()
    {
        string[] categoryIds = Enumerable.Range(0, 501)
            .Select(index => $"missing-category-{index:D3}")
            .ToArray();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SystemMaintenanceService.RunCleanup(categoryIds, "CLEAN SELECTED"));

        Assert.Contains("Too many selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void NormalizeMaintenanceToolOutput_RedactsLocalPaths()
    {
        string output = $"Processed {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\Documents\\file.txt";

        string normalized = SystemMaintenanceService.NormalizeMaintenanceToolOutput(output);

        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), normalized);
        Assert.Contains("%USERPROFILE%", normalized);
    }

    [Fact]
    public void NormalizeMaintenanceToolOutput_CapsLargeOutput()
    {
        string normalized = SystemMaintenanceService.NormalizeMaintenanceToolOutput(new string('A', 20 * 1024));

        Assert.True(normalized.Length <= 16 * 1024);
        Assert.EndsWith("Output truncated.", normalized);
    }

    [Fact]
    public void NormalizeWarningMessage_RedactsAndCapsWarnings()
    {
        string message = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\Documents\\secret.txt {new string('A', 4096)}";

        string normalized = SystemMaintenanceService.NormalizeWarningMessage(message);

        Assert.True(normalized.Length <= 2048);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), normalized);
        Assert.Contains("%USERPROFILE%", normalized);
        Assert.EndsWith("Warning truncated.", normalized);
    }

    [Fact]
    public void CleanRegistry_RejectsUnknownIssueSelection()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry(["missing-issue"], "FIX REGISTRY"));

        Assert.Contains("registry issues are no longer available", ex.Message);
    }

    [Fact]
    public void CleanRegistry_RejectsOversizedIssueSelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry([new string('A', 257)], "FIX REGISTRY"));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanRegistry_RejectsControlCharacterIssueSelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry(["issue\r\nid"], "FIX REGISTRY"));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanRegistry_RejectsUnicodeFormatCharacterIssueSelectionIds()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SystemMaintenanceService.CleanRegistry(["issue\u202Eid"], "FIX REGISTRY"));

        Assert.Contains("selected item ids", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ResolveHelpReferencePath_ReturnsFullyQualifiedHelpValue()
    {
        string helpPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "topic.chm");

        string? resolved = SystemMaintenanceService.ResolveHelpReferencePath("DisplayName", helpPath);

        Assert.Equal(Path.GetFullPath(helpPath), resolved);
    }

    [Fact]
    public void ResolveHelpReferencePath_RejectsDriveRelativeHelpValue()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string driveRelativePath = $"{root[0]}:topic.chm";

        string? resolved = SystemMaintenanceService.ResolveHelpReferencePath("DisplayName", driveRelativePath);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveHelpReferencePath_CombinesSimpleHelpNameWithQualifiedBase()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

        string? resolved = SystemMaintenanceService.ResolveHelpReferencePath("topic.chm", baseDirectory);

        Assert.Equal(Path.Combine(Path.GetFullPath(baseDirectory), "topic.chm"), resolved);
    }

    [Fact]
    public void ResolveHelpReferencePath_RejectsRelativeHelpNameWithDirectoryTraversal()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

        string? resolved = SystemMaintenanceService.ResolveHelpReferencePath(@"..\topic.chm", baseDirectory);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveHelpReferencePath_RejectsAlternateDataStreamBasePath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string adsBasePath = Path.Combine(root, "FileLocker.Tests:ads", "Help");

        string? resolved = SystemMaintenanceService.ResolveHelpReferencePath("topic.chm", adsBasePath);

        Assert.Null(resolved);
    }

    [Fact]
    public void IsMissingPath_ReturnsTrueForMissingFullyQualifiedPath()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing.exe");

        Assert.True(SystemMaintenanceService.IsMissingPath(missingPath));
    }

    [Fact]
    public void IsMissingPath_FailsClosedForDriveRelativePath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string driveRelativePath = $"{root[0]}:missing.exe";

        Assert.False(SystemMaintenanceService.IsMissingPath(driveRelativePath));
    }

    [Fact]
    public void IsMissingPath_FailsClosedForAlternateDataStreamPath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string adsPath = Path.Combine(root, "FileLocker.Tests:ads", "missing.exe");

        Assert.False(SystemMaintenanceService.IsMissingPath(adsPath));
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsHddForSingleHddPhysicalDisk()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3], physicalDiskCount: 1, timedOut: false, unsupported: false);

        Assert.Equal("HDD", media.mediaType);
        Assert.Equal("Detected", media.mediaDetectionStatus);
        Assert.Contains("traditional hard drive", media.mediaDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsSsdForSingleSsdPhysicalDisk()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([4], physicalDiskCount: 1, timedOut: false, unsupported: false);

        Assert.Equal("SSD", media.mediaType);
        Assert.Equal("Detected", media.mediaDetectionStatus);
        Assert.Contains("TRIM", media.mediaDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsMixedForConflictingPhysicalDisks()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3, 4], physicalDiskCount: 2, timedOut: false, unsupported: false);

        Assert.Equal("Mixed", media.mediaType);
        Assert.Equal("Mixed", media.mediaDetectionStatus);
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsUnknownForMultiDiskSameMediaVolume()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3, 3], physicalDiskCount: 2, timedOut: false, unsupported: false);

        Assert.Equal("Unknown", media.mediaType);
        Assert.Equal("Unknown", media.mediaDetectionStatus);
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsUnknownWhenOnlyOneOfMultipleDisksResolves()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3], physicalDiskCount: 2, timedOut: false, unsupported: false);

        Assert.Equal("Unknown", media.mediaType);
        Assert.Equal("Unknown", media.mediaDetectionStatus);
    }

    [Fact]
    public void ClassifyMediaTypes_ReturnsUnknownForTimeout()
    {
        DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([], physicalDiskCount: 0, timedOut: true, unsupported: false);

        Assert.Equal("Unknown", media.mediaType);
        Assert.Equal("TimedOut", media.mediaDetectionStatus);
    }
}

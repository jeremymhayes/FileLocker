using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace FileLocker.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task ValidateInstallerTrustDetailedAsync_AllowsUnsignedInstallerWhenDigestMatches()
    {
        string installerPath = GetBuiltFileLockerExePath();
        string digest = await ComputeSha256HexAsync(installerPath);

        InstallerTrustResult result = await UpdateService.ValidateInstallerTrustDetailedAsync(
            installerPath,
            digest,
            CancellationToken.None);

        Assert.True(result.IsTrusted, result.Message);
        Assert.True(result.DigestVerified);
        Assert.True(result.UsedDigestFallback);
    }

    [Fact]
    public async Task ValidateInstallerTrustDetailedAsync_RejectsUnsignedInstallerWithoutDigest()
    {
        string installerPath = GetBuiltFileLockerExePath();

        InstallerTrustResult result = await UpdateService.ValidateInstallerTrustDetailedAsync(
            installerPath,
            expectedSha256Hex: null,
            CancellationToken.None);

        Assert.False(result.IsTrusted);
        Assert.False(result.DigestVerified);
        Assert.False(result.UsedDigestFallback);
    }

    [Fact]
    public async Task ValidateInstallerTrustDetailedAsync_RejectsDigestMismatch()
    {
        string installerPath = GetBuiltFileLockerExePath();
        string wrongDigest = new('0', 64);

        InstallerTrustResult result = await UpdateService.ValidateInstallerTrustDetailedAsync(
            installerPath,
            wrongDigest,
            CancellationToken.None);

        Assert.False(result.IsTrusted);
        Assert.False(result.DigestVerified);
    }

    [Fact]
    public async Task ValidateInstallerTrustDetailedAsync_RejectsNullFilePath()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            UpdateService.ValidateInstallerTrustDetailedAsync(
                null!,
                expectedSha256Hex: null,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateInstallerTrustDetailedAsync_RejectsBlankFilePath(string filePath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            UpdateService.ValidateInstallerTrustDetailedAsync(
                filePath,
                expectedSha256Hex: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryExtractSha256DigestFromText_ReadsInstallerSpecificSidecarLine()
    {
        string digest = new('A', 64);
        string text = $"""
            {new string('B', 64)}  OtherInstaller.exe
            {digest}  FileLocker-Setup-1.2.3.exe
            """;

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            text,
            "FileLocker-Setup-1.2.3.exe",
            out string normalizedDigest);

        Assert.True(extracted);
        Assert.Equal(digest, normalizedDigest);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_ReadsSha256Prefix()
    {
        string digest = new('C', 64);

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            $"sha256:{digest}",
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.True(extracted);
        Assert.Equal(digest, normalizedDigest);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_ReadsSha256HyphenPrefix()
    {
        string digest = new('D', 64);

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            $"SHA-256: {digest}",
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.True(extracted);
        Assert.Equal(digest, normalizedDigest);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_RejectsAmbiguousFallbackDigests()
    {
        string firstDigest = new('A', 64);
        string secondDigest = new('B', 64);
        string text = $"""
            {firstDigest}  OtherInstaller.exe
            {secondDigest}  AnotherInstaller.exe
            """;

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            text,
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.False(extracted);
        Assert.Equal(string.Empty, normalizedDigest);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_RejectsSingleDigestForDifferentInstallerName()
    {
        string digest = new('A', 64);

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            $"{digest}  OtherFileLocker-Setup.exe",
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.False(extracted);
        Assert.Equal(string.Empty, normalizedDigest);
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsNullRelease()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            UpdateService.DownloadInstallerAsync(
                null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CreateInstallerCleanupStartInfo_UsesWaitAndDeleteCommand()
    {
        string installerPath = Path.Combine(Path.GetTempPath(), "FileLocker Test Installer.exe");

        ProcessStartInfo startInfo = UpdateService.CreateInstallerCleanupStartInfo(
            installerPath,
            TimeSpan.FromSeconds(2));

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), startInfo.FileName);
        Assert.Contains("timeout /t 2 /nobreak", startInfo.Arguments);
        Assert.Contains("start /wait", startInfo.Arguments);
        Assert.Contains("del /f /q", startInfo.Arguments);
        Assert.Equal(installerPath, startInfo.Environment["FILELOCKER_UPDATER_INSTALLER_PATH"]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateInstallerCleanupStartInfo_NormalizesInstallerPath()
    {
        string relativeInstallerPath = Path.Combine(".", "FileLocker Test Installer.exe");

        ProcessStartInfo startInfo = UpdateService.CreateInstallerCleanupStartInfo(
            $"  {relativeInstallerPath}  ",
            TimeSpan.Zero);

        Assert.Equal(
            Path.GetFullPath(relativeInstallerPath),
            startInfo.Environment["FILELOCKER_UPDATER_INSTALLER_PATH"]);
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsBlankPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath(" "));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is required.", ex.Message);
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsMalformedPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath("C:\\Temp\\bad\0installer.exe"));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Theory]
    [InlineData("  1.2.3  ", "1.2.3")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeSettings_CleansSkippedVersion(string? skippedVersion, string? expected)
    {
        var settings = new UpdateSettings { SkippedVersion = skippedVersion };

        UpdateSettings normalized = UpdateService.NormalizeSettings(settings);

        Assert.Equal(expected, normalized.SkippedVersion);
    }

    [Fact]
    public void CleanupStaleDownloadFiles_MissingDirectoryIsNoOp()
    {
        string missingDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Missing-{Guid.NewGuid():N}");

        UpdateService.CleanupStaleDownloadFiles(missingDirectory);
    }

    [Fact]
    public void CleanupStaleDownloadFiles_RemovesReadOnlyDownloads()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string downloadPath = Path.Combine(testDirectory, $"FileLocker-Setup-Current.exe.{Guid.NewGuid():N}.download");
        File.WriteAllText(downloadPath, "partial");
        File.SetAttributes(downloadPath, File.GetAttributes(downloadPath) | FileAttributes.ReadOnly);

        try
        {
            UpdateService.CleanupStaleDownloadFiles(testDirectory);

            Assert.False(File.Exists(downloadPath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void CleanupStaleDownloadFiles_PreservesUnrelatedDownloads()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string unrelatedDownloadPath = Path.Combine(testDirectory, $"OtherTool.exe.{Guid.NewGuid():N}.download");
        File.WriteAllText(unrelatedDownloadPath, "partial");

        try
        {
            UpdateService.CleanupStaleDownloadFiles(testDirectory);

            Assert.True(File.Exists(unrelatedDownloadPath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void CleanupOlderInstallers_KeepsCurrentInstallerAndRemovesOlderReadOnlyInstaller()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string currentInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Current.exe");
        string olderInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Old.exe");
        File.WriteAllText(currentInstallerPath, "current");
        File.WriteAllText(olderInstallerPath, "old");
        File.SetAttributes(olderInstallerPath, File.GetAttributes(olderInstallerPath) | FileAttributes.ReadOnly);

        try
        {
            UpdateService.CleanupOlderInstallers(testDirectory, currentInstallerPath);

            Assert.True(File.Exists(currentInstallerPath));
            Assert.False(File.Exists(olderInstallerPath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void CleanupOlderInstallers_KeepsWhitespacePaddedCurrentInstallerPath()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string currentInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Current.exe");
        string olderInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Old.exe");
        File.WriteAllText(currentInstallerPath, "current");
        File.WriteAllText(olderInstallerPath, "old");

        try
        {
            UpdateService.CleanupOlderInstallers(testDirectory, $"  {currentInstallerPath}  ");

            Assert.True(File.Exists(currentInstallerPath));
            Assert.False(File.Exists(olderInstallerPath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void CleanupOlderInstallers_RemovesOwnedInstallersWhenCurrentPathIsMalformed()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string olderInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Old.exe");
        File.WriteAllText(olderInstallerPath, "old");

        try
        {
            UpdateService.CleanupOlderInstallers(testDirectory, "C:\\Temp\\bad\0installer.exe");

            Assert.False(File.Exists(olderInstallerPath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void CleanupOlderInstallers_PreservesUnrelatedExecutables()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string currentInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Current.exe");
        string olderInstallerPath = Path.Combine(testDirectory, "FileLocker-Setup-Old.exe");
        string unrelatedExecutablePath = Path.Combine(testDirectory, "OtherTool.exe");
        File.WriteAllText(currentInstallerPath, "current");
        File.WriteAllText(olderInstallerPath, "old");
        File.WriteAllText(unrelatedExecutablePath, "unrelated");

        try
        {
            UpdateService.CleanupOlderInstallers(testDirectory, currentInstallerPath);

            Assert.True(File.Exists(currentInstallerPath));
            Assert.False(File.Exists(olderInstallerPath));
            Assert.True(File.Exists(unrelatedExecutablePath));
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void ReplaceDownloadedInstaller_ReplacesReadOnlyExistingInstaller()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string tempPath = Path.Combine(testDirectory, "installer.download");
        string installerPath = Path.Combine(testDirectory, "FileLocker-Setup.exe");
        File.WriteAllText(tempPath, "new");
        File.WriteAllText(installerPath, "old");
        File.SetAttributes(installerPath, File.GetAttributes(installerPath) | FileAttributes.ReadOnly);

        try
        {
            UpdateService.ReplaceDownloadedInstaller(tempPath, installerPath);

            Assert.False(File.Exists(tempPath));
            Assert.Equal("new", File.ReadAllText(installerPath));
            Assert.False((File.GetAttributes(installerPath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Fact]
    public void ReplaceDownloadedInstaller_RestoresReadOnlyAttributeWhenReplaceFails()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string tempPath = Path.Combine(testDirectory, "installer.download");
        string installerPath = Path.Combine(testDirectory, "FileLocker-Setup.exe");
        File.WriteAllText(tempPath, "new");
        File.WriteAllText(installerPath, "old");
        File.SetAttributes(installerPath, File.GetAttributes(installerPath) | FileAttributes.ReadOnly);

        try
        {
            using var lockStream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Exception? ex = Record.Exception(() => UpdateService.ReplaceDownloadedInstaller(tempPath, installerPath));

            Assert.True(ex is IOException or UnauthorizedAccessException);
            Assert.True((File.GetAttributes(installerPath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
        }
        finally
        {
            DeleteDirectoryIfExists(testDirectory);
        }
    }

    [Theory]
    [InlineData("CON.exe")]
    [InlineData("nul.exe")]
    [InlineData("COM1.exe")]
    [InlineData("LPT9.exe")]
    public void TryGetSafeInstallerFileName_RejectsReservedWindowsDeviceNames(string fileName)
    {
        bool accepted = InvokeTryGetSafeInstallerFileName(fileName, out string safeFileName);

        Assert.False(accepted);
        Assert.Equal(string.Empty, safeFileName);
    }

    [Fact]
    public void TryGetSafeInstallerFileName_AcceptsNormalInstallerName()
    {
        bool accepted = InvokeTryGetSafeInstallerFileName("FileLocker-Setup.exe", out string safeFileName);

        Assert.True(accepted);
        Assert.Equal("FileLocker-Setup.exe", safeFileName);
    }

    [Fact]
    public void StartInstallerAndDeleteWhenClosed_RejectsMissingInstallerPath()
    {
        string installerPath = Path.Combine(Path.GetTempPath(), $"FileLocker-Missing-{Guid.NewGuid():N}.exe");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() =>
            UpdateService.StartInstallerAndDeleteWhenClosed(installerPath, TimeSpan.Zero));

        Assert.Equal(installerPath, ex.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StartInstallerAndDeleteWhenClosed_RejectsBlankInstallerPath(string installerPath)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.StartInstallerAndDeleteWhenClosed(installerPath, TimeSpan.Zero));

        Assert.Contains("Installer path is required", ex.Message);
    }

    [Fact]
    public async Task StartInstallerAndDeleteWhenClosed_DeletesInstallerAfterProcessExits()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        string sourceExecutablePath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        string installerPath = Path.Combine(testDirectory, "FileLocker Fake Installer.exe");
        File.Copy(sourceExecutablePath, installerPath);

        try
        {
            using Process process = UpdateService.StartInstallerAndDeleteWhenClosed(installerPath, TimeSpan.Zero);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(installerPath), "Expected the installer cleanup process to delete the installer.");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartInstallerAndDeleteWhenClosed_TrimsInstallerPathBeforeLaunch()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"FileLocker-Updater-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        string sourceExecutablePath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        string installerPath = Path.Combine(testDirectory, "FileLocker Padded Installer.exe");
        File.Copy(sourceExecutablePath, installerPath);

        try
        {
            using Process process = UpdateService.StartInstallerAndDeleteWhenClosed($"  {installerPath}  ", TimeSpan.Zero);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(installerPath), "Expected the normalized installer path to be launched and deleted.");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static string GetBuiltFileLockerExePath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "FileLocker.exe");
        Assert.True(File.Exists(path), $"Expected test output to include {path}.");
        return path;
    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(digest);
    }

    private static bool InvokeTryGetSafeInstallerFileName(string rawFileName, out string safeFileName)
    {
        MethodInfo method = typeof(UpdateService).GetMethod("TryGetSafeInstallerFileName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryGetSafeInstallerFileName method not found.");
        object?[] args = [rawFileName, string.Empty];
        bool accepted = (bool)(method.Invoke(null, args) ?? false);
        safeFileName = (string)(args[1] ?? string.Empty);
        return accepted;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}

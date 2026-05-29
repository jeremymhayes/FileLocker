using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;

namespace FileLocker.Tests;

public sealed class UpdateServiceTests
{
    private static readonly object UpdateSettingsTestLock = new();

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
    public async Task ValidateInstallerTrustDetailedAsync_RejectsRelativeFilePath()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            UpdateService.ValidateInstallerTrustDetailedAsync(
                "FileLocker-Setup.exe",
                expectedSha256Hex: null,
                TestContext.Current.CancellationToken));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
    }

    [Fact]
    public void TryGetVersionFromExecutablePath_RejectsDriveRelativePath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");

        Assert.False(UpdateService.TryGetVersionFromExecutablePath($"{root[0]}:FileLocker.exe", out _));
    }

    [Fact]
    public void TryGetVersionFromExecutablePath_RejectsUnicodeFormatCharacterPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "FileLocker" + "\u202E" + ".exe");

        Assert.False(UpdateService.TryGetVersionFromExecutablePath(path, out _));
    }

    [Fact]
    public void TryGetVersionFromExecutablePath_RejectsAlternateDataStreamPath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string path = Path.Combine(root, "FileLocker.Tests:ads", "FileLocker.exe");

        Assert.False(UpdateService.TryGetVersionFromExecutablePath(path, out _));
    }

    [Fact]
    public void NormalizeSettings_RejectsNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => UpdateService.NormalizeSettings(null!));
    }

    [Fact]
    public void NormalizeSettings_TrimsSkippedVersion()
    {
        UpdateSettings settings = UpdateService.NormalizeSettings(new UpdateSettings
        {
            SkippedVersion = "  1.2.3  "
        });

        Assert.Equal("1.2.3", settings.SkippedVersion);
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
    public void TryExtractSha256DigestFromText_RejectsOversizedText()
    {
        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            new string('A', UpdateService.MaxDigestTextChars + 1),
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.False(extracted);
        Assert.Equal(string.Empty, normalizedDigest);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_IgnoresOversizedLines()
    {
        string digest = new('E', 64);
        string text = $"""
            {new string('X', UpdateService.MaxDigestLineChars + 1)}
            {digest}  FileLocker-Setup.exe
            """;

        bool extracted = UpdateService.TryExtractSha256DigestFromText(
            text,
            "FileLocker-Setup.exe",
            out string normalizedDigest);

        Assert.True(extracted);
        Assert.Equal(digest, normalizedDigest);
    }

    [Fact]
    public void ValidateFinalDigestResponse_RejectsHttpFinalUri()
    {
        using HttpResponseMessage response = CreateDigestResponse(
            "http://github.com/jeremymhayes/FileLocker/releases/download/v1/hash.sha256",
            contentLength: 64);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            UpdateService.ValidateFinalDigestResponse(response));

        Assert.Equal("The release digest download did not finish over HTTPS.", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public void ValidateFinalDigestResponse_RejectsInvalidContentLength(long contentLength)
    {
        using HttpResponseMessage response = CreateDigestResponse(
            "https://github.com/jeremymhayes/FileLocker/releases/download/v1/hash.sha256",
            contentLength);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            UpdateService.ValidateFinalDigestResponse(response));

        Assert.Equal("The release SHA-256 digest file was empty or too large.", ex.Message);
    }

    [Fact]
    public void ValidateFinalDigestResponse_AllowsHttpsDigestWithBoundedLength()
    {
        using HttpResponseMessage response = CreateDigestResponse(
            "https://github.com/jeremymhayes/FileLocker/releases/download/v1/hash.sha256",
            contentLength: 64);

        UpdateService.ValidateFinalDigestResponse(response);
    }

    [Fact]
    public async Task DeserializeJsonResponseWithLimitAsync_ReadsBoundedMetadata()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"release"}""")
        };

        Dictionary<string, string>? metadata = await UpdateService.DeserializeJsonResponseWithLimitAsync<Dictionary<string, string>>(
            response,
            "GitHub release metadata",
            TestContext.Current.CancellationToken);

        Assert.NotNull(metadata);
        Assert.Equal("release", metadata!["name"]);
    }

    [Fact]
    public async Task DeserializeJsonResponseWithLimitAsync_RejectsOversizedMetadata()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string(' ', (int)UpdateService.MaxReleaseMetadataBytes + 1))
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateService.DeserializeJsonResponseWithLimitAsync<Dictionary<string, string>>(
                response,
                "GitHub release metadata",
                TestContext.Current.CancellationToken));

        Assert.Contains("GitHub release metadata exceeded", ex.Message);
    }

    [Fact]
    public void TryCreateExpectedGitHubDownloadUri_AcceptsRepositoryReleaseAssetUrl()
    {
        bool accepted = InvokeTryCreateExpectedGitHubDownloadUri(
            "https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe",
            out Uri uri);

        Assert.True(accepted);
        Assert.Equal("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe", uri.ToString());
    }

    [Theory]
    [InlineData("https://user:token@github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe")]
    [InlineData("https://github.com:444/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe")]
    [InlineData("https://github.com/other/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe?token=abc")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe#download")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe\r\nhttps://example.test")]
    [InlineData("https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/FileLocker-Setup.exe\u202E")]
    public void TryCreateExpectedGitHubDownloadUri_RejectsUnexpectedUrlForms(string url)
    {
        bool accepted = InvokeTryCreateExpectedGitHubDownloadUri(url, out Uri uri);

        Assert.False(accepted);
        Assert.Null(uri);
    }

    [Fact]
    public void TryCreateExpectedGitHubDownloadUri_RejectsOversizedUrls()
    {
        string url = $"https://github.com/jeremymhayes/FileLocker/releases/download/v1.2.3/{new string('a', UpdateService.MaxReleaseAssetUrlChars)}.exe";

        bool accepted = InvokeTryCreateExpectedGitHubDownloadUri(url, out Uri uri);

        Assert.False(accepted);
        Assert.Null(uri);
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
    public void CreateInstallerCleanupStartInfo_TrimsInstallerPath()
    {
        string installerPath = Path.Combine(Path.GetTempPath(), "FileLocker Test Installer.exe");

        ProcessStartInfo startInfo = UpdateService.CreateInstallerCleanupStartInfo(
            $"  {installerPath}  ",
            TimeSpan.Zero);

        Assert.Equal(installerPath, startInfo.Environment["FILELOCKER_UPDATER_INSTALLER_PATH"]);
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
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsControlCharacters()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath("C:\\Temp\\installer.exe\r\nextra"));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsRelativePath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath("FileLocker-Setup.exe"));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsUnicodeFormatCharacters()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath("C:\\Temp\\installer.exe\u202E"));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
    }

    [Fact]
    public void NormalizeInstallerPath_RejectsAlternateDataStreamPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UpdateService.NormalizeInstallerPath(Path.Combine(Path.GetTempPath(), "FileLocker-Setup.exe:stream")));

        Assert.Equal("installerPath", ex.ParamName);
        Assert.Contains("Installer path is invalid.", ex.Message);
    }

    [Theory]
    [InlineData("  1.2.3  ", "1.2.3")]
    [InlineData(" v1.2.3+build.5 ", "1.2.3")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("not-a-version", null)]
    public void NormalizeSettings_CleansSkippedVersion(string? skippedVersion, string? expected)
    {
        var settings = new UpdateSettings { SkippedVersion = skippedVersion };

        UpdateSettings normalized = UpdateService.NormalizeSettings(settings);

        Assert.Equal(expected, normalized.SkippedVersion);
    }

    [Fact]
    public void NormalizeReleaseNotes_TrimsAndCapsText()
    {
        string notes = $"  {new string('A', 20 * 1024)}  ";

        string normalized = UpdateService.NormalizeReleaseNotes(notes);

        Assert.True(normalized.Length <= 16 * 1024);
        Assert.EndsWith("Release notes truncated.", normalized);
    }

    [Fact]
    public void NormalizeReleaseNotes_ReplacesUnicodeFormatCharacters()
    {
        string normalized = UpdateService.NormalizeReleaseNotes("  Fixed\u202EUpdater  ");

        Assert.Equal("Fixed Updater", normalized);
    }

    [Fact]
    public void LoadSettings_ReturnsDefaultsForOversizedSettingsFile()
    {
        lock (UpdateSettingsTestLock)
        {
            string settingsPath = InvokeGetSettingsPath();
            byte[]? originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, new string(' ', 64 * 1024 + 1));

                UpdateSettings settings = UpdateService.LoadSettings();

                Assert.True(settings.AutoCheckEnabled);
                Assert.Null(settings.LastCheckedUtc);
                Assert.Null(settings.SkippedVersion);
            }
            finally
            {
                RestoreFile(settingsPath, originalSettings);
            }
        }
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
    public void CleanupStaleDownloadFiles_RelativeDirectoryIsNoOp()
    {
        string relativeDirectory = $"FileLocker-Updater-Relative-{Guid.NewGuid():N}";
        string fullDirectory = Path.GetFullPath(relativeDirectory);
        Directory.CreateDirectory(fullDirectory);
        string downloadPath = Path.Combine(fullDirectory, $"FileLocker-Setup-Current.exe.{Guid.NewGuid():N}.download");
        File.WriteAllText(downloadPath, "partial");

        try
        {
            UpdateService.CleanupStaleDownloadFiles(relativeDirectory);

            Assert.True(File.Exists(downloadPath));
        }
        finally
        {
            DeleteDirectoryIfExists(fullDirectory);
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
    public void CleanupOlderInstallers_RelativeDirectoryIsNoOp()
    {
        string relativeDirectory = $"FileLocker-Updater-Relative-{Guid.NewGuid():N}";
        string fullDirectory = Path.GetFullPath(relativeDirectory);
        Directory.CreateDirectory(fullDirectory);
        string currentInstallerPath = Path.Combine(fullDirectory, "FileLocker-Setup-Current.exe");
        string olderInstallerPath = Path.Combine(fullDirectory, "FileLocker-Setup-Old.exe");
        File.WriteAllText(currentInstallerPath, "current");
        File.WriteAllText(olderInstallerPath, "old");

        try
        {
            UpdateService.CleanupOlderInstallers(relativeDirectory, currentInstallerPath);

            Assert.True(File.Exists(currentInstallerPath));
            Assert.True(File.Exists(olderInstallerPath));
        }
        finally
        {
            DeleteDirectoryIfExists(fullDirectory);
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
    [InlineData("FileLocker-Setup.exe")]
    [InlineData("FileLocker-Setup-1.2.3.exe")]
    [InlineData(" FileLocker-Setup_1.2.3.exe ")]
    public void UpdaterFileNameRules_AcceptsOwnedInstallerNames(string fileName)
    {
        Assert.True(UpdaterFileNameRules.IsOwnedInstallerFileName(fileName));
    }

    [Fact]
    public void UpdaterFileNameRules_AcceptsOwnedDownloadNames()
    {
        Assert.True(UpdaterFileNameRules.IsOwnedDownloadFileName("FileLocker-Setup.exe.0123456789abcdef.download"));
    }

    [Theory]
    [InlineData("FileLocker-Setup-\r.exe")]
    [InlineData("FileLocker-Setup-\u202E.exe")]
    [InlineData("FileLocker-Setup.exe:stream")]
    [InlineData("FileLocker-Setup/evil.exe")]
    [InlineData("FileLocker-Setup\\evil.exe")]
    [InlineData("FileLocker-Setup?.exe")]
    [InlineData(".")]
    [InlineData("..")]
    public void UpdaterFileNameRules_RejectsInvalidOwnedLookingNames(string fileName)
    {
        Assert.False(UpdaterFileNameRules.IsOwnedInstallerFileName(fileName));
        Assert.False(UpdaterFileNameRules.IsOwnedDownloadFileName(fileName));
    }

    [Fact]
    public void UpdaterFileNameRules_RejectsOversizedNames()
    {
        string fileName = $"FileLocker-Setup-{new string('A', 256)}.exe";

        Assert.False(UpdaterFileNameRules.IsOwnedInstallerFileName(fileName));
        Assert.False(UpdaterFileNameRules.IsOwnedDownloadFileName(fileName));
    }

    [Theory]
    [InlineData("CON.exe")]
    [InlineData("nul.exe")]
    [InlineData("COM1.exe")]
    [InlineData("LPT9.exe")]
    [InlineData("OtherTool.exe")]
    [InlineData("FileLocker-Portable.exe")]
    [InlineData("FileLocker-SetupEvil.exe")]
    [InlineData("FileLocker-Setupper.exe")]
    [InlineData("folder/FileLocker-Setup.exe")]
    [InlineData("folder\\FileLocker-Setup.exe")]
    [InlineData("FileLocker-Setup?.exe")]
    [InlineData("FileLocker-Setup-\u202E.exe")]
    public void TryGetSafeInstallerFileName_RejectsUnsafeOrUnexpectedNames(string fileName)
    {
        bool accepted = InvokeTryGetSafeInstallerFileName(fileName, out string safeFileName);

        Assert.False(accepted);
        Assert.Equal(string.Empty, safeFileName);
    }

    [Fact]
    public void TryGetSafeInstallerFileName_RejectsOversizedNames()
    {
        bool accepted = InvokeTryGetSafeInstallerFileName(
            $"FileLocker-Setup-{new string('A', UpdateService.MaxInstallerFileNameChars)}.exe",
            out string safeFileName);

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

    private static HttpResponseMessage CreateDigestResponse(string requestUri, long contentLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
            Content = new ByteArrayContent([])
        };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static bool InvokeTryCreateExpectedGitHubDownloadUri(string rawUrl, out Uri uri)
    {
        MethodInfo method = typeof(UpdateService).GetMethod("TryCreateExpectedGitHubDownloadUri", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryCreateExpectedGitHubDownloadUri method not found.");
        object?[] args = [rawUrl, null];
        bool accepted = (bool)(method.Invoke(null, args) ?? false);
        uri = (Uri?)args[1]!;
        return accepted;
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

    private static string InvokeGetSettingsPath()
    {
        MethodInfo method = typeof(UpdateService).GetMethod("GetSettingsPath", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GetSettingsPath method not found.");
        return (string)(method.Invoke(null, []) ?? throw new InvalidOperationException("GetSettingsPath returned null."));
    }

    private static void RestoreFile(string path, byte[]? originalBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (originalBytes == null)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllBytes(path, originalBytes);
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

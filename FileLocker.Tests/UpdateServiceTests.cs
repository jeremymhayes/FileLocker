using System.Diagnostics;

namespace FileLocker.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("1.3.0.0", "1.3.0.0")]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("  1.2.3.4  ", "1.2.3.4")]
    public void NormalizeSkippedVersion_AcceptsInstallerVersionValues(string value, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeSkippedVersion(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3\u202E")]
    public void NormalizeSkippedVersion_RejectsInvalidValues(string value)
    {
        Assert.Null(UpdateService.NormalizeSkippedVersion(value));
    }

    [Fact]
    public void NormalizeSettings_NormalizesSkippedVersionAndUtcTimestamp()
    {
        var localTime = new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.FromHours(-5));
        var settings = new UpdateSettings
        {
            AutoCheckEnabled = false,
            LastCheckedUtc = localTime,
            SkippedVersion = "v1.3.0.0"
        };

        UpdateSettings normalized = UpdateService.NormalizeSettings(settings);

        Assert.False(normalized.AutoCheckEnabled);
        Assert.Equal("1.3.0.0", normalized.SkippedVersion);
        Assert.Equal(TimeSpan.Zero, normalized.LastCheckedUtc?.Offset);
        Assert.Equal(localTime.UtcDateTime, normalized.LastCheckedUtc?.UtcDateTime);
    }

    [Fact]
    public void NormalizeReleaseNotes_RemovesUnsafeFormattingCharacters()
    {
        string normalized = UpdateService.NormalizeReleaseNotes("  Fixed\u202EUpdater\r\nNext\tLine  ");

        Assert.Equal("FixedUpdater\r\nNext\tLine", normalized);
    }

    [Fact]
    public void NormalizeReleaseNotes_ReturnsFallbackForBlankNotes()
    {
        Assert.Equal(
            "No release notes were provided for this release.",
            UpdateService.NormalizeReleaseNotes("   "));
    }

    [Fact]
    public void NormalizeReleaseNotes_CapsLongNotes()
    {
        string normalized = UpdateService.NormalizeReleaseNotes(new string('A', UpdateService.MaxReleaseNotesChars + 100));

        Assert.Equal(UpdateService.MaxReleaseNotesChars, normalized.Length);
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
    public void TryGetVersionFromExecutablePath_ReadsExistingExecutableVersion()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process path was not available.");

        Assert.True(UpdateService.TryGetVersionFromExecutablePath(executablePath, out Version version));
        Assert.True(version.Major >= 0);
    }

    [Fact]
    public void CreateInstallerCleanupStartInfo_UsesInnoSilentArgumentsAndRelaunch()
    {
        string installerPath = Path.Combine(Path.GetTempPath(), "FileLocker-Setup-1.3.0.0.exe");
        string relaunchPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process path was not available.");

        ProcessStartInfo startInfo = UpdateService.CreateInstallerCleanupStartInfo(
            installerPath,
            TimeSpan.FromMilliseconds(25),
            processIdToWaitFor: 1234,
            relaunchExecutablePath: relaunchPath);

        string command = DecodeEncodedPowerShellCommand(startInfo.Arguments);

        Assert.Equal(installerPath, startInfo.Environment["FILELOCKER_UPDATER_INSTALLER_PATH"]);
        Assert.Equal("25", startInfo.Environment["FILELOCKER_UPDATER_STARTUP_DELAY_MS"]);
        Assert.Equal("1234", startInfo.Environment["FILELOCKER_UPDATER_WAIT_PID"]);
        Assert.Equal(relaunchPath, startInfo.Environment["FILELOCKER_UPDATER_RELAUNCH_PATH"]);
        Assert.Contains("/VERYSILENT", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/SUPPRESSMSGBOXES", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/NORESTART", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/LOG=", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-Item -LiteralPath $installer", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_ExtractsMatchingInstallerDigest()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string text = $"{digest}  FileLocker-Setup-1.3.0.0.exe";

        bool parsed = UpdateService.TryExtractSha256DigestFromText(
            text,
            "FileLocker-Setup-1.3.0.0.exe",
            out string actual);

        Assert.True(parsed);
        Assert.Equal(digest, actual);
    }

    [Fact]
    public void TryExtractSha256DigestFromText_IgnoresMismatchedInstallerLine()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string text = $"{digest}  FileLocker-Setup-1.2.1.0.exe\nFileLocker-Setup-1.3.0.0.exe";

        Assert.False(UpdateService.TryExtractSha256DigestFromText(
            text,
            "FileLocker-Setup-1.3.0.0.exe",
            out _));
    }

    [Fact]
    public void TryCreateReleaseInfo_RequiresExactVersionedInstallerAsset()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.3.0.0",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "FileLocker-Setup-1.2.1.0.exe",
                    BrowserDownloadUrl = "https://github.com/jeremymhayes/FileLocker/releases/download/v1.3.0.0/FileLocker-Setup-1.2.1.0.exe",
                    Size = 1024
                }
            ]
        };

        bool created = UpdateService.TryCreateReleaseInfo(release, out UpdateReleaseInfo? releaseInfo, out string failureReason);

        Assert.False(created);
        Assert.Null(releaseInfo);
        Assert.Contains("FileLocker-Setup-1.3.0.0.exe", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreateReleaseInfo_UsesInstallerAndDigestSidecar()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.3.0.0",
            HtmlUrl = "https://github.com/jeremymhayes/FileLocker/releases/tag/v1.3.0.0",
            Body = "Release notes",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "FileLocker-Setup-1.3.0.0.exe",
                    BrowserDownloadUrl = "https://github.com/jeremymhayes/FileLocker/releases/download/v1.3.0.0/FileLocker-Setup-1.3.0.0.exe",
                    Size = 1024
                },
                new GitHubReleaseAsset
                {
                    Name = "FileLocker-Setup-1.3.0.0.exe.sha256",
                    BrowserDownloadUrl = "https://github.com/jeremymhayes/FileLocker/releases/download/v1.3.0.0/FileLocker-Setup-1.3.0.0.exe.sha256",
                    Size = 96
                }
            ]
        };

        bool created = UpdateService.TryCreateReleaseInfo(release, out UpdateReleaseInfo? releaseInfo, out string failureReason);

        Assert.True(created, failureReason);
        Assert.NotNull(releaseInfo);
        Assert.Equal("1.3.0.0", releaseInfo.DisplayVersion);
        Assert.Equal("FileLocker-Setup-1.3.0.0.exe", releaseInfo.InstallerFileName);
        Assert.EndsWith("FileLocker-Setup-1.3.0.0.exe.sha256", releaseInfo.Sha256DigestDownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCurrentVersionLabel_ReturnsSemVerLabel()
    {
        string version = UpdateService.GetCurrentVersionLabel();

        Assert.Matches(@"^\d+\.\d+(\.\d+)?(\.\d+)?$", version);
    }

    private static string DecodeEncodedPowerShellCommand(string arguments)
    {
        string prefix = "-EncodedCommand ";
        int index = arguments.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0, "PowerShell helper arguments should use -EncodedCommand.");

        string encoded = arguments[(index + prefix.Length)..].Trim();
        byte[] bytes = Convert.FromBase64String(encoded);
        return System.Text.Encoding.Unicode.GetString(bytes);
    }
}

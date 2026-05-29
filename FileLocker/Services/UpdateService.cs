using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class UpdateService
{
    private const string Owner = "jeremymhayes";
    private const string Repo = "FileLocker";
    private const string GitHubApiVersion = "2022-11-28";
    private const long MaxInstallerBytes = 250L * 1024L * 1024L;
    private const long MaxDigestBytes = 64L * 1024L;
    private const long MaxUpdateSettingsJsonBytes = 64L * 1024L;
    internal const long MaxReleaseMetadataBytes = 2L * 1024L * 1024L;
    internal const int MaxDigestTextChars = 64 * 1024;
    internal const int MaxDigestLineChars = 4096;
    internal const int MaxSkippedVersionChars = 64;
    internal const int MaxReleaseNotesChars = 16 * 1024;
    internal const int MaxInstallerFileNameChars = 128;

    private const string InstallerPathEnvironmentVariable = "FILELOCKER_UPDATER_INSTALLER_PATH";
    private const string InstallerDelayEnvironmentVariable = "FILELOCKER_UPDATER_STARTUP_DELAY_MS";
    private const string InstallerWaitPidEnvironmentVariable = "FILELOCKER_UPDATER_WAIT_PID";
    private const string InstallerLogPathEnvironmentVariable = "FILELOCKER_UPDATER_INSTALLER_LOG_PATH";
    private const string InstallerRelaunchPathEnvironmentVariable = "FILELOCKER_UPDATER_RELAUNCH_PATH";

    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
    private static readonly Uri ReleasesListUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=10");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string InstallerHelperPowerShellPath = Path.Combine(
        Environment.SystemDirectory,
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    internal static string GitHubRepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    internal static string GitHubLatestReleaseUrl => $"{GitHubRepositoryUrl}/releases/latest";

    internal static UpdateSettings LoadSettings()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new UpdateSettings();
        }

        try
        {
            string json = BoundedFileReader.ReadAllUtf8Text(path, MaxUpdateSettingsJsonBytes);
            return NormalizeSettings(JsonSerializer.Deserialize<UpdateSettings>(json, JsonOptions) ?? new UpdateSettings());
        }
        catch
        {
            return new UpdateSettings();
        }
    }

    internal static void SaveSettings(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string directory = GetUpdaterDataDirectory();
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(NormalizeSettings(settings), JsonOptions);
        FileWriteService.WriteAllTextAtomically(GetSettingsPath(), json, Encoding.UTF8);
    }

    internal static UpdateSettings NormalizeSettings(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SkippedVersion = NormalizeSkippedVersion(settings.SkippedVersion);
        if (settings.LastCheckedUtc.HasValue)
        {
            settings.LastCheckedUtc = settings.LastCheckedUtc.Value.ToUniversalTime();
        }

        return settings;
    }

    internal static string? NormalizeSkippedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string normalized = version.Trim();
        if (normalized.Length > MaxSkippedVersionChars || normalized.Any(ContainsUnsafeFormattingCharacter))
        {
            return null;
        }

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return TryParseVersion(normalized, out Version parsed)
            ? FormatVersion(parsed)
            : null;
    }

    internal static string NormalizeReleaseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "No release notes were provided for this release.";
        }

        var builder = new StringBuilder(Math.Min(notes.Length, MaxReleaseNotesChars));
        foreach (char character in notes.Trim())
        {
            if (builder.Length >= MaxReleaseNotesChars)
            {
                break;
            }

            if (character == '\r' || character == '\n' || character == '\t')
            {
                builder.Append(character);
                continue;
            }

            if (!ContainsUnsafeFormattingCharacter(character))
            {
                builder.Append(character);
            }
        }

        string normalized = builder.ToString().Trim();
        return normalized.Length == 0
            ? "No release notes were provided for this release."
            : normalized;
    }

    internal static string GetCurrentVersionLabel() => FormatVersion(GetCurrentVersion());

    internal static Version GetCurrentVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informationalVersion, out Version version))
        {
            return version;
        }

        if (TryGetVersionFromExecutablePath(Environment.ProcessPath, out version))
        {
            return version;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion ?? new Version(0, 0, 0, 0);
    }

    internal static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        Version currentVersion = GetCurrentVersion();
        GitHubRelease? release = await FetchLatestUsableReleaseAsync(cancellationToken);
        if (release == null)
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                $"FileLocker is up to date ({FormatVersion(currentVersion)}).");
        }

        if (!TryCreateReleaseInfo(release, out UpdateReleaseInfo? releaseInfo, out string failureReason))
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                failureReason.Length == 0
                    ? "GitHub did not publish a compatible FileLocker installer for the latest release."
                    : failureReason);
        }

        Version remoteVersion = ToComparableVersion(releaseInfo!.Version);
        Version localVersion = ToComparableVersion(currentVersion);
        if (remoteVersion <= localVersion)
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                $"FileLocker is up to date ({FormatVersion(currentVersion)}).");
        }

        return new UpdateCheckResult(
            currentVersion,
            releaseInfo,
            true,
            $"FileLocker {releaseInfo.DisplayVersion} is available.");
    }

    internal static async Task<UpdateDownloadResult> DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        UpdateCheckResult result = await CheckForUpdatesAsync(cancellationToken);
        if (!result.IsUpdateAvailable || result.Release == null)
        {
            throw new InvalidOperationException(result.StatusMessage);
        }

        string installerPath = await DownloadInstallerAsync(result.Release, cancellationToken);
        return new UpdateDownloadResult(
            installerPath,
            Path.GetFileName(installerPath),
            result.Release);
    }

    internal static async Task<string> DownloadInstallerAsync(UpdateReleaseInfo release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!TryGetSafeInstallerFileName(release.InstallerFileName, out string safeInstallerFileName))
        {
            throw new InvalidOperationException("The release installer file name was not safe to use locally.");
        }

        if (!TryCreateExpectedGitHubDownloadUri(release.InstallerDownloadUrl, out Uri installerUri))
        {
            throw new InvalidOperationException("The release installer URL was not a valid GitHub HTTPS release download URL.");
        }

        string? expectedDigest = release.Sha256DigestHex;
        if (string.IsNullOrWhiteSpace(expectedDigest) && !string.IsNullOrWhiteSpace(release.Sha256DigestDownloadUrl))
        {
            expectedDigest = await DownloadExpectedDigestAsync(release, cancellationToken);
        }

        if (!TryNormalizeSha256Digest(expectedDigest, out string normalizedDigest))
        {
            throw new InvalidOperationException("The release did not provide a valid SHA-256 digest for the installer.");
        }

        string downloadDirectory = GetUpdateDownloadsDirectory();
        Directory.CreateDirectory(downloadDirectory);
        CleanupStaleDownloadFiles(downloadDirectory);

        string installerPath = Path.Combine(downloadDirectory, safeInstallerFileName);
        if (File.Exists(installerPath) &&
            await FileDigestMatchesAsync(installerPath, normalizedDigest, cancellationToken))
        {
            return installerPath;
        }

        string tempPath = Path.Combine(downloadDirectory, $"{safeInstallerFileName}.{Guid.NewGuid():N}.download");
        try
        {
            using HttpResponseMessage response = await HttpClient.GetAsync(
                installerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();
            ValidateInstallerResponse(response);

            await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var outputStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyToAsyncWithLimit(httpStream, outputStream, MaxInstallerBytes, "installer download", cancellationToken);
            await outputStream.FlushAsync(cancellationToken);

            if (!await FileDigestMatchesAsync(tempPath, normalizedDigest, cancellationToken))
            {
                throw new InvalidOperationException("The downloaded installer did not match the published SHA-256 digest.");
            }

            ReplaceDownloadedInstaller(tempPath, installerPath);
            CleanupOlderInstallers(downloadDirectory, installerPath);
            return installerPath;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    internal static ProcessStartInfo CreateInstallerCleanupStartInfo(
        string installerPath,
        TimeSpan startupDelay,
        int? processIdToWaitFor = null,
        string? relaunchExecutablePath = null)
    {
        string normalizedInstallerPath = NormalizeInstallerPath(installerPath);
        string logPath = CreateInstallerLogPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = InstallerHelperPowerShellPath,
            Arguments = CreateInstallerCleanupCommand(),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment[InstallerPathEnvironmentVariable] = normalizedInstallerPath;
        startInfo.Environment[InstallerDelayEnvironmentVariable] = GetStartupDelayMilliseconds(startupDelay).ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[InstallerLogPathEnvironmentVariable] = logPath;
        if (processIdToWaitFor is > 0)
        {
            startInfo.Environment[InstallerWaitPidEnvironmentVariable] = processIdToWaitFor.Value.ToString(CultureInfo.InvariantCulture);
        }

        string? normalizedRelaunchPath = NormalizeOptionalRelaunchPath(relaunchExecutablePath);
        if (!string.IsNullOrWhiteSpace(normalizedRelaunchPath))
        {
            startInfo.Environment[InstallerRelaunchPathEnvironmentVariable] = normalizedRelaunchPath;
        }

        return startInfo;
    }

    internal static Process StartInstallerAndDeleteWhenClosed(
        string installerPath,
        TimeSpan startupDelay,
        int? processIdToWaitFor = null,
        string? relaunchExecutablePath = null)
    {
        string normalizedInstallerPath = NormalizeInstallerPath(installerPath);
        if (!File.Exists(normalizedInstallerPath))
        {
            throw new FileNotFoundException("The update installer could not be found.", normalizedInstallerPath);
        }

        ProcessStartInfo startInfo = CreateInstallerCleanupStartInfo(
            normalizedInstallerPath,
            startupDelay,
            processIdToWaitFor,
            relaunchExecutablePath);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to launch the update installer helper.");
    }

    internal static string NormalizeInstallerPath(string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new ArgumentException("Installer path is required.", nameof(installerPath));
        }

        string trimmed = installerPath.Trim();
        if (trimmed.Any(ContainsUnsafeFormattingCharacter))
        {
            throw new ArgumentException("Installer path is invalid.", nameof(installerPath));
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                throw new ArgumentException("Installer path is invalid.", nameof(installerPath));
            }

            string fullPath = Path.GetFullPath(trimmed);
            if (ContainsAlternateDataStreamToken(fullPath))
            {
                throw new ArgumentException("Installer path is invalid.", nameof(installerPath));
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Installer path is invalid.", nameof(installerPath), ex);
        }
    }

    internal static bool TryGetVersionFromExecutablePath(string? executablePath, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(executablePath) ||
            executablePath.Any(ContainsUnsafeFormattingCharacter))
        {
            return false;
        }

        try
        {
            string trimmedPath = executablePath.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(trimmedPath);
            if (ContainsAlternateDataStreamToken(fullPath) || !File.Exists(fullPath))
            {
                return false;
            }

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
            string?[] candidates =
            [
                versionInfo.FileVersion,
                versionInfo.ProductVersion
            ];

            foreach (string? candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                Match match = Regex.Match(candidate, @"\d+\.\d+\.\d+(?:\.\d+)?");
                if (match.Success && TryParseVersion(match.Value, out version))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return false;
    }

    internal static string CreateInstallerCleanupCommand()
    {
        string script = string.Join(
            "\n",
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"$installer = $env:{InstallerPathEnvironmentVariable}",
            $"$logPath = $env:{InstallerLogPathEnvironmentVariable}",
            "if ([string]::IsNullOrWhiteSpace($installer) -or -not (Test-Path -LiteralPath $installer -PathType Leaf)) { exit 2 }",
            "if (-not [string]::IsNullOrWhiteSpace($logPath)) {",
            "  $logDir = Split-Path -Parent $logPath",
            "  if (-not [string]::IsNullOrWhiteSpace($logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }",
            "}",
            $"$waitPidText = $env:{InstallerWaitPidEnvironmentVariable}",
            "if ($waitPidText -match '^\\d{1,10}$') {",
            "  $waitPid = [int]$waitPidText",
            "  $deadline = [DateTime]::UtcNow.AddSeconds(90)",
            "  while ([DateTime]::UtcNow -lt $deadline) {",
            "    $running = Get-Process -Id $waitPid -ErrorAction SilentlyContinue",
            "    if ($null -eq $running) { break }",
            "    Start-Sleep -Milliseconds 500",
            "  }",
            "}",
            $"$delayText = $env:{InstallerDelayEnvironmentVariable}",
            "if ($delayText -match '^\\d{1,6}$') {",
            "  $delayMs = [int]$delayText",
            "  if ($delayMs -gt 0) { Start-Sleep -Milliseconds $delayMs }",
            "}",
            "$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')",
            "if (-not [string]::IsNullOrWhiteSpace($logPath)) { $arguments += ('/LOG=\"' + $logPath + '\"') }",
            "$process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru",
            "if ($null -eq $process) { exit 3 }",
            "$exitCode = $process.ExitCode",
            "if ($exitCode -eq 0) {",
            "  Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue",
            $"  $relaunch = $env:{InstallerRelaunchPathEnvironmentVariable}",
            "  if (-not [string]::IsNullOrWhiteSpace($relaunch) -and (Test-Path -LiteralPath $relaunch -PathType Leaf)) {",
            "    Start-Process -FilePath $relaunch | Out-Null",
            "  }",
            "}",
            "exit $exitCode");

        return "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    internal static string GetUpdaterDataDirectory()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker");

        return Path.Combine(appDataDirectory, "Updater");
    }

    internal static string GetUpdateDownloadsDirectory() => Path.Combine(GetUpdaterDataDirectory(), "Downloads");

    internal static string GetUpdateLogsDirectory() => Path.Combine(GetUpdaterDataDirectory(), "Logs");

    internal static string GetSettingsPath() => Path.Combine(GetUpdaterDataDirectory(), "settings.json");

    internal static void CleanupStaleDownloadFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(path);
            if (UpdaterFileNameRules.IsOwnedDownloadFileName(fileName))
            {
                TryDeleteFile(path);
            }
        }
    }

    internal static void CleanupOlderInstallers(string directory, string currentInstallerPath)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(directory) ||
            !Directory.Exists(directory))
        {
            return;
        }

        string normalizedCurrentInstallerPath;
        try
        {
            normalizedCurrentInstallerPath = NormalizeInstallerPath(currentInstallerPath);
        }
        catch
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(path);
            if (UpdaterFileNameRules.IsOwnedInstallerFileName(fileName) &&
                !string.Equals(path, normalizedCurrentInstallerPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(path);
            }
        }
    }

    internal static void ReplaceDownloadedInstaller(string tempPath, string installerPath)
    {
        FileWriteService.ReplaceFileWithTemporaryFile(tempPath, installerPath);
    }

    internal static bool TryExtractSha256DigestFromText(string? digestText, string installerFileName, out string digest)
    {
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(digestText) ||
            digestText.Length > MaxDigestTextChars ||
            string.IsNullOrWhiteSpace(installerFileName))
        {
            return false;
        }

        foreach (string line in digestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > MaxDigestLineChars)
            {
                continue;
            }

            Match match = Regex.Match(line, @"(?<![0-9a-fA-F])([0-9a-fA-F]{64})(?![0-9a-fA-F])");
            if (!match.Success)
            {
                continue;
            }

            bool lineReferencesInstaller = line.Contains(installerFileName, StringComparison.OrdinalIgnoreCase);
            if (!lineReferencesInstaller && digestText.Contains(installerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            digest = match.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        return false;
    }

    private static async Task<GitHubRelease?> FetchLatestUsableReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage latestResponse = await GetGitHubAsync(LatestReleaseUri, cancellationToken);
        GitHubRelease? latestRelease = await DeserializeJsonResponseWithLimitAsync<GitHubRelease>(
            latestResponse,
            MaxReleaseMetadataBytes,
            cancellationToken);

        if (latestRelease != null && TryCreateReleaseInfo(latestRelease, out _, out _))
        {
            return latestRelease;
        }

        using HttpResponseMessage releasesResponse = await GetGitHubAsync(ReleasesListUri, cancellationToken);
        GitHubRelease[]? releases = await DeserializeJsonResponseWithLimitAsync<GitHubRelease[]>(
            releasesResponse,
            MaxReleaseMetadataBytes,
            cancellationToken);

        return releases?
            .Where(release => !release.Draft && !release.Prerelease)
            .FirstOrDefault(release => TryCreateReleaseInfo(release, out _, out _));
    }

    private static async Task<HttpResponseMessage> GetGitHubAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        return await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    internal static bool TryCreateReleaseInfo(GitHubRelease release, out UpdateReleaseInfo? releaseInfo, out string failureReason)
    {
        releaseInfo = null;
        failureReason = string.Empty;

        if (release.Draft || release.Prerelease)
        {
            failureReason = "The latest GitHub release is not a stable published release.";
            return false;
        }

        if (!TryGetReleaseVersion(release, out Version version))
        {
            failureReason = "The latest GitHub release did not use a supported FileLocker version tag.";
            return false;
        }

        string displayVersion = FormatVersion(version);
        string expectedInstallerFileName = $"FileLocker-Setup-{displayVersion}.exe";
        GitHubReleaseAsset? installerAsset = release.Assets
            .FirstOrDefault(asset => asset.Name.Equals(expectedInstallerFileName, StringComparison.OrdinalIgnoreCase));

        if (installerAsset == null)
        {
            failureReason = $"The latest GitHub release does not contain the expected {expectedInstallerFileName} installer asset.";
            return false;
        }

        if (installerAsset.Size > MaxInstallerBytes)
        {
            failureReason = $"The installer asset is too large ({installerAsset.Size:N0} bytes).";
            return false;
        }

        if (!TryGetSafeInstallerFileName(installerAsset.Name, out string installerFileName))
        {
            failureReason = "The installer asset file name is not safe to use locally.";
            return false;
        }

        if (!TryCreateExpectedGitHubDownloadUri(installerAsset.BrowserDownloadUrl, out Uri installerDownloadUri))
        {
            failureReason = "The installer asset download URL is not a valid GitHub HTTPS release download URL.";
            return false;
        }

        string? digest = null;
        string? digestDownloadUrl = null;
        if (TryNormalizeSha256Digest(installerAsset.Digest, out string assetDigest))
        {
            digest = assetDigest;
        }

        GitHubReleaseAsset? digestAsset = release.Assets
            .Where(asset => IsPotentialDigestAsset(asset, installerFileName))
            .OrderByDescending(asset => IsExactDigestAssetName(asset.Name, installerFileName))
            .FirstOrDefault();

        if (digestAsset != null && TryCreateExpectedGitHubDownloadUri(digestAsset.BrowserDownloadUrl, out Uri digestUri))
        {
            digestDownloadUrl = digestUri.ToString();
        }

        releaseInfo = new UpdateReleaseInfo(
            version,
            displayVersion,
            release.TagName,
            string.IsNullOrWhiteSpace(release.HtmlUrl) ? GitHubLatestReleaseUrl : release.HtmlUrl,
            NormalizeReleaseNotes(release.Body),
            installerFileName,
            installerDownloadUri.ToString(),
            digest,
            digestDownloadUrl);

        return true;
    }

    private static bool TryGetReleaseVersion(GitHubRelease release, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        string[] candidates =
        [
            release.TagName,
            release.Name
        ];

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string normalized = candidate.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            {
                normalized = normalized[1..];
            }

            Match match = Regex.Match(normalized, @"\d+\.\d+\.\d+(?:\.\d+)?");
            if (match.Success && TryParseVersion(match.Value, out version))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> DownloadExpectedDigestAsync(UpdateReleaseInfo release, CancellationToken cancellationToken)
    {
        if (!TryCreateExpectedGitHubDownloadUri(release.Sha256DigestDownloadUrl, out Uri digestUri))
        {
            throw new InvalidOperationException("The release SHA-256 digest URL was not a valid GitHub HTTPS release download URL.");
        }

        using HttpResponseMessage response = await HttpClient.GetAsync(digestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        ValidateDigestResponse(response);
        string digestText = await ReadUtf8TextWithLimitAsync(response, MaxDigestBytes, cancellationToken);

        return TryExtractSha256DigestFromText(digestText, release.InstallerFileName, out string digest)
            ? digest
            : throw new InvalidOperationException("The release SHA-256 digest file did not contain a valid digest for the installer.");
    }

    private static void ValidateInstallerResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The installer download did not finish over HTTPS.");
        }

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is <= 0)
        {
            throw new InvalidOperationException("The installer download was empty.");
        }

        if (contentLength > MaxInstallerBytes)
        {
            throw new InvalidOperationException($"The installer is too large ({contentLength.Value:N0} bytes).");
        }
    }

    private static void ValidateDigestResponse(HttpResponseMessage response)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is <= 0)
        {
            throw new InvalidOperationException("The SHA-256 digest download was empty.");
        }

        if (contentLength > MaxDigestBytes)
        {
            throw new InvalidOperationException("The SHA-256 digest file was too large.");
        }
    }

    private static bool TryCreateExpectedGitHubDownloadUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedPrefix = $"/{Owner}/{Repo}/releases/download/";
        if (!parsed.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool TryGetSafeInstallerFileName(string? installerFileName, out string safeInstallerFileName)
    {
        safeInstallerFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(installerFileName))
        {
            return false;
        }

        string fileName = Path.GetFileName(installerFileName.Trim());
        if (!fileName.Equals(installerFileName.Trim(), StringComparison.Ordinal) ||
            fileName.Length > MaxInstallerFileNameChars ||
            !UpdaterFileNameRules.IsOwnedInstallerFileName(fileName))
        {
            return false;
        }

        safeInstallerFileName = fileName;
        return true;
    }

    private static bool IsPotentialDigestAsset(GitHubReleaseAsset asset, string installerFileName)
    {
        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Size > MaxDigestBytes)
        {
            return false;
        }

        string name = asset.Name.Trim();
        return IsExactDigestAssetName(name, installerFileName) ||
            (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(installerFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExactDigestAssetName(string digestAssetName, string installerFileName)
    {
        return digestAssetName.Equals($"{installerFileName}.sha256", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256.txt", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256sum", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256sum.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> DeserializeJsonResponseWithLimitAsync<T>(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await CopyToAsyncWithLimit(stream, memoryStream, maxBytes, "release metadata", cancellationToken);
        memoryStream.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(memoryStream, JsonOptions, cancellationToken);
    }

    private static async Task<string> ReadUtf8TextWithLimitAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await CopyToAsyncWithLimit(stream, memoryStream, maxBytes, "digest download", cancellationToken);
        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private static async Task CopyToAsyncWithLimit(
        Stream source,
        Stream destination,
        long maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"The {label} exceeded the maximum allowed size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<bool> FileDigestMatchesAsync(string path, string expectedSha256Hex, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] digest = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        string actual = Convert.ToHexString(digest).ToLowerInvariant();
        return string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeSha256Digest(string? value, out string digest)
    {
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        const string sha256Prefix = "sha256:";
        if (normalized.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[sha256Prefix.Length..];
        }

        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (Regex.IsMatch(normalized, "^[0-9a-f]{64}$"))
        {
            digest = normalized;
            return true;
        }

        return false;
    }

    private static string CreateInstallerLogPath()
    {
        string logsDirectory = GetUpdateLogsDirectory();
        Directory.CreateDirectory(logsDirectory);
        return Path.Combine(logsDirectory, $"FileLocker-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
    }

    private static string? NormalizeOptionalRelaunchPath(string? relaunchExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(relaunchExecutablePath))
        {
            return null;
        }

        try
        {
            string normalized = NormalizeInstallerPath(relaunchExecutablePath);
            return File.Exists(normalized) ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    private static int GetStartupDelayMilliseconds(TimeSpan startupDelay)
    {
        double delayMilliseconds = Math.Ceiling(Math.Max(0, startupDelay.TotalMilliseconds));
        return (int)Math.Clamp(delayMilliseconds, 0, 60_000);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (!Regex.IsMatch(normalized, @"^\d+\.\d+\.\d+(?:\.\d+)?$") ||
            !Version.TryParse(normalized, out Version? parsed) ||
            parsed.Major < 0 ||
            parsed.Minor < 0)
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));

        return true;
    }

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}";

    private static Version ToComparableVersion(Version version) =>
        new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

    private static bool ContainsAlternateDataStreamToken(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        return pathWithoutRoot.Contains(':', StringComparison.Ordinal);
    }

    private static bool ContainsUnsafeFormattingCharacter(char character) =>
        char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format;

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                FileCleanupService.TryDeleteTemporaryFile(path, out _);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("FileLocker-Updater");
        return client;
    }
}

internal sealed record UpdateCheckResult(
    Version CurrentVersion,
    UpdateReleaseInfo? Release,
    bool IsUpdateAvailable,
    string StatusMessage);

internal sealed record UpdateReleaseInfo(
    Version Version,
    string DisplayVersion,
    string TagName,
    string HtmlUrl,
    string Notes,
    string InstallerFileName,
    string InstallerDownloadUrl,
    string? Sha256DigestHex,
    string? Sha256DigestDownloadUrl);

internal sealed record UpdateDownloadResult(
    string InstallerPath,
    string FileName,
    UpdateReleaseInfo Release);

internal sealed class UpdateSettings
{
    public bool AutoCheckEnabled { get; set; } = true;
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string? SkippedVersion { get; set; }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public GitHubReleaseAsset[] Assets { get; set; } = [];
}

internal sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

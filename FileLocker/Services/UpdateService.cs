using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class UpdateService
{
    private const string Owner = "jeremymhayes";
    private const string Repo = "FileLocker";
    private const string GitHubApiVersion = "2022-11-28";
    private const string ExpectedPublisherSubjectFragment = "Jeremy Hayes";

    // Flip this to false once every public installer is Authenticode signed.
    // Digest-only fallback is better than no validation, but signature + pinned publisher is stronger.
    private const bool AllowUnsignedDigestFallback = true;

    private const long MaxInstallerBytes = 250L * 1024L * 1024L;
    private const long MaxDigestBytes = 64L * 1024L;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    // Best option: add the SHA-256 thumbprint of your code-signing certificate here.
    // Example format: "AABBCCDDEEFF...". Spaces/colons are ignored.
    // Leaving this empty falls back to the old subject-fragment check below.
    private static readonly string[] TrustedSignerSha256Thumbprints =
    {
        // "PUT_YOUR_CODE_SIGNING_CERT_SHA256_THUMBPRINT_HERE"
    };

    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
    private static readonly Uri ReleasesListUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=10");
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static TimeSpan AutomaticCheckInterval => TimeSpan.FromHours(24);
    internal static string GitHubRepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    internal static UpdateSettings LoadSettings()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new UpdateSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdateSettings>(json, JsonOptions) ?? new UpdateSettings();
        }
        catch
        {
            return new UpdateSettings();
        }
    }

    internal static void SaveSettings(UpdateSettings settings)
    {
        string directory = GetUpdaterDataDirectory();
        Directory.CreateDirectory(directory);

        string path = GetSettingsPath();
        string tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    internal static bool ShouldPerformAutomaticCheck(UpdateSettings settings, DateTimeOffset utcNow)
    {
        if (!settings.AutoCheckEnabled)
        {
            return false;
        }

        return !settings.LastCheckedUtc.HasValue || utcNow - settings.LastCheckedUtc.Value >= AutomaticCheckInterval;
    }

    internal static string GetCurrentVersionLabel() => FormatVersion(GetCurrentVersion());

    internal static Version GetCurrentVersion()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(processPath);
            if (TryParseVersion(versionInfo.ProductVersion, out Version productVersion))
            {
                return productVersion;
            }

            if (TryParseVersion(versionInfo.FileVersion, out Version fileVersion))
            {
                return fileVersion;
            }
        }

        Version? assemblyVersion = typeof(UpdateService).Assembly.GetName().Version;
        return assemblyVersion ?? new Version(0, 0, 0, 0);
    }

    internal static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        Version currentVersion = GetCurrentVersion();

        try
        {
            GitHubReleaseResponse? latestRelease = await TryGetLatestReleaseAsync(cancellationToken);
            if (latestRelease == null)
            {
                return new UpdateCheckResult(
                    currentVersion,
                    null,
                    false,
                    "No GitHub releases are published yet.");
            }

            if (!TryCreateReleaseInfo(latestRelease, out UpdateReleaseInfo update, out _))
            {
                GitHubReleaseResponse? fallbackRelease = await TryGetFirstValidReleaseAsync(cancellationToken);
                if (fallbackRelease == null)
                {
                    return new UpdateCheckResult(
                        currentVersion,
                        null,
                        false,
                        "GitHub returned release metadata, but none of the latest published releases had a supported version tag and installer asset.");
                }

                if (!TryCreateReleaseInfo(fallbackRelease, out update, out string fallbackFailureReason))
                {
                    return new UpdateCheckResult(currentVersion, null, false, fallbackFailureReason);
                }
            }

            bool isUpdateAvailable = update.Version > currentVersion;
            string message = isUpdateAvailable
                ? $"Update available: {update.DisplayVersion}"
                : $"Up to date ({FormatVersion(currentVersion)})";

            return new UpdateCheckResult(currentVersion, update, isUpdateAvailable, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedUpdateFailure(ex))
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                FormatUpdateFailureMessage(ex));
        }
    }

    private static async Task<GitHubReleaseResponse?> TryGetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = CreateGitHubApiRequest(LatestReleaseUri);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            contentStream,
            JsonOptions,
            cancellationToken);
    }

    private static async Task<GitHubReleaseResponse?> TryGetFirstValidReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = CreateGitHubApiRequest(ReleasesListUri);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseResponse[]? releases = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse[]>(
            contentStream,
            JsonOptions,
            cancellationToken);

        if (releases == null || releases.Length == 0)
        {
            return null;
        }

        return releases.FirstOrDefault(release =>
            IsPublishedStableRelease(release) &&
            TryCreateReleaseInfo(release, out _, out _));
    }

    internal static async Task<string> DownloadInstallerAsync(
        UpdateReleaseInfo release,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExpectedGitHubDownloadUri(release.InstallerDownloadUrl, out Uri downloadUri))
        {
            throw new InvalidOperationException("The release installer URL was not a valid GitHub HTTPS release download URL.");
        }

        if (!TryGetSafeInstallerFileName(release.InstallerFileName, out string safeInstallerFileName))
        {
            throw new InvalidOperationException("The release installer file name was not safe to use locally.");
        }

        string downloadDirectory = GetDownloadsDirectory();
        Directory.CreateDirectory(downloadDirectory);

        string installerPath = Path.Combine(downloadDirectory, safeInstallerFileName);
        string tempPath = Path.Combine(downloadDirectory, $"{safeInstallerFileName}.{Guid.NewGuid():N}.download");
        string? expectedSha256Hex = release.Sha256DigestHex;
        if (string.IsNullOrWhiteSpace(expectedSha256Hex) && !string.IsNullOrWhiteSpace(release.Sha256DigestDownloadUrl))
        {
            expectedSha256Hex = await DownloadReleaseDigestAsync(
                release.Sha256DigestDownloadUrl,
                release.InstallerFileName,
                cancellationToken);
        }

        if (File.Exists(installerPath))
        {
            InstallerTrustResult cachedTrust = await ValidateInstallerTrustDetailedAsync(
                installerPath,
                expectedSha256Hex,
                cancellationToken);

            if (cachedTrust.IsTrusted)
            {
                return installerPath;
            }

            TryDeleteFile(installerPath);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadResponse(response);

            await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream outputStream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyToAsyncWithLimit(httpStream, outputStream, MaxInstallerBytes, cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
            }

            InstallerTrustResult trust = await ValidateInstallerTrustDetailedAsync(
                tempPath,
                expectedSha256Hex,
                cancellationToken);

            if (!trust.IsTrusted)
            {
                throw new InvalidOperationException($"The downloaded installer failed validation: {trust.Message}");
            }

            File.Move(tempPath, installerPath, overwrite: true);
            CleanupOlderInstallers(downloadDirectory, installerPath);
            CleanupStaleDownloadFiles(downloadDirectory);
            return installerPath;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task<string?> DownloadReleaseDigestAsync(
        string digestDownloadUrl,
        string installerFileName,
        CancellationToken cancellationToken)
    {
        if (!TryCreateExpectedGitHubDownloadUri(digestDownloadUrl, out Uri digestUri))
        {
            throw new InvalidOperationException("The release digest URL was not a valid GitHub HTTPS release download URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, digestUri);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is <= 0 or > MaxDigestBytes)
        {
            throw new InvalidOperationException("The release SHA-256 digest file was empty or too large.");
        }

        await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var digestStream = new MemoryStream();
        await CopyToAsyncWithLimit(httpStream, digestStream, MaxDigestBytes, cancellationToken);

        string digestText = Encoding.UTF8.GetString(digestStream.ToArray());
        return TryExtractSha256DigestFromText(digestText, installerFileName, out string digest)
            ? digest
            : throw new InvalidOperationException("The release SHA-256 digest file did not contain a valid digest for the installer.");
    }

    private static async Task CopyToAsyncWithLimit(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 128);
        long totalBytes = 0;

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"The installer download exceeded the {maxBytes:N0}-byte safety limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateFinalDownloadResponse(HttpResponseMessage response)
    {
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null || !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installer download did not finish over HTTPS.");
        }

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue)
        {
            if (contentLength.Value <= 0)
            {
                throw new InvalidOperationException("The installer download was empty.");
            }

            if (contentLength.Value > MaxInstallerBytes)
            {
                throw new InvalidOperationException($"The installer is too large ({contentLength.Value:N0} bytes).");
            }
        }
    }

    private static async Task<DigestVerificationResult> VerifyDigestAsync(
        string filePath,
        string? expectedSha256Hex,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256Hex))
        {
            return new DigestVerificationResult(
                DigestVerificationStatus.NotProvided,
                "No SHA-256 digest was provided by the release metadata.");
        }

        if (!TryNormalizeSha256Digest(expectedSha256Hex, out string normalizedExpectedDigest))
        {
            return new DigestVerificationResult(
                DigestVerificationStatus.InvalidMetadata,
                "The release metadata contained a malformed SHA-256 digest.");
        }

        await using FileStream stream = File.OpenRead(filePath);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        string actualDigest = Convert.ToHexString(digest);

        bool matched = string.Equals(actualDigest, normalizedExpectedDigest, StringComparison.OrdinalIgnoreCase);
        return matched
            ? new DigestVerificationResult(DigestVerificationStatus.Verified, "Installer digest matched release metadata.")
            : new DigestVerificationResult(DigestVerificationStatus.Mismatched, "Installer digest did not match release metadata.");
    }

    private static async Task<bool> ValidateInstallerTrustAsync(
        string filePath,
        string? expectedSha256Hex,
        CancellationToken cancellationToken)
    {
        InstallerTrustResult result = await ValidateInstallerTrustDetailedAsync(filePath, expectedSha256Hex, cancellationToken);
        return result.IsTrusted;
    }

    internal static async Task<InstallerTrustResult> ValidateInstallerTrustDetailedAsync(
        string filePath,
        string? expectedSha256Hex,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new InstallerTrustResult(
                IsTrusted: false,
                DigestVerified: false,
                SignatureVerified: false,
                PublisherVerified: false,
                UsedDigestFallback: false,
                Message: "Installer file is missing.");
        }

        DigestVerificationResult digest = await VerifyDigestAsync(filePath, expectedSha256Hex, cancellationToken);
        if (digest.IsFailure)
        {
            return new InstallerTrustResult(
                IsTrusted: false,
                DigestVerified: false,
                SignatureVerified: false,
                PublisherVerified: false,
                UsedDigestFallback: false,
                Message: digest.Message);
        }

        AuthenticodeVerificationResult authenticode = VerifyAuthenticodeSignature(filePath);
        using X509Certificate2? signerCertificate = authenticode.SignerCertificate;

        if (authenticode.Status == AuthenticodeVerificationStatus.Verified)
        {
            bool publisherVerified = signerCertificate != null && IsExpectedPublisher(signerCertificate);

            return new InstallerTrustResult(
                IsTrusted: publisherVerified,
                DigestVerified: digest.IsVerified,
                SignatureVerified: true,
                PublisherVerified: publisherVerified,
                UsedDigestFallback: false,
                Message: publisherVerified
                    ? digest.IsVerified
                        ? "Installer digest and Authenticode publisher were verified."
                        : "Installer Authenticode publisher was verified; no release digest was available."
                    : "Installer signature is valid, but the publisher is not recognized.");
        }

        if (authenticode.Status == AuthenticodeVerificationStatus.Invalid)
        {
            return new InstallerTrustResult(
                IsTrusted: false,
                DigestVerified: digest.IsVerified,
                SignatureVerified: false,
                PublisherVerified: false,
                UsedDigestFallback: false,
                Message: $"Installer Authenticode signature is invalid ({authenticode.Message}).");
        }

        if (AllowUnsignedDigestFallback && digest.IsVerified)
        {
            return new InstallerTrustResult(
                IsTrusted: true,
                DigestVerified: true,
                SignatureVerified: false,
                PublisherVerified: false,
                UsedDigestFallback: true,
                Message: $"Installer digest verified; Authenticode signature was not verified ({authenticode.Message}).");
        }

        return new InstallerTrustResult(
            IsTrusted: false,
            DigestVerified: digest.IsVerified,
            SignatureVerified: false,
            PublisherVerified: false,
            UsedDigestFallback: false,
            Message: digest.Status == DigestVerificationStatus.NotProvided
                ? $"Installer Authenticode signature was not verified and no SHA-256 digest was provided ({authenticode.Message})."
                : $"Installer Authenticode signature was not verified ({authenticode.Message}).");
    }

    private static void CleanupOlderInstallers(string downloadDirectory, string currentInstallerPath)
    {
        foreach (string file in Directory.EnumerateFiles(downloadDirectory, "*.exe"))
        {
            if (!string.Equals(file, currentInstallerPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(file);
            }
        }
    }

    private static void CleanupStaleDownloadFiles(string downloadDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(downloadDirectory, "*.download"))
        {
            TryDeleteFile(file);
        }
    }

    private static bool TryCreateReleaseInfo(
        GitHubReleaseResponse release,
        out UpdateReleaseInfo update,
        out string failureReason)
    {
        update = null!;

        if (!IsPublishedStableRelease(release))
        {
            failureReason = "The GitHub release is a draft or prerelease, so it was ignored.";
            return false;
        }

        if (!TryParseVersion(release.TagName, out Version version))
        {
            failureReason = $"The latest GitHub release tag '{release.TagName}' is not a supported version format.";
            return false;
        }

        GitHubReleaseAsset? installerAsset = release.Assets
            .Where(IsPotentialInstallerAsset)
            .OrderByDescending(asset => asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase))
            .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (installerAsset == null)
        {
            failureReason = "The latest GitHub release does not contain an uploaded .exe installer asset.";
            return false;
        }

        if (installerAsset.Size > MaxInstallerBytes)
        {
            failureReason = $"The installer asset is too large ({installerAsset.Size:N0} bytes).";
            return false;
        }

        if (!TryGetSafeInstallerFileName(installerAsset.Name, out string safeInstallerFileName))
        {
            failureReason = "The installer asset file name is not safe to use locally.";
            return false;
        }

        if (!TryCreateExpectedGitHubDownloadUri(installerAsset.BrowserDownloadUrl, out Uri installerDownloadUri))
        {
            failureReason = "The installer asset download URL is not a valid GitHub HTTPS release download URL.";
            return false;
        }

        string? sha256 = null;
        string? sha256DownloadUrl = null;
        if (!string.IsNullOrWhiteSpace(installerAsset.Digest))
        {
            if (!TryNormalizeSha256Digest(installerAsset.Digest, out sha256))
            {
                failureReason = "The installer asset digest is not a valid SHA-256 digest.";
                return false;
            }
        }
        else if (TryFindDigestAsset(release.Assets, installerAsset, out GitHubReleaseAsset? digestAsset))
        {
            if (!TryCreateExpectedGitHubDownloadUri(digestAsset.BrowserDownloadUrl, out Uri digestDownloadUri))
            {
                failureReason = "The installer SHA-256 digest asset URL is not a valid GitHub HTTPS release download URL.";
                return false;
            }

            sha256DownloadUrl = digestDownloadUri.ToString();
        }

        update = new UpdateReleaseInfo(
            version,
            FormatVersion(version),
            release.TagName,
            release.HtmlUrl,
            release.Body ?? string.Empty,
            safeInstallerFileName,
            installerDownloadUri.ToString(),
            sha256,
            sha256DownloadUrl);

        failureReason = string.Empty;
        return true;
    }

    private static bool IsPotentialInstallerAsset(GitHubReleaseAsset asset)
    {
        return string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) &&
            asset.Size <= MaxInstallerBytes &&
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !asset.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
            !asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindDigestAsset(
        GitHubReleaseAsset[] assets,
        GitHubReleaseAsset installerAsset,
        out GitHubReleaseAsset digestAsset)
    {
        digestAsset = assets
            .Where(asset => IsPotentialDigestAsset(asset, installerAsset.Name))
            .OrderByDescending(asset => IsExactDigestAssetName(asset.Name, installerAsset.Name))
            .ThenByDescending(asset => asset.Name.Contains(installerAsset.Name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()!;

        return digestAsset != null;
    }

    private static bool IsPotentialDigestAsset(GitHubReleaseAsset asset, string installerFileName)
    {
        if (!string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
            asset.Size > MaxDigestBytes ||
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsExactDigestAssetName(asset.Name, installerFileName) ||
            asset.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
            asset.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
            asset.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactDigestAssetName(string digestAssetName, string installerFileName)
    {
        return digestAssetName.Equals($"{installerFileName}.sha256", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256.txt", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256sum", StringComparison.OrdinalIgnoreCase) ||
            digestAssetName.Equals($"{installerFileName}.sha256sum.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublishedStableRelease(GitHubReleaseResponse release)
    {
        return !release.Draft && !release.Prerelease;
    }

    private static bool TryCreateExpectedGitHubDownloadUri(string rawUrl, out Uri uri)
    {
        uri = null!;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? parsedUri))
        {
            return false;
        }

        if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(parsedUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedPathPrefix = $"/{Owner}/{Repo}/releases/download/";
        if (!parsedUri.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private static bool TryGetSafeInstallerFileName(string rawFileName, out string safeFileName)
    {
        safeFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return false;
        }

        string trimmed = rawFileName.Trim().Replace('\\', '/');
        string fileName = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            return false;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] cleanedChars = fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        string cleaned = new(cleanedChars);
        if (!cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        safeFileName = cleaned;
        return true;
    }

    private static bool TryNormalizeSha256Digest(string? rawDigest, out string normalizedDigest)
    {
        normalizedDigest = string.Empty;
        if (string.IsNullOrWhiteSpace(rawDigest))
        {
            return false;
        }

        string candidate = rawDigest.Trim();
        if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["sha256:".Length..].Trim();
        }

        if (candidate.Length != 64)
        {
            return false;
        }

        if (candidate.Any(ch => !IsHexDigit(ch)))
        {
            return false;
        }

        normalizedDigest = candidate.ToUpperInvariant();
        return true;
    }

    internal static bool TryExtractSha256DigestFromText(
        string digestText,
        string installerFileName,
        out string normalizedDigest)
    {
        normalizedDigest = string.Empty;
        if (string.IsNullOrWhiteSpace(digestText))
        {
            return false;
        }

        string? fallbackDigest = null;
        string safeInstallerFileName = Path.GetFileName(installerFileName);
        string[] lines = digestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            bool referencesInstaller = string.IsNullOrWhiteSpace(safeInstallerFileName) ||
                line.Contains(safeInstallerFileName, StringComparison.OrdinalIgnoreCase);

            foreach (string candidate in ExtractDigestCandidates(line))
            {
                if (!TryNormalizeSha256Digest(candidate, out string digest))
                {
                    continue;
                }

                if (referencesInstaller)
                {
                    normalizedDigest = digest;
                    return true;
                }

                fallbackDigest ??= digest;
            }
        }

        if (fallbackDigest == null)
        {
            return false;
        }

        normalizedDigest = fallbackDigest;
        return true;
    }

    private static IEnumerable<string> ExtractDigestCandidates(string line)
    {
        yield return line;

        int equalsIndex = line.LastIndexOf('=');
        if (equalsIndex >= 0 && equalsIndex + 1 < line.Length)
        {
            yield return line[(equalsIndex + 1)..].Trim();
        }

        string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in tokens)
        {
            yield return token.Trim('*', '"', '\'');
        }
    }

    private static bool IsExpectedPublisher(X509Certificate2 certificate)
    {
        if (TrustedSignerSha256Thumbprints.Length > 0)
        {
            string actualThumbprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
            return TrustedSignerSha256Thumbprints.Any(expected =>
                string.Equals(NormalizeHexString(expected), actualThumbprint, StringComparison.OrdinalIgnoreCase));
        }

        return certificate.Subject.Contains(ExpectedPublisherSubjectFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHexString(string value)
    {
        string candidate = value.Trim();
        if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["sha256:".Length..];
        }

        return new string(candidate.Where(IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static bool IsHexDigit(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static bool IsUnsignedOrUnsupportedTrustStatus(uint status)
    {
        return status is
            TrustEProviderUnknown or
            TrustESubjectFormUnknown or
            TrustENoSignature;
    }

    private static AuthenticodeVerificationResult VerifyAuthenticodeSignature(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AuthenticodeVerificationResult(
                AuthenticodeVerificationStatus.Unsupported,
                null,
                "Authenticode verification is only available on Windows.");
        }

        IntPtr fileInfoPointer = IntPtr.Zero;
        IntPtr trustDataPointer = IntPtr.Zero;
        IntPtr filePathPointer = IntPtr.Zero;

        try
        {
            filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustFileInfo(filePathPointer);
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(fileInfo));
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(trustData));
            Marshal.StructureToPtr(trustData, trustDataPointer, false);

            Guid action = WinTrustActionGenericVerifyV2;
            uint status = WinVerifyTrust(IntPtr.Zero, in action, trustDataPointer);
            if (status != 0)
            {
                return new AuthenticodeVerificationResult(
                    IsUnsignedOrUnsupportedTrustStatus(status)
                        ? AuthenticodeVerificationStatus.NotSigned
                        : AuthenticodeVerificationStatus.Invalid,
                    null,
                    $"WinVerifyTrust returned 0x{status:X8}.");
            }

            X509Certificate2 signerCertificate = new(X509Certificate.CreateFromSignedFile(filePath));
            return new AuthenticodeVerificationResult(
                AuthenticodeVerificationStatus.Verified,
                signerCertificate,
                "Authenticode signature verified.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or ExternalException or PlatformNotSupportedException)
        {
            return new AuthenticodeVerificationResult(
                AuthenticodeVerificationStatus.Invalid,
                null,
                ex.Message);
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                WinTrustData trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                if (trustData.StateAction == WinTrustDataStateAction.Verify)
                {
                    trustData.StateAction = WinTrustDataStateAction.Close;
                    Marshal.StructureToPtr(trustData, trustDataPointer, false);
                    Guid action = WinTrustActionGenericVerifyV2;
                    _ = WinVerifyTrust(IntPtr.Zero, in action, trustDataPointer);
                }

                Marshal.FreeCoTaskMem(trustDataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (filePathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(filePathPointer);
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileLocker", "1.0"));
        return client;
    }

    private static HttpRequestMessage CreateGitHubApiRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        return request;
    }

    private static bool TryParseVersion(string? rawVersion, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        string[] parts = normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2 || parts.Length > 4)
        {
            return false;
        }

        int[] values = new[] { 0, 0, 0, 0 };
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out values[i]) || values[i] < 0)
            {
                return false;
            }
        }

        version = new Version(values[0], values[1], values[2], values[3]);
        return true;
    }

    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        if (version.Build > 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"{version.Major}.{version.Minor}";
    }

    private static bool IsExpectedUpdateFailure(Exception ex)
    {
        return ex is HttpRequestException or JsonException or IOException or OperationCanceledException;
    }

    private static string FormatUpdateFailureMessage(Exception ex)
    {
        return ex switch
        {
            HttpRequestException http when http.StatusCode == HttpStatusCode.Forbidden =>
                "GitHub rejected the update request. This is usually a rate-limit or access issue.",
            HttpRequestException http when http.StatusCode.HasValue =>
                $"GitHub update check failed with HTTP {(int)http.StatusCode.Value} ({http.StatusCode.Value}).",
            HttpRequestException =>
                "Could not contact GitHub to check for updates.",
            JsonException =>
                "GitHub returned release metadata that could not be parsed.",
            IOException =>
                "The updater could not read or write one of its local files.",
            OperationCanceledException =>
                "The update check timed out.",
            _ =>
                "The update check failed."
        };
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string GetUpdaterDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker",
            "Updater");
    }

    private static string GetDownloadsDirectory() => Path.Combine(GetUpdaterDataDirectory(), "Downloads");

    private static string GetSettingsPath() => Path.Combine(GetUpdaterDataDirectory(), "settings.json");

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[] Assets { get; set; } = Array.Empty<GitHubReleaseAsset>();
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }

    private sealed record DigestVerificationResult(DigestVerificationStatus Status, string Message)
    {
        public bool IsVerified => Status == DigestVerificationStatus.Verified;
        public bool IsFailure => Status is DigestVerificationStatus.InvalidMetadata or DigestVerificationStatus.Mismatched;
    }

    private enum DigestVerificationStatus
    {
        NotProvided,
        Verified,
        Mismatched,
        InvalidMetadata
    }

    private sealed record AuthenticodeVerificationResult(
        AuthenticodeVerificationStatus Status,
        X509Certificate2? SignerCertificate,
        string Message);

    private enum AuthenticodeVerificationStatus
    {
        Verified,
        NotSigned,
        Invalid,
        Unsupported
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustENoSignature = 0x800B0100;

    private enum WinTrustDataUIChoice : uint
    {
        All = 1,
        None = 2
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        None = 0,
        WholeChain = 1
    }

    private enum WinTrustDataChoice : uint
    {
        File = 1
    }

    private enum WinTrustDataStateAction : uint
    {
        Ignore = 0,
        Verify = 1,
        Close = 2
    }

    [Flags]
    private enum WinTrustDataProvFlags : uint
    {
        None = 0x00000000,
        RevocationCheckChain = 0x00000040,
        Safer = 0x00000100
    }

    private enum WinTrustDataUIContext : uint
    {
        Execute = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePathPointer;
        public IntPtr FileHandle;
        public IntPtr KnownSubjectPointer;

        public WinTrustFileInfo(IntPtr filePathPointer)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePathPointer = filePathPointer;
            FileHandle = IntPtr.Zero;
            KnownSubjectPointer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public WinTrustDataUIChoice UIChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public IntPtr FileInfoPointer;
        public WinTrustDataStateAction StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public WinTrustDataProvFlags ProvFlags;
        public WinTrustDataUIContext UIContext;

        public WinTrustData(IntPtr fileInfoPointer)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SIPClientData = IntPtr.Zero;
            UIChoice = WinTrustDataUIChoice.None;
            RevocationChecks = WinTrustDataRevocationChecks.WholeChain;
            UnionChoice = WinTrustDataChoice.File;
            FileInfoPointer = fileInfoPointer;
            StateAction = WinTrustDataStateAction.Verify;
            StateData = IntPtr.Zero;
            URLReference = IntPtr.Zero;
            ProvFlags = WinTrustDataProvFlags.Safer | WinTrustDataProvFlags.RevocationCheckChain;
            UIContext = WinTrustDataUIContext.Execute;
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] in Guid actionId, IntPtr actionData);
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

internal sealed record InstallerTrustResult(
    bool IsTrusted,
    bool DigestVerified,
    bool SignatureVerified,
    bool PublisherVerified,
    bool UsedDigestFallback,
    string Message);

internal sealed class UpdateSettings
{
    public bool AutoCheckEnabled { get; set; } = true;
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string? SkippedVersion { get; set; }
}

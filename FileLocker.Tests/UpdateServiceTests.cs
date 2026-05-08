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
}

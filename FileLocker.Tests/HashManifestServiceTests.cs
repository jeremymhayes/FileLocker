using FileLocker;

namespace FileLocker.Tests;

public sealed class HashManifestServiceTests
{
    [Fact]
    public async Task CreateManifestAsync_WritesRelativeSha256Manifest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "docs", "alpha.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);

            HashManifestResult result = await HashManifestService.CreateManifestAsync([root], "SHA-256", root, cancellationToken);
            string manifest = await File.ReadAllTextAsync(result.ManifestPath, cancellationToken);

            Assert.Equal("SHA-256", result.Algorithm);
            Assert.Equal(1, result.FileCount);
            Assert.EndsWith(".sha256", result.ManifestPath);
            Assert.Contains("docs/alpha.txt", manifest);
            Assert.DoesNotContain(root, manifest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_ReportsMatchedEntries()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);
            HashManifestResult result = await HashManifestService.CreateManifestAsync([filePath], "SHA-256", root, cancellationToken);

            HashManifestVerificationResult verification = await HashManifestService.VerifyManifestAsync(result.ManifestPath, root, cancellationToken);

            Assert.Equal(1, verification.EntryCount);
            Assert.Equal(1, verification.MatchedCount);
            Assert.Equal(0, verification.MismatchedCount);
            Assert.Equal(0, verification.MissingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseManifest_IgnoresCommentsAndAbsolutePaths()
    {
        string content = """
            # FileLocker SHA-256 manifest
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  relative.txt
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  C:\secret.txt
            """;

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Single(entries);
        Assert.Equal("relative.txt", entries[0].RelativePath);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"FileLockerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

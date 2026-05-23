using System.Security.Cryptography;
using System.Text;

namespace FileLocker.Tests;

public sealed class FileHashServiceTests
{
    [Fact]
    public async Task ComputeHashesHexAsync_HashesFilesInParallel()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string firstPath = Path.Combine(tempDirectory, "first.txt");
        string secondPath = Path.Combine(tempDirectory, "second.txt");
        await File.WriteAllTextAsync(firstPath, "alpha", Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(secondPath, "bravo", Encoding.UTF8, cancellationToken);

        try
        {
            IReadOnlyDictionary<string, string> hashes = await FileHashService.ComputeHashesHexAsync(
                [firstPath, secondPath],
                "SHA-256",
                maxDegreeOfParallelism: 2,
                cancellationToken: cancellationToken);

            Assert.Equal(2, hashes.Count);
            Assert.Equal(ExpectedSha256(File.ReadAllBytes(firstPath)), hashes[firstPath]);
            Assert.Equal(ExpectedSha256(File.ReadAllBytes(secondPath)), hashes[secondPath]);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashesHexAsync_TrimsAndDeduplicatesPathEntries()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "payload.txt");
        await File.WriteAllTextAsync(filePath, "alpha", Encoding.UTF8, cancellationToken);

        try
        {
            IReadOnlyDictionary<string, string> hashes = await FileHashService.ComputeHashesHexAsync(
                [$"  {filePath}  ", filePath],
                "SHA-256",
                maxDegreeOfParallelism: 1,
                cancellationToken: cancellationToken);

            string hash = Assert.Single(hashes).Value;
            Assert.Equal(ExpectedSha256(await File.ReadAllBytesAsync(filePath, cancellationToken)), hash);
            Assert.True(hashes.ContainsKey(filePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashHexAsync_ReportsCompletionForEmptyFile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "empty.txt");
        await File.WriteAllBytesAsync(filePath, [], cancellationToken);
        var progress = new RecordingProgress();

        try
        {
            string hash = await FileHashService.ComputeHashHexAsync(
                filePath,
                "SHA-256",
                progress,
                cancellationToken);

            Assert.Equal(ExpectedSha256([]), hash);
            Assert.Equal(100, progress.Values.Last());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashHexAsync_NullAlgorithmDefaultsToSha256()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "payload.txt");
        await File.WriteAllTextAsync(filePath, "alpha", Encoding.UTF8, cancellationToken);

        try
        {
            string hash = await FileHashService.ComputeHashHexAsync(
                filePath,
                null!,
                cancellationToken: cancellationToken);

            Assert.Equal(ExpectedSha256(await File.ReadAllBytesAsync(filePath, cancellationToken)), hash);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashHexAsync_TrimsFilePath()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "payload.txt");
        await File.WriteAllTextAsync(filePath, "alpha", Encoding.UTF8, cancellationToken);

        try
        {
            string hash = await FileHashService.ComputeHashHexAsync(
                $"  {filePath}  ",
                "SHA-256",
                cancellationToken: cancellationToken);

            Assert.Equal(ExpectedSha256(await File.ReadAllBytesAsync(filePath, cancellationToken)), hash);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashHexAsync_RejectsUnsupportedAlgorithm()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "payload.txt");
        await File.WriteAllTextAsync(filePath, "alpha", TestContext.Current.CancellationToken);

        try
        {
            ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                FileHashService.ComputeHashHexAsync(
                    filePath,
                    "MD5",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("Unsupported hash algorithm", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashHexAsync_RejectsNullFilePath()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            FileHashService.ComputeHashHexAsync(
                null!,
                "SHA-256",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ComputeHashHexAsync_RejectsBlankFilePath(string filePath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FileHashService.ComputeHashHexAsync(
                filePath,
                "SHA-256",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComputeHashHexAsync_CanceledTokenDoesNotOpenFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashService.ComputeHashHexAsync(
                Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing.txt"),
                "SHA-256",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ComputeHashesHexAsync_RejectsNullFilePaths()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            FileHashService.ComputeHashesHexAsync(
                null!,
                "SHA-256",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComputeHashesHexAsync_RejectsUnsupportedAlgorithmBeforeEnumeratingPaths()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            FileHashService.ComputeHashesHexAsync(
                ThrowIfEnumerated(),
                "SHA-1",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported hash algorithm", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ComputeHashesHexAsync_RejectsBlankPathEntries(string? path)
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            FileHashService.ComputeHashesHexAsync(
                [Path.Combine(Path.GetTempPath(), "valid.txt"), path!],
                "SHA-256",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("blank entries", ex.Message);
    }

    [Fact]
    public async Task ComputeHashesHexAsync_CanceledTokenDoesNotEnumeratePaths()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashService.ComputeHashesHexAsync(
                ThrowIfEnumerated(),
                "SHA-256",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ComputeHashesHexAsync_CanceledDuringEnumerationStopsBeforeNextPath()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHashService.ComputeHashesHexAsync(
                CancelBeforeYieldingFirstPath(cancellation),
                "SHA-256",
                cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetExpectedHexLength_DefaultsBlankAlgorithmsToSha256(string? algorithm)
    {
        Assert.Equal(64, FileHashService.GetExpectedHexLength(algorithm));
        Assert.Equal(256, FileHashService.GetDigestBits(algorithm));
    }

    private static string ExpectedSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IEnumerable<string> ThrowIfEnumerated()
    {
        throw new InvalidOperationException("The path sequence should not have been enumerated.");
#pragma warning disable CS0162
        yield return string.Empty;
#pragma warning restore CS0162
    }

    private static IEnumerable<string> CancelBeforeYieldingFirstPath(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        yield return Path.Combine(Path.GetTempPath(), "FileLocker.Tests", "cancelled.txt");
        throw new InvalidOperationException("The path sequence should not continue after cancellation.");
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
        }
    }
}

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

    private static string ExpectedSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

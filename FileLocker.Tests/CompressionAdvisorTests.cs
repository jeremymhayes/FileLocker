namespace FileLocker.Tests;

public sealed class CompressionAdvisorTests
{
    [Fact]
    public void CreatePlan_DisabledCompressionKeepsOriginalSize()
    {
        CompressionPlan plan = CompressionAdvisor.CreatePlan("notes.txt", 128_000, compressionRequested: false);

        Assert.False(plan.ShouldCompress);
        Assert.Equal(128_000, plan.EstimatedCompressedSize);
    }

    [Fact]
    public void CreatePlan_SkipsKnownCompressedExtensions()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string zipPath = Path.Combine(tempDirectory, "archive.zip");
        File.WriteAllBytes(zipPath, Enumerable.Range(0, 128_000).Select(i => (byte)(i % 251)).ToArray());

        try
        {
            CompressionPlan plan = CompressionAdvisor.CreatePlan(zipPath, new FileInfo(zipPath).Length, compressionRequested: true);

            Assert.False(plan.ShouldCompress);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_CompressesHighlyRepetitiveContent()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string textPath = Path.Combine(tempDirectory, "notes.txt");
        File.WriteAllText(textPath, new string('A', 128_000));

        try
        {
            CompressionPlan plan = CompressionAdvisor.CreatePlan(textPath, new FileInfo(textPath).Length, compressionRequested: true);

            Assert.True(plan.ShouldCompress);
            Assert.True(plan.EstimatedCompressedSize < new FileInfo(textPath).Length);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void HasUsefulSavings_RejectsTinySavings()
    {
        Assert.False(CompressionAdvisor.HasUsefulSavings(1_000_000, 999_500));
        Assert.True(CompressionAdvisor.HasUsefulSavings(1_000_000, 900_000));
    }
}

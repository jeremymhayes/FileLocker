namespace FileLocker.Tests;

public sealed class KeyfileLoadingTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadKeyfileBytesIfConfigured_ReturnsNullForBlankPath()
    {
        Assert.Null(MainWindow.ReadKeyfileBytesIfConfigured("   "));
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_ReadsNonEmptyKeyfile()
    {
        Directory.CreateDirectory(_rootPath);
        string keyfilePath = Path.Combine(_rootPath, "key.bin");
        File.WriteAllBytes(keyfilePath, [1, 2, 3, 4]);

        byte[]? keyfileBytes = MainWindow.ReadKeyfileBytesIfConfigured(keyfilePath);

        Assert.Equal([1, 2, 3, 4], keyfileBytes);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsEmptyKeyfile()
    {
        Directory.CreateDirectory(_rootPath);
        string keyfilePath = Path.Combine(_rootPath, "empty.key");
        File.WriteAllBytes(keyfilePath, []);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(keyfilePath));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsOversizedKeyfileBeforeReading()
    {
        Directory.CreateDirectory(_rootPath);
        string keyfilePath = Path.Combine(_rootPath, "oversized.key");
        using (FileStream stream = File.Create(keyfilePath))
        {
            stream.SetLength(MainWindow.MaxKeyfileBytes + 1);
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(keyfilePath));

        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

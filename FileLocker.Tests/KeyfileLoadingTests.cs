using System.Security.Cryptography;

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
    public void ReadKeyfileBytesIfConfigured_RejectsDirectoryPath()
    {
        Directory.CreateDirectory(_rootPath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(_rootPath));

        Assert.Contains("file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsMalformedPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured("C:\\Temp\\bad\0key.bin"));

        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsControlCharacterPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured("C:\\Temp\\bad\r\nkey.bin"));

        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsUnicodeFormatCharacterPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured("C:\\Temp\\bad\u202Ekey.bin"));

        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("key.bin")]
    [InlineData("C:key.bin")]
    public void ReadKeyfileBytesIfConfigured_RejectsRelativeKeyfilePath(string keyfilePath)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(keyfilePath));

        Assert.Contains("fully qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsAlternateDataStreamPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(Path.Combine(_rootPath, "key.bin:stream")));

        Assert.Contains("normal file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadKeyfileBytesIfConfigured_RejectsAlternateDataStreamParentPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ReadKeyfileBytesIfConfigured(Path.Combine(_rootPath, "keys:stream", "key.bin")));

        Assert.Contains("normal file", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ValidateKdfSecretTextLength_RejectsOversizedSecretText()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.ValidateKdfSecretTextLength(new string('A', KdfSecretValidator.MaxSecretTextBytes + 1)));

        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateKdfSecretTextLength_RejectsUtf8ByteOversizedSecretText()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.ValidateKdfSecretTextLength(new string('\u20AC', (KdfSecretValidator.MaxSecretTextBytes / 2) + 1)));

        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KdfSecretMaterial_ReturnsPasswordBytesWhenNoKeyfileIsConfigured()
    {
        byte[] passwordBytes = [1, 2, 3];

        byte[] secret = KdfSecretMaterial.Build(passwordBytes, null, out byte[]? combinedSecret);

        Assert.Same(passwordBytes, secret);
        Assert.Null(combinedSecret);
    }

    [Fact]
    public void KdfSecretMaterial_HashesPasswordAndKeyfileTogether()
    {
        byte[] passwordBytes = [1, 2, 3];
        byte[] keyfileBytes = [4, 5];
        byte[] expected = SHA256.HashData([1, 2, 3, 4, 5]);

        byte[] secret = KdfSecretMaterial.Build(passwordBytes, keyfileBytes, out byte[]? combinedSecret);

        Assert.Equal([1, 2, 3, 4, 5], combinedSecret);
        Assert.Equal(expected, secret);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

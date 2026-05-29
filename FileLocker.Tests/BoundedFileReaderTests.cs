using System.Text;

namespace FileLocker.Tests;

public sealed class BoundedFileReaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAllUtf8TextAsync_AllowsFileAtLimitAndStripsUtf8Bom()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'o', (byte)'k'];
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        string text = await BoundedFileReader.ReadAllUtf8TextAsync(path, bytes.Length, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", text);
    }

    [Fact]
    public async Task ReadAllUtf8TextAsync_RejectsOversizedFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        await File.WriteAllTextAsync(path, "abcd", Encoding.UTF8, TestContext.Current.CancellationToken);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedFileReader.ReadAllUtf8TextAsync(path, maxBytes: 3, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("File is too large to read.", ex.Message);
    }

    [Fact]
    public void ReadAllUtf8Text_UsesCustomTooLargeMessage()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        File.WriteAllText(path, "abcd", Encoding.UTF8);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            BoundedFileReader.ReadAllUtf8Text(path, maxBytes: 3, "Stored data is too large."));

        Assert.Equal("Stored data is too large.", ex.Message);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsInvalidUtf8()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        File.WriteAllBytes(path, [0xC3, 0x28]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            BoundedFileReader.ReadAllUtf8Text(path, maxBytes: 1024));

        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsControlCharacterPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8Text("C:\\bad\r\nsettings.json", maxBytes: 1024));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsUnicodeFormatCharacterPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8Text(Path.Combine(_rootPath, "settings" + "\u202E" + ".json"), maxBytes: 1024));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllUtf8TextAsync_RejectsBlankPath()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8TextAsync(" ", maxBytes: 1024, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsRelativePath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8Text("settings.json", maxBytes: 1024));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("normal file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsAlternateDataStreamPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8Text(Path.Combine(_rootPath, "settings.json:stream"), maxBytes: 1024));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("normal file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAllUtf8Text_RejectsAlternateDataStreamInParentPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            BoundedFileReader.ReadAllUtf8Text(Path.Combine(_rootPath, "settings:stream", "settings.json"), maxBytes: 1024));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("normal file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

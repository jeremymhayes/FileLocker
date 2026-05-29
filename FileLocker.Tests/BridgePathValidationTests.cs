using System.Text;

namespace FileLocker.Tests;

public sealed class BridgePathValidationTests
{
    [Fact]
    public void RequireExistingPath_RejectsBlankPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath(" "));

        Assert.Equal("A file or folder path is required.", ex.Message);
    }

    [Fact]
    public void RequireExistingPath_RejectsControlCharacterPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath("C:\\Temp\\bad\0path.txt"));

        Assert.Equal("The selected path contains invalid characters.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void RequireExistingPath_RejectsUnicodeFormatCharacterPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath("C:\\Temp\\bad\u202Epath.txt"));

        Assert.Equal("The selected path contains invalid characters.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void RequireExistingPath_RejectsRelativePath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath("payload.txt"));

        Assert.Equal("The selected path must be fully qualified.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void RequireExistingPath_RejectsAlternateDataStreamPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath(Path.Combine(Path.GetTempPath(), "payload.txt:stream")));

        Assert.Equal("The selected path must reference a normal file or folder.", ex.Message);
    }

    [Fact]
    public void RequireExistingPath_RejectsAlternateDataStreamParentPath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.RequireExistingPath(Path.Combine(Path.GetTempPath(), "payload:stream", "file.txt")));

        Assert.Equal("The selected path must reference a normal file or folder.", ex.Message);
    }

    [Fact]
    public void RequireExistingPath_ReportsMissingPath()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "payload.txt");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() =>
            MainWindow.RequireExistingPath(missingPath));

        Assert.Equal("The selected path could not be found.", ex.Message);
        Assert.Equal(Path.GetFullPath(missingPath), ex.FileName);
    }

    [Fact]
    public void RequireExistingPath_ReturnsFullPathForExistingDirectory()
    {
        string directory = Path.GetTempPath();

        string resolved = MainWindow.RequireExistingPath($"  {directory}  ");

        Assert.Equal(Path.GetFullPath(directory), resolved);
    }

    [Fact]
    public void CreateExplorerSelectStartInfo_UsesValidatedFullPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "payload.txt");
        File.WriteAllText(filePath, "payload");

        try
        {
            System.Diagnostics.ProcessStartInfo startInfo = MainWindow.CreateExplorerSelectStartInfo($"  {filePath}  ");

            Assert.Equal("explorer.exe", startInfo.FileName);
            Assert.Equal($"/select,\"{Path.GetFullPath(filePath)}\"", startInfo.Arguments);
            Assert.True(startInfo.UseShellExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateExplorerSelectStartInfo_RejectsRelativePath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.CreateExplorerSelectStartInfo("payload.txt"));

        Assert.Equal("The selected path must be fully qualified.", ex.Message);
    }

    [Fact]
    public void TryNormalizeDecryptSelectionPath_ReturnsFullPathForExistingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "payload.locked");
        File.WriteAllText(filePath, "payload");

        try
        {
            bool result = MainWindow.TryNormalizeDecryptSelectionPath($"  {filePath}  ", out string normalizedPath, out string detail);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(filePath), normalizedPath);
            Assert.Equal(string.Empty, detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("payload.locked")]
    [InlineData("C:\\Temp\\payload.locked:stream")]
    [InlineData("C:\\Temp\\payload\u202E.locked")]
    public void TryNormalizeDecryptSelectionPath_RejectsUnsafePaths(string path)
    {
        bool result = MainWindow.TryNormalizeDecryptSelectionPath(path, out string normalizedPath, out string detail);

        Assert.False(result);
        Assert.Equal(string.Empty, normalizedPath);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void TryNormalizeQueueSelectionPath_ReturnsFullPathForExistingDirectory()
    {
        string directory = Path.GetTempPath();

        bool result = MainWindow.TryNormalizeQueueSelectionPath($"  {directory}  ", out string normalizedPath, out string warning);

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(directory), normalizedPath);
        Assert.Equal(string.Empty, warning);
    }

    [Theory]
    [InlineData("payload.txt")]
    [InlineData("C:\\Temp\\payload.txt:stream")]
    [InlineData("C:\\Temp\\payload\u202E.txt")]
    public void TryNormalizeQueueSelectionPath_RejectsUnsafePaths(string path)
    {
        bool result = MainWindow.TryNormalizeQueueSelectionPath(path, out string normalizedPath, out string warning);

        Assert.False(result);
        Assert.Equal(string.Empty, normalizedPath);
        Assert.False(string.IsNullOrWhiteSpace(warning));
    }

    [Fact]
    public void TryNormalizeMetadataScramblerPath_ReturnsFullPathForExistingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "photo.jpg");
        File.WriteAllText(filePath, "metadata");

        try
        {
            bool result = MainWindow.TryNormalizeMetadataScramblerPath($"  {filePath}  ", out string normalizedPath, out string warning);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(filePath), normalizedPath);
            Assert.Equal(string.Empty, warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("C:\\Temp\\photo.jpg:stream")]
    [InlineData("C:\\Temp\\photo\u202E.jpg")]
    public void TryNormalizeMetadataScramblerPath_RejectsUnsafePaths(string path)
    {
        bool result = MainWindow.TryNormalizeMetadataScramblerPath(path, out string normalizedPath, out string warning);

        Assert.False(result);
        Assert.Equal(string.Empty, normalizedPath);
        Assert.False(string.IsNullOrWhiteSpace(warning));
    }

    [Fact]
    public void TryNormalizeHashFileSelectionPath_ReturnsFullPathForExistingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "payload.txt");
        File.WriteAllText(filePath, "payload");

        try
        {
            bool result = MainWindow.TryNormalizeHashFileSelectionPath($"  {filePath}  ", out string normalizedPath);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(filePath), normalizedPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("payload.txt")]
    [InlineData("C:\\Temp\\payload.txt:stream")]
    [InlineData("C:\\Temp\\payload\u202E.txt")]
    public void TryNormalizeHashFileSelectionPath_RejectsUnsafePaths(string path)
    {
        bool result = MainWindow.TryNormalizeHashFileSelectionPath(path, out string normalizedPath);

        Assert.False(result);
        Assert.Equal(string.Empty, normalizedPath);
    }

    [Fact]
    public void IsPayloadV3File_RejectsRelativePathBeforeOpening()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.IsPayloadV3File("payload.locked"));

        Assert.Equal("The selected path must be fully qualified.", ex.Message);
    }

    [Fact]
    public void IsPayloadV3File_ReturnsTrueForTrimmedPayloadPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "payload.locked");

        try
        {
            using (FileStream output = new(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] metadata = Encoding.UTF8.GetBytes("""{"kind":"file","algorithm":"AES-256-GCM","keySizeBits":256}""");
                byte[] plaintext = Encoding.UTF8.GetBytes("payload");
                PayloadChunkedService.WritePayload(
                    output,
                    metadata,
                    (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
                    new PayloadWriteInputs(
                        "Password!234",
                        null,
                        null,
                        16_384,
                        0x01,
                        EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm),
                    CancellationToken.None);
            }

            Assert.True(MainWindow.IsPayloadV3File($"  {filePath}  "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

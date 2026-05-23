using System.Text;

namespace FileLocker.Tests;

public sealed class FileWriteServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReplacesExistingFileAndRemovesTempFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "receipt.md");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8, TestContext.Current.CancellationToken);

        await FileWriteService.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8, TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomically_ReplacesExistingFileAndRemovesTempFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        File.WriteAllText(path, "old", Encoding.UTF8);

        FileWriteService.WriteAllTextAtomically(path, "new", Encoding.UTF8);

        Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReplacesReadOnlyExistingFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "hash.txt");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8, TestContext.Current.CancellationToken);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        await FileWriteService.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8, TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.False((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
    }

    [Fact]
    public void WriteAllTextAtomically_PreservesHiddenAttributeWhenReplaceSucceeds()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        File.WriteAllText(path, "old", Encoding.UTF8);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        FileWriteService.WriteAllTextAtomically(path, "new", Encoding.UTF8);

        Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) == FileAttributes.Hidden);
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_CanceledTokenDoesNotReplaceExistingFile()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "receipt.md");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileWriteService.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8, cancellation.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_CanceledTokenDoesNotCreateDestinationDirectory()
    {
        string destinationDirectory = Path.Combine(_rootPath, "exports");
        string path = Path.Combine(destinationDirectory, "receipt.md");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileWriteService.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8, cancellation.Token));

        Assert.False(Directory.Exists(destinationDirectory));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_RejectsDirectoryLikePathBeforeCreatingDirectory()
    {
        string destinationDirectory = Path.Combine(_rootPath, "exports");
        string path = destinationDirectory + Path.DirectorySeparatorChar;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            FileWriteService.WriteAllTextAtomicallyAsync(path, "new", Encoding.UTF8, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(destinationDirectory));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_CanceledTokenDoesNotEncodeContents()
    {
        string path = Path.Combine(_rootPath, "receipt.md");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileWriteService.WriteAllTextAtomicallyAsync(path, "new", new ThrowingEncoding(), cancellation.Token));

        Assert.False(Directory.Exists(_rootPath));
    }

    [Fact]
    public void WriteAllTextAtomically_RestoresReadOnlyAttributeWhenReplaceFails()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        File.WriteAllText(path, "old", Encoding.UTF8);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        Exception? ex;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ex = Record.Exception(() => FileWriteService.WriteAllTextAtomically(path, "new", Encoding.UTF8));
        }

        Assert.True(ex is IOException or UnauthorizedAccessException);
        Assert.Equal("old", File.ReadAllText(path, Encoding.UTF8));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomically_DoesNotFollowFileLinkTarget()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.txt");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target", Encoding.UTF8);
        string linkPath = Path.Combine(_rootPath, "settings.json");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            IOException exception = Assert.Throws<IOException>(() =>
                FileWriteService.WriteAllTextAtomically(linkPath, "new", Encoding.UTF8));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("external target", File.ReadAllText(outside, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReplaceFileWithTemporaryFile_RestoresReadOnlyAttributeAndContentsWhenSourceIsMissing()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "settings.json");
        string tempPath = Path.Combine(_rootPath, "settings.json.missing.tmp");
        File.WriteAllText(path, "old", Encoding.UTF8);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        Assert.Throws<FileNotFoundException>(() => FileWriteService.ReplaceFileWithTemporaryFile(tempPath, path));

        Assert.Equal("old", File.ReadAllText(path, Encoding.UTF8));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
        Assert.Empty(Directory.EnumerateFiles(_rootPath, "*.tmp"));
    }

    [Fact]
    public void ReplaceFileWithTemporaryFile_RejectsTempFileLinkWithoutCreatingDestination()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.tmp");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target", Encoding.UTF8);
        string tempPath = Path.Combine(_rootPath, "settings.json.link.tmp");
        string path = Path.Combine(_rootPath, "settings.json");

        try
        {
            try
            {
                File.CreateSymbolicLink(tempPath, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            IOException exception = Assert.Throws<IOException>(() =>
                FileWriteService.ReplaceFileWithTemporaryFile(tempPath, path));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(tempPath));
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside, Encoding.UTF8));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteAllTextAtomically_RejectsBlankPath(string path)
    {
        Assert.Throws<ArgumentException>(() => FileWriteService.WriteAllTextAtomically(path, "contents", Encoding.UTF8));
    }

    [Fact]
    public void WriteAllTextAtomically_RejectsNullContents()
    {
        string path = Path.Combine(_rootPath, "settings.json");

        Assert.Throws<ArgumentNullException>(() => FileWriteService.WriteAllTextAtomically(path, null!, Encoding.UTF8));
    }

    [Fact]
    public void WriteAllBytesAtomically_RejectsNullBytes()
    {
        string path = Path.Combine(_rootPath, "settings.bin");

        Assert.Throws<ArgumentNullException>(() => FileWriteService.WriteAllBytesAtomically(path, null!));
    }

    [Fact]
    public void ResolveAvailablePath_RejectsBlankPath()
    {
        Assert.Throws<ArgumentException>(() => FileWriteService.ResolveAvailablePath("   "));
    }

    [Fact]
    public void ResolveAvailablePath_RejectsDirectoryLikePath()
    {
        string path = Path.Combine(_rootPath, "exports") + Path.DirectorySeparatorChar;

        Assert.Throws<ArgumentException>(() => FileWriteService.ResolveAvailablePath(path));
    }

    [Fact]
    public void ResolveAvailablePath_AddsSuffixForExistingFilesAndDirectories()
    {
        Directory.CreateDirectory(_rootPath);
        string path = Path.Combine(_rootPath, "FileLocker-history-20260521-120000.json");
        File.WriteAllText(path, "first");
        Directory.CreateDirectory(Path.Combine(_rootPath, "FileLocker-history-20260521-120000-1.json"));

        string availablePath = FileWriteService.ResolveAvailablePath(path);

        Assert.Equal(Path.Combine(_rootPath, "FileLocker-history-20260521-120000-2.json"), availablePath);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_rootPath, recursive: true);
    }

    private sealed class ThrowingEncoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) =>
            throw new InvalidOperationException("Encoding should not run after cancellation.");

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            throw new InvalidOperationException("Encoding should not run after cancellation.");

        public override int GetCharCount(byte[] bytes, int index, int count) =>
            throw new NotSupportedException();

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            throw new NotSupportedException();

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}

namespace FileLocker.Tests;

public sealed class SecureDeleteTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecureDelete_RemovesWritableFile()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "source.txt");
        File.WriteAllText(filePath, "sensitive");

        MainWindow.SecureDelete(filePath, passes: 1);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void SecureDelete_MissingFileThrows()
    {
        string filePath = Path.Combine(_rootPath, "missing.txt");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => MainWindow.SecureDelete(filePath, passes: 1));

        Assert.Equal(filePath, ex.FileName);
    }

    [Fact]
    public void SecureDelete_RejectsControlCharacterPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.SecureDelete("C:\\bad\r\nsource.txt", passes: 1));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("valid file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureDelete_RejectsUnicodeFormatCharacterPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.SecureDelete(Path.Combine(_rootPath, "source" + "\u202E" + ".txt"), passes: 1));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("valid file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureDelete_RejectsRelativePath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.SecureDelete("source.txt", passes: 1));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("fully qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureDelete_RejectsAlternateDataStreamPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.SecureDelete(Path.Combine(_rootPath, "source.txt:stream"), passes: 1));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureDelete_RejectsAlternateDataStreamParentPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.SecureDelete(Path.Combine(_rootPath, "source:stream", "payload.txt"), passes: 1));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureDelete_RemovesReadOnlyFile()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "readonly.txt");
        File.WriteAllText(filePath, "sensitive");
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);

        MainWindow.SecureDelete(filePath, passes: 1);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void SecureDelete_ThrowsWhenOverwriteFails()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "locked.txt");
        File.WriteAllText(filePath, "sensitive");

        using FileStream locked = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Delete);

        IOException ex = Assert.Throws<IOException>(() => MainWindow.SecureDelete(filePath, passes: 1));

        Assert.Contains("overwrite", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void SecureDelete_RestoresReadOnlyAttributeWhenOverwriteFails()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "locked-readonly.txt");
        File.WriteAllText(filePath, "sensitive");
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);

        using FileStream locked = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        IOException ex = Assert.Throws<IOException>(() => MainWindow.SecureDelete(filePath, passes: 1));

        Assert.Contains("overwrite", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((File.GetAttributes(filePath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
    }

    [Fact]
    public void SecureDelete_RejectsFileLinkWithoutTouchingTarget()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.txt");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target");
        string linkPath = Path.Combine(_rootPath, "linked.txt");

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

            IOException deleteException = Assert.Throws<IOException>(() => MainWindow.SecureDelete(linkPath, passes: 1));

            Assert.Contains("reparse", deleteException.InnerException?.Message ?? deleteException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside));
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
    public void DeleteSourceFile_RemovesReadOnlyFileWithoutSecureDelete()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "readonly-normal-delete.txt");
        File.WriteAllText(filePath, "sensitive");
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);

        MainWindow.DeleteSourceFile(filePath, secureDelete: false);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteSourceFile_RejectsAlternateDataStreamPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.DeleteSourceFile(Path.Combine(_rootPath, "source.txt:stream"), secureDelete: false));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteSourceFile_RejectsRelativePath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.DeleteSourceFile("source.txt", secureDelete: false));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("fully qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteSourceFile_RejectsAlternateDataStreamParentPath()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.DeleteSourceFile(Path.Combine(_rootPath, "source:stream", "payload.txt"), secureDelete: false));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSecureDeleteSourceFile_RejectsFileLinkBeforeOutput()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.txt");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target");
        string linkPath = Path.Combine(_rootPath, "linked.txt");

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

            IOException validationException = Assert.Throws<IOException>(() =>
                MainWindow.ValidateSecureDeleteSourceFile(linkPath, removeOriginalsAfterSuccess: true, secureDeleteOriginals: true));

            Assert.Contains("symlink", validationException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside));
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
    public void ResolveBackupCopyPath_SkipsExistingDirectoryCollision()
    {
        Directory.CreateDirectory(_rootPath);
        string backupFolderPath = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(backupFolderPath);
        string sourcePath = Path.Combine(_rootPath, "source.txt");
        var timestamp = new DateTime(2026, 5, 21, 12, 0, 0);
        Directory.CreateDirectory(Path.Combine(backupFolderPath, "source_20260521_120000.txt"));

        string backupPath = MainWindow.ResolveBackupCopyPath(sourcePath, backupFolderPath, timestamp);

        Assert.Equal(Path.Combine(backupFolderPath, "source_20260521_120000-1.txt"), backupPath);
    }

    [Fact]
    public void CreateBackupCopy_CopiesSourceContents()
    {
        Directory.CreateDirectory(_rootPath);
        string sourcePath = Path.Combine(_rootPath, "source.txt");
        string backupFolderPath = Path.Combine(_rootPath, "backups");
        File.WriteAllText(sourcePath, "sensitive");

        string backupPath = MainWindow.CreateBackupCopy(sourcePath, backupFolderPath);

        Assert.True(File.Exists(backupPath));
        Assert.Equal("sensitive", File.ReadAllText(backupPath));
    }

    [Fact]
    public void CreateBackupCopy_RejectsAlternateDataStreamSourcePath()
    {
        string sourcePath = Path.Combine(_rootPath, "source.txt:stream");
        string backupFolderPath = Path.Combine(_rootPath, "backups");

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.CreateBackupCopy(sourcePath, backupFolderPath));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(backupFolderPath));
    }

    [Fact]
    public void CreateBackupCopy_RejectsRelativeSourcePath()
    {
        string backupFolderPath = Path.Combine(_rootPath, "backups");

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.CreateBackupCopy("source.txt", backupFolderPath));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("fully qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(backupFolderPath));
    }

    [Fact]
    public void CreateBackupCopy_RejectsAlternateDataStreamSourceParentPath()
    {
        string sourcePath = Path.Combine(_rootPath, "source:stream", "payload.txt");
        string backupFolderPath = Path.Combine(_rootPath, "backups");

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.CreateBackupCopy(sourcePath, backupFolderPath));

        Assert.Equal("filePath", ex.ParamName);
        Assert.Contains("normal file path", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(backupFolderPath));
    }

    [Fact]
    public void CreateBackupCopy_RejectsAlternateDataStreamBackupFolderPath()
    {
        Directory.CreateDirectory(_rootPath);
        string sourcePath = Path.Combine(_rootPath, "source.txt");
        File.WriteAllText(sourcePath, "sensitive");

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.CreateBackupCopy(sourcePath, Path.Combine(_rootPath, "backups:stream")));

        Assert.Equal("path", ex.ParamName);
        Assert.Contains("alternate data stream", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBackupCopy_RejectsFileLinkWithoutCopyingTarget()
    {
        Directory.CreateDirectory(_rootPath);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideDirectory, "outside.txt");
        string backupFolderPath = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outside, "external target");
        string linkPath = Path.Combine(_rootPath, "linked.txt");

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

            IOException backupException = Assert.Throws<IOException>(() => MainWindow.CreateBackupCopy(linkPath, backupFolderPath));

            Assert.Contains("reparse", backupException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(backupFolderPath));
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside));
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
}

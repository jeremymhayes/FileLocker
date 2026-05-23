namespace FileLocker.Tests;

public sealed class FolderPackagePathTests
{
    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("nested/file.txt", true)]
    [InlineData(@"nested\file.txt", true)]
    [InlineData(@"..\outside.txt", false)]
    [InlineData(@"nested\..\outside.txt", false)]
    [InlineData(@"C:\outside.txt", false)]
    [InlineData(@"\outside.txt", false)]
    [InlineData(@"CON\file.txt", false)]
    [InlineData(@"COM1.txt", false)]
    [InlineData(@"nested\bad:name.txt", false)]
    [InlineData(@"folder.\file.txt", false)]
    [InlineData("", false)]
    public void IsSafeFolderPackageRelativePath_RejectsUnsafeRestorePaths(string relativePath, bool expected)
    {
        bool actual = MainWindow.IsSafeFolderPackageRelativePath(relativePath);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveFolderPackageEntryPath_RequiresPathToStayUnderRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string resolved = MainWindow.ResolveFolderPackageEntryPath(root, @"nested\file.txt");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "nested", "file.txt")), resolved);
        Assert.Throws<UnauthorizedAccessException>(() => MainWindow.ResolveFolderPackageEntryPath(root, @"..\outside.txt"));
    }

    [Fact]
    public void ValidateFolderPackageEntryLength_AllowsMatchingLength()
    {
        var entry = new FolderPackageEntryMetadata
        {
            RelativePath = "nested/file.txt",
            OriginalSize = 12
        };

        MainWindow.ValidateFolderPackageEntryLength(entry, 12);
    }

    [Theory]
    [InlineData(12, 11)]
    [InlineData(12, -1)]
    [InlineData(-1, 0)]
    public void ValidateFolderPackageEntryLength_RejectsCorruptLengthMetadata(long metadataLength, long payloadLength)
    {
        var entry = new FolderPackageEntryMetadata
        {
            RelativePath = "nested/file.txt",
            OriginalSize = metadataLength
        };

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageEntryLength(entry, payloadLength));
    }

    [Fact]
    public void CreateUserTreeEnumerationOptions_SkipsReparsePoints()
    {
        EnumerationOptions options = MainWindow.CreateUserTreeEnumerationOptions();

        Assert.True(options.RecurseSubdirectories);
        Assert.False(options.IgnoreInaccessible);
        Assert.Equal(FileAttributes.ReparsePoint, options.AttributesToSkip & FileAttributes.ReparsePoint);
    }

    [Fact]
    public void ValidateFolderPackageSourceRoot_RejectsLinkedRootWithoutTouchingTarget()
    {
        string targetRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "target");
        string linkRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "linked-root");
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(Path.Combine(targetRoot, "outside.txt"), "external target");
        Directory.CreateDirectory(Path.GetDirectoryName(linkRoot)!);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkRoot, targetRoot);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            IOException validationException = Assert.Throws<IOException>(() => MainWindow.ValidateFolderPackageSourceRoot(linkRoot));

            Assert.Contains("symlink", validationException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(targetRoot));
            Assert.Equal("external target", File.ReadAllText(Path.Combine(targetRoot, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(linkRoot))
            {
                Directory.Delete(linkRoot, recursive: false);
            }

            string? linkParent = Path.GetDirectoryName(linkRoot);
            if (!string.IsNullOrWhiteSpace(linkParent) && Directory.Exists(linkParent))
            {
                Directory.Delete(linkParent, recursive: true);
            }

            string? targetParent = Path.GetDirectoryName(targetRoot);
            if (!string.IsNullOrWhiteSpace(targetParent) && Directory.Exists(targetParent))
            {
                Directory.Delete(targetParent, recursive: true);
            }
        }
    }

    [Fact]
    public void IsBackupFolderInsideSource_DetectsNestedBackupFolder()
    {
        string sourceRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "source");
        string nestedBackup = Path.Combine(sourceRoot, "backups");
        string siblingBackup = Path.Combine(Path.GetDirectoryName(sourceRoot)!, "backups");

        Assert.True(MainWindow.IsBackupFolderInsideSource(sourceRoot, nestedBackup));
        Assert.True(MainWindow.IsBackupFolderInsideSource(sourceRoot, sourceRoot));
        Assert.False(MainWindow.IsBackupFolderInsideSource(sourceRoot, siblingBackup));
    }

    [Fact]
    public void IsBackupFolderInsideSource_DetectsLinkedBackupFolderTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string linkedBackup = Path.Combine(root, "linked-backup");
        string nestedTarget = Path.Combine(sourceRoot, "backups");

        try
        {
            Directory.CreateDirectory(nestedTarget);
            try
            {
                Directory.CreateSymbolicLink(linkedBackup, nestedTarget);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(MainWindow.IsBackupFolderInsideSource(sourceRoot, linkedBackup));
        }
        finally
        {
            if (Directory.Exists(linkedBackup))
            {
                Directory.Delete(linkedBackup, recursive: false);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsBackupFolderInsideSource_DetectsNewFolderUnderLinkedParent()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string linkedParent = Path.Combine(root, "linked-parent");
        string nestedTarget = Path.Combine(sourceRoot, "output-root");
        string candidateBackup = Path.Combine(linkedParent, "new-backups");

        try
        {
            Directory.CreateDirectory(nestedTarget);
            try
            {
                Directory.CreateSymbolicLink(linkedParent, nestedTarget);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(MainWindow.IsBackupFolderInsideSource(sourceRoot, candidateBackup));
            Assert.False(Directory.Exists(candidateBackup));
        }
        finally
        {
            if (Directory.Exists(linkedParent))
            {
                Directory.Delete(linkedParent, recursive: false);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsDirectoryInsideSource_DetectsNestedOutputFolder()
    {
        string sourceRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "source");
        string nestedOutput = Path.Combine(sourceRoot, "locked-output");
        string siblingOutput = Path.Combine(Path.GetDirectoryName(sourceRoot)!, "locked-output");

        Assert.True(MainWindow.IsDirectoryInsideSource(sourceRoot, nestedOutput));
        Assert.True(MainWindow.IsDirectoryInsideSource(sourceRoot, sourceRoot));
        Assert.False(MainWindow.IsDirectoryInsideSource(sourceRoot, siblingOutput));
    }

    [Fact]
    public void ResolveAvailableDirectoryPath_AddsSuffixForExistingDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string existing = Path.Combine(root, "source_20260521_120000");

        try
        {
            Directory.CreateDirectory(existing);

            string candidate = MainWindow.ResolveAvailableDirectoryPath(existing);

            Assert.Equal($"{existing}_1", candidate);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveTemporaryOutputPath_AddsSuffixWhenTempSiblingExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string finalPath = Path.Combine(root, "payload.locked");
        string existingTempPath = finalPath + ".tmp";

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(existingTempPath, "existing temp");

            string tempPath = MainWindow.ResolveTemporaryOutputPath(finalPath);

            Assert.Equal(Path.Combine(root, "payload.locked_1.tmp"), tempPath);
            Assert.Equal("existing temp", File.ReadAllText(existingTempPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveTemporaryOutputPath_RejectsBlankSuffix()
    {
        Assert.Throws<ArgumentException>(() =>
            MainWindow.ResolveTemporaryOutputPath(Path.Combine(Path.GetTempPath(), "payload.locked"), "   "));
    }

    [Theory]
    [InlineData("report.txt", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("CON", false)]
    [InlineData("COM1.txt", false)]
    [InlineData("report.", false)]
    [InlineData("bad:name.txt", false)]
    public void IsSafeRestoredFileName_RejectsUnsafeMetadataNames(string fileName, bool expected)
    {
        bool actual = MainWindow.IsSafeRestoredFileName(fileName);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("CON")]
    [InlineData("bad:name.txt")]
    public void ResolveDecryptedFileName_FallsBackForUnsafeMetadataNames(string originalFileName)
    {
        string fileName = MainWindow.ResolveDecryptedFileName(
            Path.Combine(Path.GetTempPath(), "payload.locked"),
            originalFileName,
            restoreOriginalFilename: true);

        Assert.Equal("payload", fileName);
    }

    [Fact]
    public void ResolveFolderPackageRootName_UsesSafeMetadataRootName()
    {
        string rootName = MainWindow.ResolveFolderPackageRootName(
            Path.Combine(Path.GetTempPath(), "archive.locked"),
            @"C:\Source\Project Files",
            restoreOriginalFilename: true);

        Assert.Equal("Project Files", rootName);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("CON")]
    [InlineData("bad:name")]
    public void ResolveFolderPackageRootName_FallsBackForUnsafeMetadataRootNames(string rootFolderName)
    {
        string rootName = MainWindow.ResolveFolderPackageRootName(
            Path.Combine(Path.GetTempPath(), "archive.locked"),
            rootFolderName,
            restoreOriginalFilename: true);

        Assert.Equal("archive", rootName);
    }

    [Fact]
    public void CreateNewOutputFileStream_RejectsExistingFileWithoutOverwriting()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(root, "restored.txt");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(filePath, "existing");

            Assert.Throws<IOException>(() => MainWindow.CreateNewOutputFileStream(filePath));

            Assert.Equal("existing", File.ReadAllText(filePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteSourceDirectory_RemovesRegularDirectoryTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(child, "child.txt"), "child");

        MainWindow.DeleteSourceDirectory(root, secureDelete: false);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DeleteSourceDirectory_RemovesReadOnlyDirectoryTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "child.txt"), "child");
        File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.ReadOnly);
        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.ReadOnly);

        try
        {
            MainWindow.DeleteSourceDirectory(root, secureDelete: false);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.SetAttributes(root, FileAttributes.Normal);
                foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TryCleanupPartialFolderRestore_RemovesPartialReadOnlyTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string child = Path.Combine(root, "partial");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "entry.txt"), "partial");
        File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.ReadOnly);
        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.ReadOnly);

        try
        {
            bool cleaned = MainWindow.TryCleanupPartialFolderRestore(root);

            Assert.True(cleaned);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.SetAttributes(root, FileAttributes.Normal);
                foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CalculateFolderPackageProgress_UsesEndPercentForZeroBytePackages()
    {
        double progress = MainWindow.CalculateFolderPackageProgress(
            processedBytes: 0,
            entryBytes: 0,
            entryPercent: 50,
            totalBytes: 0,
            startPercent: 10,
            endPercent: 90);

        Assert.Equal(90, progress);
    }

    [Fact]
    public void CalculateFolderPackageProgress_ClampsEntryPercentForNormalPackages()
    {
        double progress = MainWindow.CalculateFolderPackageProgress(
            processedBytes: 25,
            entryBytes: 50,
            entryPercent: 150,
            totalBytes: 100,
            startPercent: 10,
            endPercent: 90);

        Assert.Equal(70, progress);
    }

    [Fact]
    public void DeleteSourceDirectory_SecureDeleteRemovesFileLinkWithoutTouchingTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "outside.txt");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "external target");
        string linkPath = Path.Combine(root, "linked.txt");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            MainWindow.DeleteSourceDirectory(root, secureDelete: true, secureDeletePasses: 1);

            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(outside));
            Assert.Equal("external target", File.ReadAllText(outside));
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            string? outsideDirectory = Path.GetDirectoryName(outside);
            if (!string.IsNullOrWhiteSpace(outsideDirectory) && Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteSourceDirectory_RemovesDirectoryLinkWithoutTouchingTarget()
    {
        string targetRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "target");
        string linkRoot = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "linked-root");
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(Path.Combine(targetRoot, "outside.txt"), "external target");
        Directory.CreateDirectory(Path.GetDirectoryName(linkRoot)!);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkRoot, targetRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            MainWindow.DeleteSourceDirectory(linkRoot, secureDelete: true, secureDeletePasses: 1);

            Assert.False(Directory.Exists(linkRoot));
            Assert.True(Directory.Exists(targetRoot));
            Assert.Equal("external target", File.ReadAllText(Path.Combine(targetRoot, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(linkRoot))
            {
                Directory.Delete(linkRoot, recursive: false);
            }

            string? linkParent = Path.GetDirectoryName(linkRoot);
            if (!string.IsNullOrWhiteSpace(linkParent) && Directory.Exists(linkParent))
            {
                Directory.Delete(linkParent, recursive: true);
            }

            string? targetParent = Path.GetDirectoryName(targetRoot);
            if (!string.IsNullOrWhiteSpace(targetParent) && Directory.Exists(targetParent))
            {
                Directory.Delete(targetParent, recursive: true);
            }
        }
    }
}

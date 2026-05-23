using FileLocker;

namespace FileLocker.Tests;

public sealed class HashManifestServiceTests
{
    [Fact]
    public async Task CreateManifestAsync_WritesRelativeSha256Manifest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "docs", "alpha.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);

            HashManifestResult result = await HashManifestService.CreateManifestAsync([root], "SHA-256", root, cancellationToken);
            string manifest = await File.ReadAllTextAsync(result.ManifestPath, cancellationToken);

            Assert.Equal("SHA-256", result.Algorithm);
            Assert.Equal(1, result.FileCount);
            Assert.EndsWith(".sha256", result.ManifestPath);
            Assert.Contains("docs/alpha.txt", manifest);
            Assert.DoesNotContain(root, manifest);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_NullAlgorithmDefaultsToSha256()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);

            HashManifestResult result = await HashManifestService.CreateManifestAsync([filePath], null!, root, cancellationToken);

            Assert.Equal("SHA-256", result.Algorithm);
            Assert.EndsWith(".sha256", result.ManifestPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsOnlyMalformedInputsAsNoFiles()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.CreateManifestAsync(["C:\\Temp\\bad\0path.txt"], "SHA-256", root, cancellationToken));

            Assert.Equal("No files were available for the hash manifest.", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsUnsupportedAlgorithm()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);

            ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                HashManifestService.CreateManifestAsync([filePath], "SHA-1", root, cancellationToken));

            Assert.Contains("Unsupported hash algorithm", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsUnsupportedAlgorithmBeforeEnumeratingInputs()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            HashManifestService.CreateManifestAsync(
                ThrowIfEnumerated(),
                "SHA-1",
                Path.GetTempPath(),
                TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported hash algorithm", ex.Message);
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsUnsupportedAlgorithmBeforeCreatingOutputDirectory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            string outputDirectory = Path.Combine(root, "manifest-output");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                HashManifestService.CreateManifestAsync([filePath], "SHA-1", outputDirectory, cancellationToken));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsNullInputPaths()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HashManifestService.CreateManifestAsync(
                null!,
                "SHA-256",
                Path.GetTempPath(),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateManifestAsync_RejectsBlankOutputDirectory(string outputDirectory)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HashManifestService.CreateManifestAsync(
                [],
                "SHA-256",
                outputDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateManifestAsync_CanceledTokenDoesNotCreateOutputDirectory()
    {
        string root = CreateTempDirectory();
        string outputDirectory = Path.Combine(root, "manifest-output");
        string filePath = Path.Combine(root, "alpha.txt");
        await File.WriteAllTextAsync(filePath, "alpha", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                HashManifestService.CreateManifestAsync(
                    [filePath],
                    "SHA-256",
                    outputDirectory,
                    cancellation.Token));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_CanceledDuringInputEnumerationStopsBeforeContinuing()
    {
        string root = CreateTempDirectory();
        string outputDirectory = Path.Combine(root, "manifest-output");
        string filePath = Path.Combine(root, "alpha.txt");
        await File.WriteAllTextAsync(filePath, "alpha", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                HashManifestService.CreateManifestAsync(
                    CancelAfterFirstInput(filePath, cancellation),
                    "SHA-256",
                    outputDirectory,
                    cancellation.Token));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_ReportsMatchedEntries()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);
            HashManifestResult result = await HashManifestService.CreateManifestAsync([filePath], "SHA-256", root, cancellationToken);

            HashManifestVerificationResult verification = await HashManifestService.VerifyManifestAsync(result.ManifestPath, root, cancellationToken);

            Assert.Equal(1, verification.EntryCount);
            Assert.Equal(1, verification.MatchedCount);
            Assert.Equal(0, verification.MismatchedCount);
            Assert.Equal(0, verification.MissingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_TreatsLockedFilesAsMissing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", cancellationToken);
            string hash = await FileHashService.ComputeHashHexAsync(filePath, "SHA-256", cancellationToken: cancellationToken);
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(manifestPath, $"{hash}  alpha.txt{Environment.NewLine}", cancellationToken);

            await using (var lockedFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                HashManifestVerificationResult verification = await HashManifestService.VerifyManifestAsync(manifestPath, root, cancellationToken);

                Assert.Equal(1, verification.EntryCount);
                Assert.Equal(0, verification.MatchedCount);
                Assert.Equal(0, verification.MismatchedCount);
                Assert.Equal(1, verification.MissingCount);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsNullManifestPath()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HashManifestService.VerifyManifestAsync(
                null!,
                Path.GetTempPath(),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VerifyManifestAsync_RejectsBlankManifestPath(string manifestPath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HashManifestService.VerifyManifestAsync(
                manifestPath,
                Path.GetTempPath(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsNullRootDirectory()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(manifestPath, "# empty", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    null!,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VerifyManifestAsync_RejectsBlankRootDirectory(string rootDirectory)
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(manifestPath, "# empty", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    rootDirectory,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_CanceledTokenDoesNotCheckManifestPath()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HashManifestService.VerifyManifestAsync(
                Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "missing.sha256"),
                Path.GetTempPath(),
                cancellation.Token));
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsManifestWithoutEntries()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(
                manifestPath,
                "# FileLocker SHA-256 manifest\r\nnot-a-valid-entry\r\n",
                TestContext.Current.CancellationToken);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("does not contain any verifiable file entries", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseManifest_IgnoresCommentsAndAbsolutePaths()
    {
        string content = """
            # FileLocker SHA-256 manifest
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  relative.txt
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  C:\secret.txt
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  ..\outside.txt
            2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  nested/../outside.txt
            """;

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Single(entries);
        Assert.Equal("relative.txt", entries[0].RelativePath);
    }

    [Fact]
    public void ParseManifest_IgnoresMalformedHashTokens()
    {
        string sha256 = new('a', 64);
        string sha512 = new('B', 128);
        string content = string.Join(
            Environment.NewLine,
            [
                $"{sha256}  sha256.txt",
                $"{sha512}  sha512.txt",
                $"{new string('a', 63)}  too-short.txt",
                $"{new string('g', 64)}  non-hex.txt",
                $"sha256:{sha256}  prefixed.txt"
            ]);

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Equal(2, entries.Count);
        Assert.Equal("sha256.txt", entries[0].RelativePath);
        Assert.Equal("sha512.txt", entries[1].RelativePath);
    }

    [Fact]
    public void ParseManifest_RejectsEscapedControlCharactersWithoutCorruptingLiteralBackslashN()
    {
        string hash = new('a', 64);
        string content = string.Join(
            Environment.NewLine,
            [
                $"{hash}  folder\\\\note.txt",
                $"{hash}  folder\\nname.txt"
            ]);

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Single(entries);
        Assert.Equal(@"folder\note.txt", entries[0].RelativePath);
    }

    [Fact]
    public void ParseManifest_PreservesLeadingSpacesInRelativePaths()
    {
        string hash = new('a', 64);
        string content = $"{hash}    spaced.txt";

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Single(entries);
        Assert.Equal("  spaced.txt", entries[0].RelativePath);
    }

    [Fact]
    public void CreateManifestEnumerationOptions_SkipsReparsePointsAndInaccessibleEntries()
    {
        EnumerationOptions options = HashManifestService.CreateManifestEnumerationOptions();

        Assert.True(options.RecurseSubdirectories);
        Assert.True(options.IgnoreInaccessible);
        Assert.Equal(FileAttributes.ReparsePoint, options.AttributesToSkip & FileAttributes.ReparsePoint);
    }

    [Fact]
    public void GetManifestRelativePath_RejectsCrossVolumePath()
    {
        string currentRoot = Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\";
        string otherRoot = currentRoot.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) ? @"D:\" : @"C:\";
        string commonRoot = Path.Combine(currentRoot, "FileLockerManifestRoot");
        string filePath = Path.Combine(otherRoot, "OtherRoot", "file.txt");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.GetManifestRelativePath(commonRoot, filePath));

        Assert.Contains("common root", ex.Message);
    }

    [Fact]
    public void ResolveAvailableManifestPath_AddsSuffixWhenManifestAlreadyExists()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "FileLocker-manifest-20260521-120000.sha256");
            File.WriteAllText(manifestPath, "first");

            string availablePath = HashManifestService.ResolveAvailableManifestPath(manifestPath);

            Assert.Equal(Path.Combine(root, "FileLocker-manifest-20260521-120000-1.sha256"), availablePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveManifestEntryPath_ResolvesSafeRelativePathInsideRoot()
    {
        string root = CreateTempDirectory();
        try
        {
            bool resolved = HashManifestService.TryResolveManifestEntryPath(root, "docs/alpha.txt", out string filePath);

            Assert.True(resolved);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "docs", "alpha.txt")), filePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"nested/../outside.txt")]
    [InlineData(@"C:\outside.txt")]
    public void TryResolveManifestEntryPath_RejectsPathsOutsideRoot(string relativePath)
    {
        string root = CreateTempDirectory();
        try
        {
            bool resolved = HashManifestService.TryResolveManifestEntryPath(root, relativePath, out string filePath);

            Assert.False(resolved);
            Assert.Equal(string.Empty, filePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpandFiles_DeduplicatesInputsAndIgnoresMissingPaths()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "docs", "alpha.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "alpha");

            string missingPath = Path.Combine(root, "missing");
            string[] files = HashManifestService.ExpandFiles([root, root, missingPath], TestContext.Current.CancellationToken)
                .ToArray();

            Assert.Single(files);
            Assert.Equal(filePath, files[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpandFiles_IgnoresMalformedPaths()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            File.WriteAllText(filePath, "alpha");

            string[] files = HashManifestService.ExpandFiles([
                    "C:\\Temp\\bad\0path.txt",
                    filePath
                ], TestContext.Current.CancellationToken)
                .ToArray();

            string singlePath = Assert.Single(files);
            Assert.Equal(filePath, singlePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpandFiles_SkipsGeneratedManifestFilesDuringDirectoryExpansion()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "docs", "alpha.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "alpha");
            File.WriteAllText(Path.Combine(root, "FileLocker-manifest-20260522-120000.sha256"), "old manifest");
            File.WriteAllText(Path.Combine(root, "FileLocker-manifest-20260522-120000.sha512"), "old manifest");

            string[] files = HashManifestService.ExpandFiles([root], TestContext.Current.CancellationToken)
                .ToArray();

            Assert.Single(files);
            Assert.Equal(filePath, files[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpandFiles_IncludesExplicitlySelectedGeneratedManifestFile()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "FileLocker-manifest-20260522-120000.sha256");
            File.WriteAllText(manifestPath, "old manifest");

            string[] files = HashManifestService.ExpandFiles([manifestPath], TestContext.Current.CancellationToken)
                .ToArray();

            Assert.Single(files);
            Assert.Equal(manifestPath, files[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<string> ThrowIfEnumerated()
    {
        throw new InvalidOperationException("Manifest inputs should not be enumerated.");
#pragma warning disable CS0162
        yield return string.Empty;
#pragma warning restore CS0162
    }

    private static IEnumerable<string> CancelAfterFirstInput(string path, CancellationTokenSource cancellation)
    {
        yield return path;
        cancellation.Cancel();
        yield return path;
        throw new InvalidOperationException("Manifest inputs should not continue after cancellation.");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"FileLockerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

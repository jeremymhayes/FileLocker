using FileLocker;
using System.Reflection;
using System.Text;

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
    public async Task CreateManifestAsync_RejectsMalformedInputPathCharacters()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTempDirectory();
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.CreateManifestAsync(["C:\\Temp\\bad\0path.txt"], "SHA-256", root, cancellationToken));

            Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task CreateManifestAsync_RejectsAlternateDataStreamOutputDirectory()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", TestContext.Current.CancellationToken);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.CreateManifestAsync(
                    [filePath],
                    "SHA-256",
                    Path.Combine(root, "manifest-output:stream"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("output folder", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateManifestAsync_RejectsRelativeOutputDirectory()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            await File.WriteAllTextAsync(filePath, "alpha", TestContext.Current.CancellationToken);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.CreateManifestAsync(
                    [filePath],
                    "SHA-256",
                    "manifest-output",
                    TestContext.Current.CancellationToken));

            Assert.Contains("output folder", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    public void NormalizeManifestFiles_DeduplicatesCaseInsensitivePaths()
    {
        string[] files = HashManifestService.NormalizeManifestFiles(
            [@"C:\Root\Alpha.txt", @"c:\root\alpha.txt", @"C:\Root\Beta.txt"],
            TestContext.Current.CancellationToken);

        Assert.Equal([@"C:\Root\Alpha.txt", @"C:\Root\Beta.txt"], files);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsTooManyFiles()
    {
        IEnumerable<string> files = Enumerable
            .Range(0, HashManifestService.MaxManifestEntries + 1)
            .Select(index => $@"C:\Root\{index}.txt");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles(files, TestContext.Current.CancellationToken));

        Assert.Equal("The hash manifest contains too many files.", ex.Message);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsOversizedPathText()
    {
        string oversizedPath = @"C:\" + new string('a', FileHashService.MaxHashPathChars + 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles([oversizedPath], TestContext.Current.CancellationToken));

        Assert.Equal("A hash manifest file path is too long.", ex.Message);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsControlCharacterPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles(
                [Path.Combine(Path.GetTempPath(), "manifest\tpayload.txt")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsUnicodeFormatCharacterPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles(
                [Path.Combine(Path.GetTempPath(), "payload" + "\u202E" + ".txt")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsAlternateDataStreamPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles(
                [Path.Combine(Path.GetTempPath(), "payload.txt:stream")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestFiles_RejectsRelativePathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestFiles(
                ["payload.txt"],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestInputPaths_DeduplicatesBeforeApplyingLimit()
    {
        string[] paths = HashManifestService.NormalizeManifestInputPaths(
            [@"  C:\Root\Alpha.txt  ", @"c:\root\alpha.txt", @"C:\Root\Beta.txt"],
            TestContext.Current.CancellationToken);

        Assert.Equal([@"C:\Root\Alpha.txt", @"C:\Root\Beta.txt"], paths);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsOversizedPathText()
    {
        string oversizedPath = @"C:\" + new string('a', FileHashService.MaxHashPathChars + 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths([oversizedPath], TestContext.Current.CancellationToken));

        Assert.Equal("A hash manifest input path is too long.", ex.Message);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsControlCharacterPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths(
                [Path.Combine(Path.GetTempPath(), "manifest\r\npayload.txt")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsUnicodeFormatCharacterPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths(
                [Path.Combine(Path.GetTempPath(), "payload" + "\u202E" + ".txt")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsAlternateDataStreamPathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths(
                [Path.Combine(Path.GetTempPath(), "payload.txt:stream")],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsRelativePathText()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths(
                ["payload.txt"],
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeManifestInputPaths_RejectsTooManyInputPaths()
    {
        IEnumerable<string> paths = Enumerable
            .Range(0, HashManifestService.MaxManifestEntries + 1)
            .Select(index => $@"C:\Root\{index}.txt");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            HashManifestService.NormalizeManifestInputPaths(paths, TestContext.Current.CancellationToken));

        Assert.Equal("The hash manifest contains too many input paths.", ex.Message);
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
    public async Task VerifyManifestAsync_RejectsAlternateDataStreamManifestPath()
    {
        string root = CreateTempDirectory();
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    Path.Combine(root, "manifest.sha256:stream"),
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("manifest path", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsRelativeManifestPath()
    {
        string root = CreateTempDirectory();
        try
        {
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    "manifest.sha256",
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("manifest path", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsAlternateDataStreamRootDirectory()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(manifestPath, "# empty", TestContext.Current.CancellationToken);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    Path.Combine(root, "root:stream"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("root folder", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsRelativeRootDirectory()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(manifestPath, "# empty", TestContext.Current.CancellationToken);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    "manifest-root",
                    TestContext.Current.CancellationToken));

            Assert.Contains("root folder", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task VerifyManifestAsync_RejectsUnsupportedManifestExtension()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "payload.txt");
            await File.WriteAllTextAsync(filePath, "payload", TestContext.Current.CancellationToken);

            string manifestPath = Path.Combine(root, "manifest.txt");
            await File.WriteAllTextAsync(
                manifestPath,
                $"{new string('a', 64)}  payload.txt",
                TestContext.Current.CancellationToken);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains(".sha256 or .sha512", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
                "# FileLocker SHA-256 manifest\r\n# no entries\r\n",
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
    public async Task VerifyManifestAsync_RejectsMalformedManifestEntries()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "payload.txt");
            await File.WriteAllTextAsync(filePath, "payload", TestContext.Current.CancellationToken);
            string sha256 = await FileHashService.ComputeHashHexAsync(
                filePath,
                FileHashService.Sha256,
                cancellationToken: TestContext.Current.CancellationToken);
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(
                manifestPath,
                $"{sha256}  payload.txt{Environment.NewLine}not-a-valid-entry",
                TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("malformed or unsafe", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsOversizedManifestLines()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(
                manifestPath,
                $"{new string('a', HashManifestService.MaxManifestLineChars + 1)}{Environment.NewLine}",
                TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("line that is too long", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsTooManyManifestLines()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await using (var writer = new StreamWriter(manifestPath, append: false, Encoding.UTF8))
            {
                for (int index = 0; index <= HashManifestService.MaxManifestLines; index++)
                {
                    await writer.WriteLineAsync("# comment");
                }
            }

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("too many lines", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData("payload.txt:stream")]
    [InlineData("payload\u202E.txt")]
    public async Task VerifyManifestAsync_RejectsUnsafeManifestEntryPaths(string relativePath)
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.sha256");
            await File.WriteAllTextAsync(
                manifestPath,
                $"{new string('a', 64)}  {relativePath}",
                TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("malformed or unsafe", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyManifestAsync_RejectsEntriesThatDoNotMatchManifestAlgorithmLength()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "payload.txt");
            await File.WriteAllTextAsync(filePath, "payload", TestContext.Current.CancellationToken);

            string sha256 = await FileHashService.ComputeHashHexAsync(
                filePath,
                FileHashService.Sha256,
                cancellationToken: TestContext.Current.CancellationToken);
            string manifestPath = Path.Combine(root, "manifest.sha512");
            await File.WriteAllTextAsync(
                manifestPath,
                $"{sha256}  payload.txt",
                TestContext.Current.CancellationToken);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                HashManifestService.VerifyManifestAsync(
                    manifestPath,
                    root,
                    TestContext.Current.CancellationToken));

            Assert.Contains("malformed or unsafe", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseManifest_IgnoresCommentsAndAbsolutePaths()
    {
        string hash = "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";
        string content = string.Join(
            Environment.NewLine,
            [
                "# FileLocker SHA-256 manifest",
                $"{hash}  relative.txt",
                $@"{hash}  C:\secret.txt",
                $@"{hash}  ..\outside.txt",
                $"{hash}  nested/../outside.txt",
                $"{hash}  payload.txt:stream",
                $"{hash}  payload\u202E.txt"
            ]);

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
    public void ParseManifest_IgnoresOversizedLines()
    {
        string hash = new('a', 64);
        string content = string.Join(
            Environment.NewLine,
            [
                $"{hash}  alpha.txt",
                new string('b', HashManifestService.MaxManifestLineChars + 1),
                $"{hash}  beta.txt"
            ]);

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(content);

        Assert.Equal(2, entries.Count);
        Assert.Equal("alpha.txt", entries[0].RelativePath);
        Assert.Equal("beta.txt", entries[1].RelativePath);
    }

    [Fact]
    public void ParseManifest_StopsAfterLineLimit()
    {
        string hash = new('a', 64);
        var builder = new StringBuilder();
        for (int index = 0; index <= HashManifestService.MaxManifestLines; index++)
        {
            builder.AppendLine("# comment");
        }

        builder.AppendLine($"{hash}  late.txt");

        List<HashManifestEntry> entries = HashManifestService.ParseManifest(builder.ToString());

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseManifest_RejectsNullContent()
    {
        Assert.Throws<ArgumentNullException>(() => HashManifestService.ParseManifest(null!));
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
    public void FindCommonDirectory_PreservesFilesystemRoot()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("No filesystem root was available for the test.");
        string child = Path.Combine(root, "FileLockerManifestRoot", "nested");

        string common = InvokeFindCommonDirectory([root, child]);

        Assert.Equal(root, common);
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
    public void ExpandFiles_IgnoresAlternateDataStreamPaths()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            File.WriteAllText(filePath, "alpha");

            string[] files = HashManifestService.ExpandFiles([
                    Path.Combine(root, "alpha.txt:stream"),
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
    public void ExpandFiles_IgnoresRelativeAndUnicodeFormatPaths()
    {
        string root = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(root, "alpha.txt");
            File.WriteAllText(filePath, "alpha");

            string[] files = HashManifestService.ExpandFiles([
                    "alpha.txt",
                    Path.Combine(root, "payload" + "\u202E" + ".txt"),
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

    private static string InvokeFindCommonDirectory(IReadOnlyList<string> directories)
    {
        MethodInfo method = typeof(HashManifestService).GetMethod("FindCommonDirectory", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FindCommonDirectory method not found.");

        return (string)(method.Invoke(null, [directories])
            ?? throw new InvalidOperationException("FindCommonDirectory returned null."));
    }
}

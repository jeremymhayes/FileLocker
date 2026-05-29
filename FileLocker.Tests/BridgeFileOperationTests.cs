namespace FileLocker.Tests;

public sealed class BridgeFileOperationTests
{
    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_RejectsFolderRemovalWithoutConfirmation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                MainWindow.ValidateFolderSourceRemovalConfirmation(
                    removeOriginalsAfterSuccess: true,
                    [directory],
                    deleteConfirmation: ""));

            Assert.Equal("Folder-wide source removal requires typing DELETE before the run starts.", ex.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_AllowsFolderRemovalWithExactConfirmation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            MainWindow.ValidateFolderSourceRemovalConfirmation(
                removeOriginalsAfterSuccess: true,
                [directory],
                deleteConfirmation: "DELETE");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidateFolderSourceRemovalConfirmation_AllowsFileOnlyRemovalWithoutFolderConfirmation()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"FileLocker-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(filePath, "contents");
        try
        {
            MainWindow.ValidateFolderSourceRemovalConfirmation(
                removeOriginalsAfterSuccess: true,
                [filePath],
                deleteConfirmation: "");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsNullPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths(null));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsEmptyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths([]));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsBlankOnlyPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths([" ", "\t"]));

        Assert.Equal("Select at least one file or folder.", ex.Message);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsControlCharacterPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths(["C:\\Temp\\bad\0path.txt"]));

        Assert.Equal("A selected value contains invalid characters.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_RejectsAlternateDataStreamPaths()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateFileOperationBridgePaths([Path.Combine(Path.GetTempPath(), "payload.txt:stream")]));

        Assert.Contains("normal files or folders", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateFileOperationBridgePaths_NormalizesAndDeduplicatesPaths()
    {
        string path = Path.Combine(Path.GetTempPath(), "payload.txt");
        string fullPath = Path.GetFullPath(path);

        string[] validated = MainWindow.ValidateFileOperationBridgePaths([
            $"  {path}  ",
            "",
            fullPath
        ]);

        string singlePath = Assert.Single(validated);
        Assert.Equal(fullPath, singlePath);
    }

    [Fact]
    public void ValidateBridgeUnlockSecret_AllowsRecoveryKeyOnlyForReadOperations()
    {
        MainWindow.ValidateBridgeUnlockSecret(
            encryptingNewPayload: false,
            password: "",
            recoveryKey: "RECOVERY-KEY");
    }

    [Fact]
    public void ValidateBridgeUnlockSecret_RejectsMissingReadUnlockMaterial()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateBridgeUnlockSecret(
                encryptingNewPayload: false,
                password: "",
                recoveryKey: ""));

        Assert.Equal("Enter the unlock password or recovery key.", ex.Message);
    }

    [Fact]
    public void ValidateBridgeUnlockSecret_RejectsRecoveryKeyOnlyForEncryption()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidateBridgeUnlockSecret(
                encryptingNewPayload: true,
                password: "",
                recoveryKey: "RECOVERY-KEY"));

        Assert.Equal("Enter a password before encrypting.", ex.Message);
    }

    [Fact]
    public void ValidateBridgeUnlockSecret_RejectsOversizedPassword()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.ValidateBridgeUnlockSecret(
                encryptingNewPayload: true,
                password: new string('A', KdfSecretValidator.MaxSecretTextBytes + 1),
                recoveryKey: null));

        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateBridgeUnlockSecret_RejectsOversizedRecoveryKeyForReadOperations()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.ValidateBridgeUnlockSecret(
                encryptingNewPayload: false,
                password: "",
                recoveryKey: new string('A', KdfSecretValidator.MaxSecretTextBytes + 1)));

        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeBridgeEncryptionAlgorithm_RejectsUnavailableAlgorithmsForEncryption()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            MainWindow.NormalizeBridgeEncryptionAlgorithm(
                EncryptionAlgorithmCatalog.XChaCha20Poly1305,
                encryptingNewPayload: true));

        Assert.Contains("not supported by this build", ex.Message);
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.XChaCha20Poly1305)]
    [InlineData("Unknown-Stale-Ui-Value")]
    [InlineData(null)]
    public void NormalizeBridgeEncryptionAlgorithm_IgnoresSelectedAlgorithmForReadOperations(string? algorithm)
    {
        string normalized = MainWindow.NormalizeBridgeEncryptionAlgorithm(
            algorithm,
            encryptingNewPayload: false);

        Assert.Equal(EncryptionAlgorithmCatalog.Aes256Gcm, normalized);
    }

    [Theory]
    [InlineData(null, "SHA-256")]
    [InlineData("SHA256", "SHA-256")]
    [InlineData("SHA-512", "SHA-512")]
    [InlineData("sha512", "SHA-512")]
    public void NormalizeBridgeHashAlgorithm_UsesSharedHashAlgorithmNames(string? algorithm, string expected)
    {
        string normalized = MainWindow.NormalizeBridgeHashAlgorithm(algorithm);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizeBridgeHashAlgorithm_RejectsUnsupportedAlgorithms()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            MainWindow.NormalizeBridgeHashAlgorithm("SHA-1"));

        Assert.Contains("Unsupported hash algorithm", ex.Message);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"FileLocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}

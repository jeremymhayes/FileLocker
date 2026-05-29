using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FileLocker.Tests;

public sealed class PayloadMetadataValidationTests
{
    [Fact]
    public void ReadLegacyPayloadLayout_ReturnsExpectedOffsets()
    {
        byte[] plaintext = BuildLegacyPlaintext(metadataLength: 12, paddingLength: 5, fileLength: 7);

        var layout = MainWindow.ReadLegacyPayloadLayout(plaintext);

        Assert.Equal(4, layout.MetadataOffset);
        Assert.Equal(12, layout.MetadataLength);
        Assert.Equal(5, layout.PaddingLength);
        Assert.Equal(25, layout.FileDataOffset);
    }

    [Theory]
    [MemberData(nameof(MalformedLegacyPlaintexts))]
    public void ReadLegacyPayloadLayout_RejectsMalformedLengths(byte[] plaintext)
    {
        Assert.Throws<InvalidDataException>(() => MainWindow.ReadLegacyPayloadLayout(plaintext));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(64)]
    public void GetLegacyPayloadCiphertextLength_RejectsTruncatedPayloads(long payloadLength)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            MainWindow.GetLegacyPayloadCiphertextLength(payloadLength));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetLegacyPayloadCiphertextLength_ReturnsValidatedCiphertextLength()
    {
        Assert.Equal(4, MainWindow.GetLegacyPayloadCiphertextLength(65));
    }

    [Fact]
    public void DeserializeMetadata_RejectsOversizedLegacyTextFields()
    {
        byte[] metadata = BuildLegacyMetadataWithAlgorithm(new string('A', 32 * 1024 + 1));

        Exception ex = InvokeDeserializeMetadataException(metadata);

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("invalid text field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeMetadata_RejectsInvalidUtf8LegacyTextFields()
    {
        byte[] metadata = BuildLegacyMetadataWithAlgorithmBytes([0xC3, 0x28]);

        Exception ex = InvokeDeserializeMetadataException(metadata);

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateFilePayloadMetadata_AllowsNonNegativeLengths()
    {
        FilePayloadMetadata metadata = BuildFilePayloadMetadata();

        MainWindow.ValidateFilePayloadMetadata(metadata);
    }

    [Fact]
    public void ValidateFilePayloadMetadata_RejectsNullMetadata()
    {
        Assert.Throws<ArgumentNullException>(() => MainWindow.ValidateFilePayloadMetadata(null!));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ValidateFilePayloadMetadata_RejectsNegativeLengths(long originalSize, long paddingLength)
    {
        FilePayloadMetadata metadata = BuildFilePayloadMetadata();
        metadata.OriginalSize = originalSize;
        metadata.ContentPaddingLength = paddingLength;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFilePayloadMetadata(metadata));
    }

    [Theory]
    [InlineData(nameof(FilePayloadMetadata.CreationTimeUtc))]
    [InlineData(nameof(FilePayloadMetadata.LastWriteTimeUtc))]
    [InlineData(nameof(FilePayloadMetadata.LastAccessTimeUtc))]
    public void ValidateFilePayloadMetadata_RejectsInvalidTimestamps(string timestampName)
    {
        FilePayloadMetadata metadata = BuildFilePayloadMetadata();
        SetInvalidFilePayloadTimestamp(metadata, timestampName);

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFilePayloadMetadata(metadata));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public void ValidateFilePayloadMetadata_RejectsInvalidContentHashes(string hash)
    {
        FilePayloadMetadata metadata = BuildFilePayloadMetadata();
        metadata.ContentHashBase64 = hash;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFilePayloadMetadata(metadata));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidatePayloadMetadataAlgorithm_AllowsMissingLegacyAesMetadata(string? algorithm)
    {
        MainWindow.ValidatePayloadMetadataAlgorithm(
            algorithm,
            0,
            EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
            PayloadChunkedService.LegacyVersion,
            "File payload metadata");
    }

    [Theory]
    [InlineData("AES-GCM")]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    public void ValidatePayloadMetadataAlgorithm_AllowsMatchingCurrentAlgorithm(string algorithm)
    {
        MainWindow.ValidatePayloadMetadataAlgorithm(
            algorithm,
            256,
            EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
            PayloadChunkedService.CurrentVersion,
            "File payload metadata");
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsHeaderMismatch()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                EncryptionAlgorithmCatalog.ChaCha20Poly1305,
                256,
                EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsUnsupportedAlgorithmName()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                "AES-128-CBC",
                256,
                EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsUnsupportedHeaderAlgorithm()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                null,
                0,
                255,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsMissingAlgorithmForCurrentAesHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                null,
                256,
                EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsMissingAlgorithmForNonAesHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                null,
                256,
                EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsMissingKeySizeForCurrentAesHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                EncryptionAlgorithmCatalog.Aes256Gcm,
                0,
                EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsMissingKeySizeForNonAesHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                EncryptionAlgorithmCatalog.ChaCha20Poly1305,
                0,
                EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_RejectsInvalidKeySize()
    {
        Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePayloadMetadataAlgorithm(
                EncryptionAlgorithmCatalog.Aes256Gcm,
                128,
                EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm,
                PayloadChunkedService.CurrentVersion,
                "File payload metadata"));
    }

    [Fact]
    public void ValidatePayloadMetadataAlgorithm_UsesCatalogKeySizeForHeaderAlgorithm()
    {
        int expectedKeySize = EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305);

        MainWindow.ValidatePayloadMetadataAlgorithm(
            EncryptionAlgorithmCatalog.ChaCha20Poly1305,
            expectedKeySize,
            EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305,
            PayloadChunkedService.CurrentVersion,
            "File payload metadata");
    }

    [Theory]
    [InlineData("{\"Kind\":\"folder-package\"}")]
    [InlineData("{\"kind\":\"folder-package\"}")]
    [InlineData("{\"KIND\":\"folder-package\"}")]
    [InlineData("{ \"Entries\": [], \"Kind\" : \"folder-package\" }")]
    public void ReadPayloadMetadataKind_ReadsFolderPackageKind(string json)
    {
        string kind = MainWindow.ReadPayloadMetadataKind(Encoding.UTF8.GetBytes(json));

        Assert.Equal(PayloadKinds.FolderPackage, kind);
    }

    [Fact]
    public void PayloadJsonOptions_ReadsMetadataPropertiesCaseInsensitively()
    {
        JsonSerializerOptions options = GetMainWindowJsonOptions();
        FilePayloadMetadata? metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(
            "{\"kind\":\"file\",\"originalFileName\":\"case.txt\",\"originalSize\":12,\"contentHashBase64\":\"\"}",
            options);

        Assert.NotNull(metadata);
        Assert.Equal(PayloadKinds.File, metadata.Kind);
        Assert.Equal("case.txt", metadata.OriginalFileName);
        Assert.Equal(12, metadata.OriginalSize);
    }

    [Fact]
    public void ReadPayloadMetadataKind_DefaultsMissingKindToFile()
    {
        string kind = MainWindow.ReadPayloadMetadataKind(Encoding.UTF8.GetBytes("{\"OriginalSize\":0}"));

        Assert.Equal(PayloadKinds.File, kind);
    }

    [Theory]
    [InlineData("{\"Kind\":\"file\",\"kind\":\"folder-package\"}")]
    [InlineData("{\"KIND\":\"folder-package\",\"Kind\":\"file\"}")]
    [InlineData("{\"Kind\":\"file\",\"Algorithm\":\"AES-256-GCM\",\"algorithm\":\"ChaCha20-Poly1305\"}")]
    [InlineData("{\"Kind\":\"folder-package\",\"Entries\":[{\"RelativePath\":\"a.txt\",\"relativePath\":\"b.txt\"}]}")]
    public void ReadPayloadMetadataKind_RejectsDuplicateProperties(string json)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            MainWindow.ReadPayloadMetadataKind(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"Kind\":123}")]
    [InlineData("{\"Kind\":\"archive\"}")]
    [InlineData("{not-json")]
    public void ReadPayloadMetadataKind_RejectsMalformedMetadata(string json)
    {
        Assert.Throws<InvalidDataException>(() => MainWindow.ReadPayloadMetadataKind(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_AllowsSafeEntries()
    {
        var metadata = BuildFolderPackageMetadata();

        MainWindow.ValidateFolderPackageMetadata(metadata);
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsNullMetadata()
    {
        Assert.Throws<ArgumentNullException>(() => MainWindow.ValidateFolderPackageMetadata(null!));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsNegativePaddingLength()
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.PackagePaddingLength = -1;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"C:\outside.txt")]
    public void ValidateFolderPackageMetadata_RejectsUnsafeEntryPaths(string relativePath)
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries[0].RelativePath = relativePath;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsMissingEntryList()
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries = null!;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsNullEntries()
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries.Add(null!);

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Theory]
    [InlineData(@"nested\file.txt", "nested/file.txt")]
    [InlineData(@"nested\file.txt", "nested//file.txt")]
    [InlineData("Report.txt", "report.txt")]
    public void ValidateFolderPackageMetadata_RejectsDuplicateEntryPaths(string firstPath, string secondPath)
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries[0].RelativePath = firstPath;
        metadata.Entries.Add(new FolderPackageEntryMetadata
        {
            RelativePath = secondPath,
            OriginalSize = 3,
            CreationTimeUtc = ValidPayloadTimestamp(),
            LastWriteTimeUtc = ValidPayloadTimestamp(),
            LastAccessTimeUtc = ValidPayloadTimestamp(),
            ContentHashBase64 = Convert.ToBase64String(SHA256.HashData([4, 5, 6]))
        });

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsInvalidEntrySize()
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries[0].OriginalSize = -1;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Fact]
    public void ValidateFolderPackageMetadata_RejectsOverflowingTotalEntrySize()
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries[0].OriginalSize = long.MaxValue;
        metadata.Entries.Add(new FolderPackageEntryMetadata
        {
            RelativePath = "other.txt",
            OriginalSize = 1,
            CreationTimeUtc = ValidPayloadTimestamp(),
            LastWriteTimeUtc = ValidPayloadTimestamp(),
            LastAccessTimeUtc = ValidPayloadTimestamp(),
            ContentHashBase64 = Convert.ToBase64String(SHA256.HashData([4, 5, 6]))
        });

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Theory]
    [InlineData(nameof(FolderPackageEntryMetadata.CreationTimeUtc))]
    [InlineData(nameof(FolderPackageEntryMetadata.LastWriteTimeUtc))]
    [InlineData(nameof(FolderPackageEntryMetadata.LastAccessTimeUtc))]
    public void ValidateFolderPackageMetadata_RejectsInvalidEntryTimestamps(string timestampName)
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        SetInvalidFolderPackageEntryTimestamp(metadata.Entries[0], timestampName);

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public void ValidateFolderPackageMetadata_RejectsInvalidEntryHashes(string hash)
    {
        FolderPackageMetadata metadata = BuildFolderPackageMetadata();
        metadata.Entries[0].ContentHashBase64 = hash;

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFolderPackageMetadata(metadata));
    }

    [Fact]
    public void NormalizeRestoredFileAttributes_StripsUnsafeStructuralFlags()
    {
        FileAttributes attributes =
            FileAttributes.Archive |
            FileAttributes.Hidden |
            FileAttributes.Directory |
            FileAttributes.ReparsePoint;

        FileAttributes normalized = MainWindow.NormalizeRestoredFileAttributes(attributes);

        Assert.Equal(FileAttributes.Archive | FileAttributes.Hidden, normalized);
    }

    [Fact]
    public void NormalizeRestoredFileAttributes_DefaultsEmptyOrStructuralOnlyValuesToNormal()
    {
        Assert.Equal(FileAttributes.Normal, MainWindow.NormalizeRestoredFileAttributes(0));
        Assert.Equal(FileAttributes.Normal, MainWindow.NormalizeRestoredFileAttributes(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void DecompressData_RestoresGzipPayload()
    {
        byte[] plaintext = [1, 2, 3, 4, 5];
        byte[] compressed = CompressForTest(plaintext);

        byte[] restored = MainWindow.DecompressData(compressed, TestContext.Current.CancellationToken);

        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void DecompressData_ObservesPreCanceledToken()
    {
        byte[] compressed = CompressForTest([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MainWindow.DecompressData(compressed, cancellation.Token));
    }

    [Fact]
    public void ValidatePngCarrierSourceSize_AllowsConfiguredLimit()
    {
        MainWindow.ValidatePngCarrierSourceSize(MainWindow.MaxPngCarrierSourceBytes);
    }

    [Fact]
    public void ValidatePngCarrierSourceSize_RejectsOversizedInputs()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidatePngCarrierSourceSize(MainWindow.MaxPngCarrierSourceBytes + 1));

        Assert.Contains("PNG carrier mode", ex.Message);
        Assert.Contains("standard .locked", ex.Message);
    }

    [Fact]
    public void ValidatePngCarrierQueueSizes_RejectsOversizedCarrierQueueBeforePartialRun()
    {
        var oversized = new QueuedFileItem(
            Path.Combine(Path.GetTempPath(), "large.bin"),
            Path.GetTempPath(),
            sourceRootIsFolder: false,
            MainWindow.MaxPngCarrierSourceBytes + 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ValidatePngCarrierQueueSizes([oversized], useSteganography: true));

        Assert.Contains("large.bin", ex.Message);
        Assert.Contains("PNG carrier mode", ex.Message);
    }

    [Fact]
    public void ValidatePngCarrierQueueSizes_AllowsOversizedQueueWhenCarrierModeIsOff()
    {
        var oversized = new QueuedFileItem(
            Path.Combine(Path.GetTempPath(), "large.bin"),
            Path.GetTempPath(),
            sourceRootIsFolder: false,
            MainWindow.MaxPngCarrierSourceBytes + 1);

        MainWindow.ValidatePngCarrierQueueSizes([oversized], useSteganography: false);
    }

    public static TheoryData<byte[]> MalformedLegacyPlaintexts()
    {
        return new TheoryData<byte[]>
        {
            Array.Empty<byte>(),
            WriteInt32(-1),
            WriteInt32(0),
            WriteInt32(8),
            BuildLegacyPlaintext(metadataLength: 2, paddingLength: -1, fileLength: 0),
            BuildLegacyPlaintext(metadataLength: 2, paddingLength: 8, fileLength: 1, trimBytes: 4)
        };
    }

    private static byte[] BuildLegacyPlaintext(int metadataLength, int paddingLength, int fileLength, int trimBytes = 0)
    {
        byte[] plaintext = new byte[4 + metadataLength + 4 + Math.Max(paddingLength, 0) + fileLength];
        WriteInt32(metadataLength).CopyTo(plaintext, 0);
        WriteInt32(paddingLength).CopyTo(plaintext, 4 + metadataLength);

        if (trimBytes <= 0)
        {
            return plaintext;
        }

        return plaintext[..^trimBytes];
    }

    private static byte[] WriteInt32(int value)
    {
        byte[] buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] CompressForTest(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(plaintext);
        }

        return output.ToArray();
    }

    private static FolderPackageMetadata BuildFolderPackageMetadata()
    {
        DateTime timestamp = ValidPayloadTimestamp();

        return new FolderPackageMetadata
        {
            RootFolderName = "source",
            PackagePaddingLength = 0,
            Entries =
            [
                new FolderPackageEntryMetadata
                {
                    RelativePath = @"nested\file.txt",
                    OriginalSize = 3,
                    CreationTimeUtc = timestamp,
                    LastWriteTimeUtc = timestamp,
                    LastAccessTimeUtc = timestamp,
                    ContentHashBase64 = Convert.ToBase64String(SHA256.HashData([1, 2, 3]))
                }
            ]
        };
    }

    private static FilePayloadMetadata BuildFilePayloadMetadata()
    {
        DateTime timestamp = ValidPayloadTimestamp();
        return new FilePayloadMetadata
        {
            OriginalSize = 0,
            ContentPaddingLength = 0,
            CreationTimeUtc = timestamp,
            LastWriteTimeUtc = timestamp,
            LastAccessTimeUtc = timestamp,
            ContentHashBase64 = Convert.ToBase64String(SHA256.HashData([1, 2, 3]))
        };
    }

    private static DateTime ValidPayloadTimestamp()
    {
        return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime InvalidPayloadTimestamp()
    {
        return new DateTime(1600, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    }

    private static void SetInvalidFilePayloadTimestamp(FilePayloadMetadata metadata, string timestampName)
    {
        DateTime invalidTimestamp = InvalidPayloadTimestamp();
        switch (timestampName)
        {
            case nameof(FilePayloadMetadata.CreationTimeUtc):
                metadata.CreationTimeUtc = invalidTimestamp;
                break;
            case nameof(FilePayloadMetadata.LastWriteTimeUtc):
                metadata.LastWriteTimeUtc = invalidTimestamp;
                break;
            case nameof(FilePayloadMetadata.LastAccessTimeUtc):
                metadata.LastAccessTimeUtc = invalidTimestamp;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(timestampName), timestampName, "Unknown timestamp property.");
        }
    }

    private static void SetInvalidFolderPackageEntryTimestamp(FolderPackageEntryMetadata entry, string timestampName)
    {
        DateTime invalidTimestamp = InvalidPayloadTimestamp();
        switch (timestampName)
        {
            case nameof(FolderPackageEntryMetadata.CreationTimeUtc):
                entry.CreationTimeUtc = invalidTimestamp;
                break;
            case nameof(FolderPackageEntryMetadata.LastWriteTimeUtc):
                entry.LastWriteTimeUtc = invalidTimestamp;
                break;
            case nameof(FolderPackageEntryMetadata.LastAccessTimeUtc):
                entry.LastAccessTimeUtc = invalidTimestamp;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(timestampName), timestampName, "Unknown timestamp property.");
        }
    }

    private static byte[] BuildLegacyMetadataWithAlgorithm(string algorithm)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteLegacyMetadataPrefix(writer);
        writer.Write(algorithm);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildLegacyMetadataWithAlgorithmBytes(byte[] algorithmBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteLegacyMetadataPrefix(writer);
        writer.Write((byte)algorithmBytes.Length);
        writer.Write(algorithmBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteLegacyMetadataPrefix(BinaryWriter writer)
    {
        writer.Write("payload.txt");
        writer.Write(1L);
        writer.Write(DateTime.UtcNow.ToBinary());
        writer.Write(DateTime.UtcNow.ToBinary());
        writer.Write(false);
        writer.Write(false);
        writer.Write(0);
    }

    private static Exception InvokeDeserializeMetadataException(byte[] metadata)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("DeserializeMetadata", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeserializeMetadata method not found.");

        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [metadata]));
        return ex.InnerException ?? ex;
    }

    private static JsonSerializerOptions GetMainWindowJsonOptions()
    {
        FieldInfo field = typeof(MainWindow).GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("JsonOptions field not found.");
        return (JsonSerializerOptions?)field.GetValue(null)
            ?? throw new InvalidOperationException("JsonOptions was null.");
    }
}

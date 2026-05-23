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

    [Fact]
    public void ValidateFilePayloadMetadata_AllowsNonNegativeLengths()
    {
        var metadata = new FilePayloadMetadata
        {
            OriginalSize = 0,
            ContentPaddingLength = 0
        };

        MainWindow.ValidateFilePayloadMetadata(metadata);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ValidateFilePayloadMetadata_RejectsNegativeLengths(long originalSize, long paddingLength)
    {
        var metadata = new FilePayloadMetadata
        {
            OriginalSize = originalSize,
            ContentPaddingLength = paddingLength
        };

        Assert.Throws<InvalidDataException>(() => MainWindow.ValidateFilePayloadMetadata(metadata));
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
        BitConverter.GetBytes(value).CopyTo(buffer, 0);
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
}

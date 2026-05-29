using System.Buffers.Binary;
using System.Text;

namespace FileLocker.Tests;

public sealed class StegoPayloadTests
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void TryExtractStegoPayload_ReturnsEmbeddedPayload()
    {
        byte[] expectedPayload = [1, 2, 3, 4, 5];
        string path = WriteTempFile(BuildPng(
            ("tEXt", new byte[32]),
            ("flDR", expectedPayload),
            ("IEND", Array.Empty<byte>())));

        try
        {
            byte[]? payload = MainWindow.TryExtractStegoPayload(path);

            Assert.Equal(expectedPayload, payload);
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    [Fact]
    public void ContainsStegoPayload_ReturnsTrueWithoutExtractingPayload()
    {
        string path = WriteTempFile(BuildPng(
            ("tEXt", new byte[32]),
            ("flDR", [1, 2, 3, 4, 5]),
            ("IEND", Array.Empty<byte>())));

        try
        {
            Assert.True(MainWindow.ContainsStegoPayload(path));
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    [Fact]
    public void TryExtractStegoPayload_ReturnsNullForNonPng()
    {
        string path = WriteTempFile(Encoding.UTF8.GetBytes("not a png"));

        try
        {
            Assert.Null(MainWindow.TryExtractStegoPayload(path));
            Assert.False(MainWindow.ContainsStegoPayload(path));
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    [Fact]
    public void TryExtractStegoPayload_ReturnsNullForTruncatedChunk()
    {
        using var stream = new MemoryStream();
        stream.Write(PngSignature);
        WriteChunkHeader(stream, "flDR", length: 10);
        stream.Write([1, 2, 3]);
        string path = WriteTempFile(stream.ToArray());

        try
        {
            Assert.Null(MainWindow.TryExtractStegoPayload(path));
            Assert.False(MainWindow.ContainsStegoPayload(path));
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    [Fact]
    public void ContainsStegoPayload_ReturnsFalseForOversizedPayloadChunk()
    {
        string path = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "oversized-carrier.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int oversizedLength = checked((int)MainWindow.MaxPngCarrierPayloadBytes + 1);

        try
        {
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(PngSignature);
                WriteChunkHeader(stream, "flDR", oversizedLength);
                stream.SetLength(stream.Position + oversizedLength + 4L);
            }

            Assert.False(MainWindow.ContainsStegoPayload(path));
            Assert.Null(MainWindow.TryExtractStegoPayload(path));
        }
        finally
        {
            DeleteTempFile(path);
        }
    }

    [Fact]
    public void ContainsStegoPayload_RejectsRelativePath()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.ContainsStegoPayload("carrier.png"));

        Assert.Equal("The selected path must be fully qualified.", ex.Message);
    }

    [Fact]
    public void TryExtractStegoPayload_RejectsAlternateDataStreamPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "carrier.png:stream");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MainWindow.TryExtractStegoPayload(path));

        Assert.Equal("The selected path must reference a normal file or folder.", ex.Message);
    }

    [Fact]
    public void ValidatePngCarrierPayloadSize_RejectsOversizedPayloadChunks()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            MainWindow.ValidatePngCarrierPayloadSize(MainWindow.MaxPngCarrierPayloadBytes + 1));

        Assert.Contains("PNG carrier payload", ex.Message);
    }

    private static string WriteTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"), "carrier.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void DeleteTempFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        File.Delete(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] BuildPng(params (string Type, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        stream.Write(PngSignature);
        foreach ((string type, byte[] data) in chunks)
        {
            WriteChunkHeader(stream, type, data.Length);
            stream.Write(data);
            stream.Write(new byte[4]);
        }

        return stream.ToArray();
    }

    private static void WriteChunkHeader(Stream stream, string type, int length)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, length);
        stream.Write(lengthBytes);
        stream.Write(Encoding.ASCII.GetBytes(type));
    }
}

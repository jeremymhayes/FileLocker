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
    public void TryExtractStegoPayload_ReturnsNullForNonPng()
    {
        string path = WriteTempFile(Encoding.UTF8.GetBytes("not a png"));

        try
        {
            Assert.Null(MainWindow.TryExtractStegoPayload(path));
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
        }
        finally
        {
            DeleteTempFile(path);
        }
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

using System.Text;

namespace FileLocker.Tests;

public sealed class PayloadChunkedServiceTests
{
    [Fact]
    public void WritePayload_AndOpenPayload_RoundTripsMetadataAndContent()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("{\"kind\":\"file\",\"name\":\"demo.txt\"}");
        byte[] plaintext = Encoding.UTF8.GetBytes(new string('A', 250_000));

        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            metadata,
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs("CorrectHorseBatteryStaple!", null, null, 32_768, 0x01),
            CancellationToken.None);

        output.Position = 0;
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            output,
            new PayloadUnlockInputs("CorrectHorseBatteryStaple!", null, null),
            CancellationToken.None);

        byte[] restored = ReadToEnd(payload.PlaintextStream);

        Assert.Equal(metadata, payload.MetadataBytes);
        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void OpenPayload_AllowsRecoveryKeyUnlock()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("{\"kind\":\"file\"}");
        byte[] plaintext = Encoding.UTF8.GetBytes("secret payload");

        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            metadata,
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs("PrimaryPassword!234", null, "RECOVERY-KEY-123", 16_384, 0x02),
            CancellationToken.None);

        output.Position = 0;
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            output,
            new PayloadUnlockInputs(null, null, "RECOVERY-KEY-123"),
            CancellationToken.None);

        Assert.Equal(plaintext, ReadToEnd(payload.PlaintextStream));
    }

    [Fact]
    public void OpenPayload_RejectsWrongPassword()
    {
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{}"),
            (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
            new PayloadWriteInputs("CorrectPassword!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        output.Position = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PayloadChunkedService.OpenPayload(
                output,
                new PayloadUnlockInputs("WrongPassword!234", null, null),
                CancellationToken.None));
    }

    [Fact]
    public void OpenPayload_FailsSafelyForTruncatedCiphertext()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(new string('T', 80_000));
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{\"kind\":\"file\"}"),
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        byte[] payloadBytes = output.ToArray();
        Array.Resize(ref payloadBytes, payloadBytes.Length - 8);

        using var input = new MemoryStream(payloadBytes);
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            input,
            new PayloadUnlockInputs("Password!234", null, null),
            CancellationToken.None);

        Assert.ThrowsAny<Exception>(() => ReadToEnd(payload.PlaintextStream));
    }

    [Fact]
    public void RotateKeys_ReplacesPasswordButPreservesCiphertextContent()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("{\"kind\":\"file\",\"version\":3}");
        byte[] plaintext = Encoding.UTF8.GetBytes(new string('Z', 64_000));

        using var original = new MemoryStream();
        PayloadChunkedService.WritePayload(
            original,
            metadata,
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs("OldPassword!234", null, "OLD-RECOVERY", 16_384, 0x03),
            CancellationToken.None);

        original.Position = 0;
        using var rotated = new MemoryStream();
        PayloadChunkedService.RotateKeys(
            original,
            rotated,
            new PayloadRotateInputs(
                new PayloadUnlockInputs("OldPassword!234", null, null),
                "NewPassword!234",
                null,
                "NEW-RECOVERY"));

        rotated.Position = 0;
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            rotated,
            new PayloadUnlockInputs("NewPassword!234", null, null),
            CancellationToken.None);

        Assert.Equal(metadata, payload.MetadataBytes);
        Assert.Equal(plaintext, ReadToEnd(payload.PlaintextStream));
    }

    [Fact]
    public void InspectHeader_ReturnsExpectedChunkConfiguration()
    {
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{}"),
            (stream, _) => stream.Write(Encoding.UTF8.GetBytes("abc")),
            new PayloadWriteInputs("Password!234", null, "RECOVERY", 8_192, 0x0F),
            CancellationToken.None);

        output.Position = 0;
        PayloadHeader header = PayloadChunkedService.InspectHeader(output);

        Assert.Equal(3, header.Version);
        Assert.Equal(8_192, header.ChunkSize);
        Assert.Equal((byte)0x0F, header.Flags);
        Assert.Equal(2, header.Slots.Count);
    }

    [Fact]
    public void ResolveAvailablePath_AppendsCounterWhenFileAlreadyExists()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string original = Path.Combine(tempDirectory, "demo.locked");
            File.WriteAllText(original, "existing");

            string resolved = InvokePrivateStatic<string>("ResolveAvailablePath", original);

            Assert.Equal(Path.Combine(tempDirectory, "demo_1.locked"), resolved);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CalculateSizeHidingPadding_UsesLargerBuckets()
    {
        long smallPadding = InvokePrivateStatic<long>("CalculateSizeHidingPadding", 50_000L);
        long mediumPadding = InvokePrivateStatic<long>("CalculateSizeHidingPadding", 2_000_000L);

        Assert.True(smallPadding >= 14_000);
        Assert.True(mediumPadding >= 3_000_000);
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(MainWindow).GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");
        object? result = method.Invoke(null, args);
        return (T)result!;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
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
    public void OpenPayloadResult_DisposeClearsMetadataBytes()
    {
        byte[] metadata = Encoding.UTF8.GetBytes("{\"kind\":\"file\",\"name\":\"secret.txt\"}");
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            metadata,
            (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret payload")),
            new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        output.Position = 0;
        OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            output,
            new PayloadUnlockInputs("Password!234", null, null),
            CancellationToken.None);
        byte[] metadataReference = payload.MetadataBytes;

        payload.Dispose();

        Assert.All(metadataReference, value => Assert.Equal(0, value));
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
    public void OpenPayload_RejectsNullUnlockInputsBeforeReading()
    {
        using var input = new PositionTrackingStream();

        Assert.Throws<ArgumentNullException>(() =>
            PayloadChunkedService.OpenPayload(input, null!, CancellationToken.None));

        Assert.Equal(0, input.Position);
    }

    [Fact]
    public void OpenPayload_CanceledTokenDoesNotRead()
    {
        using var input = new PositionTrackingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                cancellation.Token));

        Assert.Equal(0, input.Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WritePayload_RejectsBlankPassword(string password)
    {
        using var output = new MemoryStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
                new PayloadWriteInputs(password, null, null, 16_384, 0x01),
                CancellationToken.None));

        Assert.Contains("password is required", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WritePayload_RejectsInvalidChunkSize(int chunkSize)
    {
        using var output = new MemoryStream();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
                new PayloadWriteInputs("Password!234", null, null, chunkSize, 0x01),
                CancellationToken.None));

        Assert.Contains("chunk size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WritePayload_DoesNotFinalizePayloadWhenContentWriterFails()
    {
        using var output = new MemoryStream();

        InvalidOperationException writeException = Assert.Throws<InvalidOperationException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) =>
                {
                    stream.Write(Encoding.UTF8.GetBytes("partial"));
                    throw new InvalidOperationException("Simulated writer failure.");
                },
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
                CancellationToken.None));

        Assert.Equal("Simulated writer failure.", writeException.Message);
        output.Position = 0;

        Assert.Throws<EndOfStreamException>(() =>
            PayloadChunkedService.OpenPayload(
                output,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Fact]
    public void WritePayload_CanceledTokenDoesNotWriteHeader()
    {
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool contentWriterCalled = false;

        Assert.Throws<OperationCanceledException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (_, _) =>
                {
                    contentWriterCalled = true;
                },
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
                cancellation.Token));

        Assert.False(contentWriterCalled);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void RotateKeys_RejectsNullInputsBeforeReading()
    {
        using var input = new PositionTrackingStream();
        using var output = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() =>
            PayloadChunkedService.RotateKeys(input, output, null!, TestContext.Current.CancellationToken));

        Assert.Equal(0, input.Position);
    }

    [Fact]
    public void RotateKeys_RejectsNullCurrentInputsBeforeReading()
    {
        using var input = new PositionTrackingStream();
        using var output = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() =>
            PayloadChunkedService.RotateKeys(
                input,
                output,
                new PayloadRotateInputs(null!, "NewPassword!234", null, null),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, input.Position);
    }

    [Fact]
    public void RotateKeys_CanceledTokenDoesNotReadOrWrite()
    {
        using var input = new PositionTrackingStream();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PayloadChunkedService.RotateKeys(
                input,
                output,
                new PayloadRotateInputs(
                    new PayloadUnlockInputs("OldPassword!234", null, null),
                    "NewPassword!234",
                    null,
                    null),
                cancellation.Token));

        Assert.Equal(0, input.Position);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RotateKeys_RejectsBlankNewPasswordBeforeReading(string newPassword)
    {
        using var input = new PositionTrackingStream();
        using var output = new MemoryStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.RotateKeys(
                input,
                output,
                new PayloadRotateInputs(
                    new PayloadUnlockInputs("OldPassword!234", null, null),
                    newPassword,
                    null,
                    null),
                TestContext.Current.CancellationToken));

        Assert.Contains("New payload password is required", ex.Message);
        Assert.Equal(0, input.Position);
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
    public void OpenPayload_RejectsTamperedCiphertext()
    {
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{\"kind\":\"file\"}"),
            (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret payload")),
            new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        byte[] payloadBytes = output.ToArray();
        using (var headerStream = new MemoryStream(payloadBytes, writable: false))
        {
            PayloadHeader header = PayloadChunkedService.InspectHeader(headerStream);
            int firstChunkLength = BinaryPrimitives.ReadInt32LittleEndian(payloadBytes.AsSpan((int)header.CiphertextOffset, sizeof(int)));
            Assert.True(firstChunkLength > 0);
            int firstCiphertextOffset = (int)header.CiphertextOffset + sizeof(int) + 16;
            payloadBytes[firstCiphertextOffset] ^= 0x01;
        }

        using var input = new MemoryStream(payloadBytes);

        Assert.ThrowsAny<CryptographicException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Fact]
    public void OpenPayload_RejectsInvalidChunkLengthBeforeAllocation()
    {
        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{\"kind\":\"file\"}"),
            (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
            new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        byte[] payloadBytes = output.ToArray();
        using (var headerStream = new MemoryStream(payloadBytes, writable: false))
        {
            PayloadHeader header = PayloadChunkedService.InspectHeader(headerStream);
            BinaryPrimitives.WriteInt32LittleEndian(payloadBytes.AsSpan((int)header.CiphertextOffset, sizeof(int)), -1);
        }

        using var input = new MemoryStream(payloadBytes);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));

        Assert.Contains("chunk length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public void ValidateMetadataLength_RejectsInvalidLengths(int metadataLength)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            PayloadChunkedService.ValidateMetadataLength(metadataLength));

        Assert.Contains("metadata length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024 * 1024)]
    public void ValidateMetadataLength_AllowsBoundedLengths(int metadataLength)
    {
        PayloadChunkedService.ValidateMetadataLength(metadataLength);
    }

    [Fact]
    public void WritePayload_RejectsOversizedMetadataBeforeWriting()
    {
        using var output = new MemoryStream();
        byte[] metadata = new byte[64 * 1024 * 1024 + 1];

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                metadata,
                (stream, _) => stream.WriteByte(1),
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
                CancellationToken.None));

        Assert.Contains("metadata length", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
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
                "NEW-RECOVERY"),
            TestContext.Current.CancellationToken);

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
    public void InspectHeader_RejectsInvalidChunkSize()
    {
        using MemoryStream header = BuildPayloadHeader(chunkSize: 0);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("chunk size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsInvalidNoncePrefixLength()
    {
        using MemoryStream header = BuildPayloadHeader(noncePrefixLength: 7);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("nonce prefix", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsMissingKeySlots()
    {
        using MemoryStream header = BuildPayloadHeader(slotCount: 0);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("key slots", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikePayloadV3_RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => PayloadChunkedService.LooksLikePayloadV3(null!));
    }

    [Fact]
    public void LooksLikePayloadV3_PreservesStreamPosition()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("xxFLKR"));
        stream.Position = 2;

        Assert.True(PayloadChunkedService.LooksLikePayloadV3(stream));
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void LooksLikePayloadV3_ReturnsFalseForNonSeekableStreams()
    {
        using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("FLKR"));

        Assert.False(PayloadChunkedService.LooksLikePayloadV3(stream));
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

    private static MemoryStream BuildPayloadHeader(int chunkSize = 16_384, int noncePrefixLength = 8, int slotCount = 1)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FLKR"));
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((byte)0x01);
        writer.Write(3);
        writer.Write(65_536);
        writer.Write(1);
        writer.Write(chunkSize);
        writer.Write((byte)noncePrefixLength);
        writer.Write(new byte[noncePrefixLength]);
        writer.Write((byte)slotCount);

        for (int i = 0; i < slotCount; i++)
        {
            writer.Write((byte)PayloadKeySlotKind.Password);
            writer.Write(new byte[32]);
            writer.Write(new byte[12]);
            writer.Write(new byte[16]);
            writer.Write(new byte[32]);
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class PositionTrackingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("Payload validation should happen before reading the stream.");
        }

        public override int Read(Span<byte> buffer)
        {
            throw new InvalidOperationException("Payload validation should happen before reading the stream.");
        }
    }

    private sealed class NonSeekableReadStream : MemoryStream
    {
        public NonSeekableReadStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}

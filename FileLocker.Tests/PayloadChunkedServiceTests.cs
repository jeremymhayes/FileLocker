using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto;

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

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void WritePayload_AndOpenPayload_RoundTripsSupportedEncryptionAlgorithms(string algorithm)
    {
        byte[] metadata = Encoding.UTF8.GetBytes($"{{\"algorithm\":\"{algorithm}\"}}");
        byte[] plaintext = Encoding.UTF8.GetBytes(new string(algorithm[0], 180_000));

        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            metadata,
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs(
                "CorrectHorseBatteryStaple!",
                null,
                null,
                32_768,
                0x01,
                EncryptionAlgorithmCatalog.GetNewPayloadAlgorithmId(algorithm)),
            CancellationToken.None);

        output.Position = 0;
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            output,
            new PayloadUnlockInputs("CorrectHorseBatteryStaple!", null, null),
            CancellationToken.None);

        Assert.Equal(EncryptionAlgorithmCatalog.GetNewPayloadAlgorithmId(algorithm), payload.Header.AlgorithmId);
        Assert.Equal(metadata, payload.MetadataBytes);
        Assert.Equal(plaintext, ReadToEnd(payload.PlaintextStream));
    }

    [Fact]
    public void AlgorithmDefinitions_MarkReservedAlgorithmsUnavailableForNewPayloads()
    {
        Assert.True(EncryptionAlgorithmCatalog.TryGetDefinition(EncryptionAlgorithmCatalog.XChaCha20Poly1305, out EncryptionAlgorithmDefinition? xchacha));
        Assert.False(xchacha.CanEncryptNewPayloads);
        Assert.False(xchacha.CanReadPayloads);

        Assert.True(EncryptionAlgorithmCatalog.TryGetDefinition(EncryptionAlgorithmCatalog.Aes256Gcm, out EncryptionAlgorithmDefinition? aesGcm));
        Assert.True(aesGcm.CanEncryptNewPayloads);
        Assert.True(aesGcm.CanReadPayloads);

        Assert.True(EncryptionAlgorithmCatalog.TryGetDefinition(EncryptionAlgorithmCatalog.ChaCha20Poly1305, out EncryptionAlgorithmDefinition? chacha));
        Assert.True(chacha.CanEncryptNewPayloads);
        Assert.True(chacha.CanReadPayloads);

        Assert.True(EncryptionAlgorithmCatalog.TryGetDefinition(EncryptionAlgorithmCatalog.Aes256GcmSiv, out EncryptionAlgorithmDefinition? aesGcmSiv));
        Assert.True(aesGcmSiv.CanEncryptNewPayloads);
        Assert.True(aesGcmSiv.CanReadPayloads);
    }

    [Fact]
    public void AlgorithmDefinitions_HaveStableUniqueFormatFields()
    {
        Assert.Equal(
            EncryptionAlgorithmCatalog.Definitions.Length,
            EncryptionAlgorithmCatalog.Definitions.Select(definition => definition.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            EncryptionAlgorithmCatalog.Definitions.Length,
            EncryptionAlgorithmCatalog.Definitions.Select(definition => definition.PayloadAlgorithmId).Distinct().Count());

        Assert.All(EncryptionAlgorithmCatalog.Definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Id));
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.FileFormatName));
            Assert.True(definition.KeySizeBits > 0);
            Assert.NotEqual(0, definition.PayloadAlgorithmId);
        });
    }

    [Fact]
    public void CanEncryptNewPayloadOnThisRuntime_RejectsReservedDefinitions()
    {
        EncryptionAlgorithmDefinition xchacha = Assert.Single(
            EncryptionAlgorithmCatalog.Definitions,
            definition => string.Equals(definition.Id, EncryptionAlgorithmCatalog.XChaCha20Poly1305, StringComparison.Ordinal));

        Assert.False(PayloadChunkedService.CanEncryptNewPayloadOnThisRuntime(xchacha));
    }

    [Fact]
    public void GetPayloadAlgorithmId_RejectsUnknownAlgorithmNames()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            EncryptionAlgorithmCatalog.GetPayloadAlgorithmId("unknown-aead"));

        Assert.Contains("Unsupported file encryption algorithm", ex.Message);
    }

    [Fact]
    public void TryGetDefinition_RejectsOversizedAlgorithmNames()
    {
        bool accepted = EncryptionAlgorithmCatalog.TryGetDefinition(
            new string('A', EncryptionAlgorithmCatalog.MaxAlgorithmNameChars + 1),
            out EncryptionAlgorithmDefinition? definition);

        Assert.False(accepted);
        Assert.Null(definition);
    }

    [Fact]
    public void TryGetDefinition_RejectsControlCharacterAlgorithmNames()
    {
        bool accepted = EncryptionAlgorithmCatalog.TryGetDefinition(
            "AES-256\r\n-GCM",
            out EncryptionAlgorithmDefinition? definition);

        Assert.False(accepted);
        Assert.Null(definition);
    }

    [Fact]
    public void TryGetDefinition_RejectsUnicodeFormatCharacterAlgorithmNames()
    {
        bool accepted = EncryptionAlgorithmCatalog.TryGetDefinition(
            $"{EncryptionAlgorithmCatalog.Aes256Gcm}\u202E",
            out EncryptionAlgorithmDefinition? definition);

        Assert.False(accepted);
        Assert.Null(definition);
    }

    [Fact]
    public void GetKeySizeBits_RejectsUnknownAlgorithmNames()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            EncryptionAlgorithmCatalog.GetKeySizeBits("unknown-aead"));

        Assert.Contains("Unsupported file encryption algorithm", ex.Message);
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void GetKeySizeBits_UsesCatalogDefinition(string algorithm)
    {
        EncryptionAlgorithmDefinition definition = Assert.Single(
            EncryptionAlgorithmCatalog.Definitions,
            candidate => string.Equals(candidate.DisplayName, algorithm, StringComparison.Ordinal));

        Assert.Equal(definition.KeySizeBits, EncryptionAlgorithmCatalog.GetKeySizeBits(algorithm));
    }

    [Theory]
    [InlineData("AES-GCM", EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData("chacha20poly1305ietf", EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData("AES GCM SIV", EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void GetFileFormatName_NormalizesAliases(string algorithm, string expected)
    {
        Assert.Equal(expected, EncryptionAlgorithmCatalog.GetFileFormatName(algorithm));
    }

    [Fact]
    public void GetFileFormatName_RejectsUnknownAlgorithmNames()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            EncryptionAlgorithmCatalog.GetFileFormatName("unknown-aead"));

        Assert.Contains("Unsupported file encryption algorithm", ex.Message);
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm, true)]
    [InlineData("AES-GCM", true)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305, false)]
    [InlineData("unknown-aead", false)]
    [InlineData(null, false)]
    public void IsAesGcm_ReturnsTrueOnlyForAesAliases(string? algorithm, bool expected)
    {
        Assert.Equal(expected, EncryptionAlgorithmCatalog.IsAesGcm(algorithm));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.PayloadAlgorithmXChaCha20Poly1305)]
    [InlineData(255)]
    public void WritePayload_RejectsAlgorithmsNotApprovedForNewEncryption(int algorithmId)
    {
        using var output = new MemoryStream();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01, (byte)algorithmId),
                CancellationToken.None));

        Assert.Contains("Unsupported payload algorithm", ex.Message);
        Assert.Equal(0, output.Length);
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
    public void OpenPayload_ReadRejectsInvalidDestinationWithoutConsumingPlaintext()
    {
        byte[] payloadBytes = CreatePayloadBytes(EncryptionAlgorithmCatalog.Aes256Gcm);
        using var input = new MemoryStream(payloadBytes);
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            input,
            new PayloadUnlockInputs("Password!234", null, null),
            CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => payload.PlaintextStream.Read(new byte[4], -1, 1));
        byte[] restored = ReadToEnd(payload.PlaintextStream);

        Assert.Equal(Encoding.UTF8.GetBytes("secret payload"), restored);
    }

    [Fact]
    public void OpenPayload_PlaintextStreamRejectsReadsAfterDispose()
    {
        byte[] payloadBytes = CreatePayloadBytes(EncryptionAlgorithmCatalog.Aes256Gcm);
        using var input = new MemoryStream(payloadBytes);
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            input,
            new PayloadUnlockInputs("Password!234", null, null),
            CancellationToken.None);
        Stream plaintextStream = payload.PlaintextStream;

        payload.Dispose();

        Assert.False(plaintextStream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => plaintextStream.Read(new byte[1], 0, 1));
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

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsWrongPassword_ForSupportedEncryptionAlgorithms(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        using var input = new MemoryStream(payloadBytes);

        Assert.Throws<UnauthorizedAccessException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
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
    public void OpenPayload_RejectsMissingUnlockSecretBeforeReading()
    {
        using var input = new PositionTrackingStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(null, [1, 2, 3], " "),
                CancellationToken.None));

        Assert.Contains("password or recovery key", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void OpenPayload_CanceledAfterHeaderDoesNotDeriveKey()
    {
        byte[] payloadBytes = CreatePayloadBytes(EncryptionAlgorithmCatalog.Aes256Gcm);
        using var headerInput = new MemoryStream(payloadBytes);
        long headerLength = PayloadChunkedService.InspectHeader(headerInput).CiphertextOffset;
        using var cancellation = new CancellationTokenSource();
        using var input = new CancelAfterPositionStream(payloadBytes, headerLength, cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("WrongPassword!234", null, null),
                cancellation.Token));
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

    [Fact]
    public void WritePayload_RejectsOversizedPasswordBeforeWriting()
    {
        using var output = new MemoryStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) => stream.Write(Encoding.UTF8.GetBytes("secret")),
                new PayloadWriteInputs(CreateOversizedSecretText(), null, null, 16_384, 0x01),
                CancellationToken.None));

        Assert.Contains("Payload password is too long.", ex.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void OpenPayload_RejectsOversizedUnlockSecretBeforeReading()
    {
        using var input = new PositionTrackingStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(CreateOversizedSecretText(), null, null),
                TestContext.Current.CancellationToken));

        Assert.Contains("Payload password is too long.", ex.Message);
        Assert.Equal(0, input.Position);
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
    public void WritePayload_ContentStreamRejectsInvalidWriteRange()
    {
        using var output = new MemoryStream();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) => stream.Write(new byte[4], -1, 1),
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
                CancellationToken.None));

        Assert.Equal("offset", ex.ParamName);
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
    public void WritePayload_CapturedContentStreamRejectsWritesAfterCompletion()
    {
        using var output = new MemoryStream();
        Stream? capturedStream = null;

        PayloadChunkedService.WritePayload(
            output,
            Encoding.UTF8.GetBytes("{}"),
            (stream, _) =>
            {
                capturedStream = stream;
                stream.Write(Encoding.UTF8.GetBytes("secret"));
            },
            new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
            CancellationToken.None);

        Assert.NotNull(capturedStream);
        Assert.False(capturedStream!.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => capturedStream.Write([1], 0, 1));
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
    public void WritePayload_CanceledAfterContentWriteDoesNotFinalizePayload()
    {
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            PayloadChunkedService.WritePayload(
                output,
                Encoding.UTF8.GetBytes("{}"),
                (stream, _) =>
                {
                    stream.Write(Encoding.UTF8.GetBytes("partial"));
                    cancellation.Cancel();
                },
                new PayloadWriteInputs("Password!234", null, null, 16_384, 0x01),
                cancellation.Token));

        output.Position = 0;
        Assert.Throws<EndOfStreamException>(() =>
            PayloadChunkedService.OpenPayload(
                output,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
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
    public void RotateKeys_RejectsOversizedNewPasswordBeforeReading()
    {
        using var input = new PositionTrackingStream();
        using var output = new MemoryStream();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            PayloadChunkedService.RotateKeys(
                input,
                output,
                new PayloadRotateInputs(
                    new PayloadUnlockInputs("OldPassword!234", null, null),
                    CreateOversizedSecretText(),
                    null,
                    null),
                TestContext.Current.CancellationToken));

        Assert.Contains("New payload password is too long.", ex.Message);
        Assert.Equal(0, input.Position);
        Assert.Equal(0, output.Length);
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

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedCiphertext(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        using (var headerStream = new MemoryStream(payloadBytes, writable: false))
        {
            PayloadHeader header = PayloadChunkedService.InspectHeader(headerStream);
            int firstChunkLength = BinaryPrimitives.ReadInt32LittleEndian(payloadBytes.AsSpan((int)header.CiphertextOffset, sizeof(int)));
            Assert.True(firstChunkLength > 0);
            int firstCiphertextOffset = (int)header.CiphertextOffset + sizeof(int) + 16;
            payloadBytes[firstCiphertextOffset] ^= 0x01;
        }

        using var input = new MemoryStream(payloadBytes);

        AssertAuthenticatedPayloadFailure(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedTag(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        using (var headerStream = new MemoryStream(payloadBytes, writable: false))
        {
            PayloadHeader header = PayloadChunkedService.InspectHeader(headerStream);
            int firstTagOffset = (int)header.CiphertextOffset + sizeof(int);
            payloadBytes[firstTagOffset] ^= 0x01;
        }

        using var input = new MemoryStream(payloadBytes);

        AssertAuthenticatedPayloadFailure(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedNoncePrefix(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        payloadBytes[PayloadNoncePrefixOffset] ^= 0x01;
        using var input = new MemoryStream(payloadBytes);

        Assert.Throws<UnauthorizedAccessException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedPayloadAlgorithmHeader(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        payloadBytes[PayloadAlgorithmOffset] = 255;
        using var input = new MemoryStream(payloadBytes);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));

        Assert.Contains("Unsupported payload algorithm", ex.Message);
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedArgonParameters(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        BinaryPrimitives.WriteInt32LittleEndian(payloadBytes.AsSpan(PayloadArgonIterationsOffset, sizeof(int)), 4);
        using var input = new MemoryStream(payloadBytes);

        Assert.Throws<UnauthorizedAccessException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsTamperedAuthenticatedHeaderFlags(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        payloadBytes[PayloadFlagsOffset] ^= 0x02;
        using var input = new MemoryStream(payloadBytes);

        Assert.Throws<UnauthorizedAccessException>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData(EncryptionAlgorithmCatalog.ChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.Aes256GcmSiv)]
    public void OpenPayload_RejectsAuthenticatedHeaderVersionDowngrade(string algorithm)
    {
        byte[] payloadBytes = CreatePayloadBytes(algorithm);
        payloadBytes[PayloadVersionOffset] = 3;
        using var input = new MemoryStream(payloadBytes);

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("Password!234", null, null),
                CancellationToken.None));

        if (string.Equals(algorithm, EncryptionAlgorithmCatalog.Aes256Gcm, StringComparison.Ordinal))
        {
            Assert.IsType<UnauthorizedAccessException>(ex);
        }
        else
        {
            InvalidDataException invalidHeader = Assert.IsType<InvalidDataException>(ex);
            Assert.Contains("Legacy payload headers support only AES-256-GCM", invalidHeader.Message);
        }
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
    [InlineData(int.MaxValue)]
    public void BuildChunkAad_RejectsInvalidChunkCounters(int chunkIndex)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            InvokePayloadPrivateStatic<byte[]>("BuildChunkAad", chunkIndex));

        Assert.Contains("chunk counter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void BuildChunkNonceBytes_RejectsInvalidChunkCounters(int chunkIndex)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            InvokePayloadPrivateStatic<byte[]>("BuildChunkNonceBytes", 12, new byte[8], chunkIndex));

        Assert.Contains("chunk counter", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void RotateKeys_RejectsTamperedCiphertextBeforeWritingRotatedPayload()
    {
        byte[] payloadBytes = CreatePayloadBytes(EncryptionAlgorithmCatalog.Aes256Gcm);
        TamperLastCiphertextByte(payloadBytes);

        using var original = new MemoryStream(payloadBytes);
        using var rotated = new MemoryStream();

        AssertAuthenticatedPayloadFailure(() =>
            PayloadChunkedService.RotateKeys(
                original,
                rotated,
                new PayloadRotateInputs(
                    new PayloadUnlockInputs("Password!234", null, null),
                    "NewPassword!234",
                    null,
                    null),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, rotated.Length);
    }

    [Fact]
    public void VerifyFolderPackagePayloadV3_AuthenticatesPackagePadding()
    {
        byte[] payloadBytes = CreateFolderPackagePayloadBytesWithPadding();
        TamperLastCiphertextByte(payloadBytes);

        using var input = new MemoryStream(payloadBytes);
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            input,
            new PayloadUnlockInputs("Password!234", null, null),
            CancellationToken.None);
        FolderPackageMetadata metadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes)
            ?? throw new InvalidOperationException("Folder package metadata could not be read.");

        MethodInfo method = typeof(MainWindow).GetMethod("VerifyFolderPackagePayloadV3", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("VerifyFolderPackagePayloadV3 was not found.");
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [payload, metadata, CancellationToken.None, null]));

        AssertAuthenticatedPayloadFailure(() => throw ex.InnerException!);
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

        Assert.Equal(PayloadChunkedService.CurrentVersion, header.Version);
        Assert.Equal(8_192, header.ChunkSize);
        Assert.Equal((byte)0x0F, header.Flags);
        Assert.Equal(2, header.Slots.Count);
    }

    [Fact]
    public void InspectHeader_RejectsNonSeekableStreams()
    {
        using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("FLKR"));

        Assert.Throws<ArgumentException>(() => PayloadChunkedService.InspectHeader(stream));
    }

    [Fact]
    public void InspectHeader_RejectsInvalidChunkSize()
    {
        using MemoryStream header = BuildPayloadHeader(chunkSize: 0);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("chunk size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsTruncatedScalarHeader()
    {
        byte[] truncated = Encoding.ASCII.GetBytes("FLKR");
        using var header = new MemoryStream(truncated);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsOversizedChunkSize()
    {
        using MemoryStream header = BuildPayloadHeader(chunkSize: 16 * 1024 * 1024 + 1);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("chunk size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsExcessiveArgonMemoryBeforeDerivingKeys()
    {
        using MemoryStream header = BuildPayloadHeader(argonMemoryKb: 1024 * 1024 + 1);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("key-derivation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsUnsupportedPayloadAlgorithm()
    {
        using MemoryStream header = BuildPayloadHeader(algorithmId: 255);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("Unsupported payload algorithm", ex.Message);
    }

    [Theory]
    [InlineData(EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305)]
    [InlineData(EncryptionAlgorithmCatalog.PayloadAlgorithmAes256GcmSiv)]
    public void InspectHeader_RejectsLegacyHeadersWithNonLegacyAlgorithms(byte algorithmId)
    {
        using MemoryStream header = BuildPayloadHeader(algorithmId: algorithmId);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("Legacy payload", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsReservedPayloadAlgorithmWithoutMaintainedImplementation()
    {
        using MemoryStream header = BuildPayloadHeader(
            algorithmId: EncryptionAlgorithmCatalog.PayloadAlgorithmXChaCha20Poly1305);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("Unsupported payload algorithm", ex.Message);
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
    public void InspectHeader_RejectsTooManyKeySlots()
    {
        using MemoryStream header = BuildPayloadHeader(slotCount: 3);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("too many key slots", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsDuplicateKeySlotKinds()
    {
        using MemoryStream header = BuildPayloadHeader(slotCount: 2);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("duplicate key-slot", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectHeader_RejectsUnsupportedKeySlotKind()
    {
        using MemoryStream header = BuildPayloadHeader(slotKind: 255);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PayloadChunkedService.InspectHeader(header));

        Assert.Contains("key-slot kind", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void LooksLikePayloadV3_AllowsPartialReads()
    {
        using var stream = new PartialReadMemoryStream(Encoding.ASCII.GetBytes("FLKR"));

        Assert.True(PayloadChunkedService.LooksLikePayloadV3(stream));
        Assert.Equal(0, stream.Position);
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
    public void ResolveAvailablePath_ThrowsAfterTooManyPayloadCollisions()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string original = Path.Combine(tempDirectory, "demo.locked");
            File.WriteAllText(original, "existing");
            for (int counter = 1; counter <= MainWindow.MaxResolveAvailablePathAttempts; counter++)
            {
                File.WriteAllText(Path.Combine(tempDirectory, $"demo_{counter}.locked"), "existing");
            }

            TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(() =>
                InvokePrivateStatic<string>("ResolveAvailablePath", original));
            IOException ex = Assert.IsType<IOException>(wrapper.InnerException);

            Assert.Contains("available file name", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void CalculateSizeHidingPadding_RejectsNegativeSizes()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokePrivateStatic<long>("CalculateSizeHidingPadding", -1L));

        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    [Fact]
    public void CalculateSizeHidingPadding_DoesNotOverflowNearLongMaxValue()
    {
        long padding = InvokePrivateStatic<long>("CalculateSizeHidingPadding", long.MaxValue);

        Assert.Equal(0, padding);
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private const int PayloadVersionOffset = 4;
    private const int PayloadAlgorithmOffset = 5;
    private const int PayloadFlagsOffset = 7;
    private const int PayloadArgonIterationsOffset = 8;
    private const int PayloadNoncePrefixOffset = 25;

    private static byte[] CreatePayloadBytes(string algorithm)
    {
        byte[] metadata = Encoding.UTF8.GetBytes($"{{\"kind\":\"file\",\"algorithm\":\"{algorithm}\"}}");
        byte[] plaintext = Encoding.UTF8.GetBytes("secret payload");

        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            metadata,
            (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
            new PayloadWriteInputs(
                "Password!234",
                null,
                null,
                16_384,
                0x01,
                EncryptionAlgorithmCatalog.GetNewPayloadAlgorithmId(algorithm)),
            CancellationToken.None);

        return output.ToArray();
    }

    private static byte[] CreateFolderPackagePayloadBytesWithPadding()
    {
        byte[] entryBytes = Encoding.UTF8.GetBytes("folder entry content");
        var metadata = new FolderPackageMetadata
        {
            RootFolderName = "package-root",
            PackagePaddingLength = 4096,
            Entries =
            [
                new FolderPackageEntryMetadata
                {
                    RelativePath = "entry.txt",
                    OriginalSize = entryBytes.Length,
                    ContentHashBase64 = Convert.ToBase64String(SHA256.HashData(entryBytes))
                }
            ]
        };

        using var output = new MemoryStream();
        PayloadChunkedService.WritePayload(
            output,
            JsonSerializer.SerializeToUtf8Bytes(metadata),
            (stream, cancellationToken) =>
            {
                Span<byte> lengthBuffer = stackalloc byte[sizeof(long)];
                BinaryPrimitives.WriteInt64LittleEndian(lengthBuffer, entryBytes.Length);
                stream.Write(lengthBuffer);
                stream.Write(entryBytes, 0, entryBytes.Length);
                MainWindow.WriteRandomPadding(stream, metadata.PackagePaddingLength, cancellationToken);
            },
            new PayloadWriteInputs("Password!234", null, null, 128, 0x03),
            CancellationToken.None);

        return output.ToArray();
    }

    private static void TamperLastCiphertextByte(byte[] payloadBytes)
    {
        using var headerStream = new MemoryStream(payloadBytes, writable: false);
        PayloadHeader header = PayloadChunkedService.InspectHeader(headerStream);
        int offset = (int)header.CiphertextOffset;
        int lastCiphertextOffset = -1;
        int lastCiphertextLength = 0;

        while (true)
        {
            int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(payloadBytes.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            if (chunkLength == 0)
            {
                break;
            }

            offset += 16;
            lastCiphertextOffset = offset;
            lastCiphertextLength = chunkLength;
            offset += chunkLength;
        }

        if (lastCiphertextOffset < 0 || lastCiphertextLength <= 0)
        {
            throw new InvalidOperationException("No payload chunk was found to tamper.");
        }

        payloadBytes[lastCiphertextOffset + lastCiphertextLength - 1] ^= 0x01;
    }

    private static void AssertAuthenticatedPayloadFailure(Action action)
    {
        Exception ex = Assert.ThrowsAny<Exception>(action);
        Assert.True(
            ex is CryptographicException or InvalidCipherTextException,
            $"Expected an authenticated decryption failure, but got {ex.GetType().FullName}: {ex.Message}");
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(MainWindow).GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");
        object? result = method.Invoke(null, args);
        return (T)result!;
    }

    private static T InvokePayloadPrivateStatic<T>(string methodName, params object[] args)
    {
        MethodInfo method = typeof(PayloadChunkedService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        try
        {
            object? result = method.Invoke(null, args);
            return (T)result!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static MemoryStream BuildPayloadHeader(
        int chunkSize = 16_384,
        int noncePrefixLength = 8,
        int slotCount = 1,
        int algorithmId = 1,
        int argonIterations = 3,
        int argonMemoryKb = 65_536,
        int argonParallelism = 1,
        int slotKind = (int)PayloadKeySlotKind.Password)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FLKR"));
        writer.Write((byte)3);
        writer.Write((byte)algorithmId);
        writer.Write((byte)1);
        writer.Write((byte)0x01);
        writer.Write(argonIterations);
        writer.Write(argonMemoryKb);
        writer.Write(argonParallelism);
        writer.Write(chunkSize);
        writer.Write((byte)noncePrefixLength);
        writer.Write(new byte[noncePrefixLength]);
        writer.Write((byte)slotCount);

        for (int i = 0; i < slotCount; i++)
        {
            writer.Write((byte)slotKind);
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

    private static string CreateOversizedSecretText() => new('A', 1024 * 1024 + 1);

    private sealed class PartialReadMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override int Read(byte[] destination, int offset, int count)
        {
            return base.Read(destination, offset, Math.Min(count, 2));
        }

        public override int Read(Span<byte> destination)
        {
            return base.Read(destination[..Math.Min(destination.Length, 2)]);
        }
    }

    private sealed class CancelAfterPositionStream(
        byte[] buffer,
        long cancelAtPosition,
        CancellationTokenSource cancellation) : MemoryStream(buffer)
    {
        public override int Read(byte[] destination, int offset, int count)
        {
            int read = base.Read(destination, offset, count);
            CancelIfThresholdReached();
            return read;
        }

        public override int Read(Span<byte> destination)
        {
            int read = base.Read(destination);
            CancelIfThresholdReached();
            return read;
        }

        private void CancelIfThresholdReached()
        {
            if (Position >= cancelAtPosition)
            {
                cancellation.Cancel();
            }
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

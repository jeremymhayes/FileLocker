using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileLocker.Tests;

#pragma warning disable SYSLIB0050
public sealed class EncryptionProbeTests
{
    [Fact]
    public void EncryptFileAdvancedV3_RoundTripsOnTempFile()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "probe.txt");
        File.WriteAllText(inputPath, "hello world");

        try
        {
            MethodInfo method = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            object? result = method.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);

            Assert.NotNull(result);
            Assert.True(Directory.EnumerateFiles(tempRoot, "*.locked").Any());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_WorksWhenLockedOutputsAlreadyExist()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "probe.webp");
        File.WriteAllBytes(inputPath, Enumerable.Range(0, 512_000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllText(Path.Combine(tempRoot, "probe.webp.locked"), "existing");
        File.WriteAllText(Path.Combine(tempRoot, "probe.webp_1.locked"), "existing");

        try
        {
            MethodInfo method = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            object? result = method.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);

            Assert.NotNull(result);
            Assert.True(File.Exists(Path.Combine(tempRoot, "probe.webp_2.locked")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_SkipsCompressionForAlreadyCompressedExtensions()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "archive.zip");
        File.WriteAllText(inputPath, new string('A', 128_000));

        try
        {
            MethodInfo method = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            method.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);

            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();
            using FileStream input = File.OpenRead(lockedPath);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs("CorrectHorseBatteryStaple!123", null, null),
                CancellationToken.None);

            FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes)
                ?? throw new InvalidOperationException("Could not read payload metadata.");

            Assert.False(metadata.IsCompressed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_WithCompressionRecordsOriginalAndOutputSizes()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(compressFiles: true);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "compressible.txt");
        File.WriteAllText(inputPath, new string('A', 250_000));
        long originalSize = new FileInfo(inputPath).Length;

        try
        {
            MethodInfo method = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            object result = method.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null])
                ?? throw new InvalidOperationException("No result returned.");

            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();
            long encryptedSize = new FileInfo(lockedPath).Length;

            Assert.Equal(originalSize, GetNullableLongProperty(result, "OriginalSizeBytes"));
            Assert.Equal(encryptedSize, GetNullableLongProperty(result, "OutputSizeBytes"));
            Assert.True(encryptedSize < originalSize);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_WithoutCompressionRecordsSizes()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(compressFiles: false);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "plain.bin");
        File.WriteAllBytes(inputPath, Enumerable.Range(0, 64_000).Select(i => (byte)(i % 251)).ToArray());
        long originalSize = new FileInfo(inputPath).Length;

        try
        {
            MethodInfo method = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            object result = method.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null])
                ?? throw new InvalidOperationException("No result returned.");

            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();
            long encryptedSize = new FileInfo(lockedPath).Length;

            Assert.Equal(originalSize, GetNullableLongProperty(result, "OriginalSizeBytes"));
            Assert.Equal(encryptedSize, GetNullableLongProperty(result, "OutputSizeBytes"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_VerifyAndDecryptReportSavedPayloadAlgorithm()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(algorithm: EncryptionAlgorithmCatalog.ChaCha20Poly1305);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "algorithm-message.txt");
        File.WriteAllText(inputPath, "algorithm result message probe");

        try
        {
            MethodInfo encryptMethod = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");
            MethodInfo verifyMethod = typeof(MainWindow).GetMethod("VerifyLockedFileV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("VerifyLockedFileV3 method not found.");
            MethodInfo decryptMethod = typeof(MainWindow).GetMethod("DecryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DecryptFileAdvancedV3 method not found.");

            encryptMethod.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);
            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();

            object verifyResult = verifyMethod.Invoke(window, [lockedPath, "CorrectHorseBatteryStaple!123", options, null])
                ?? throw new InvalidOperationException("No verify result returned.");
            object decryptResult = decryptMethod.Invoke(window, [lockedPath, "CorrectHorseBatteryStaple!123", options, null, null])
                ?? throw new InvalidOperationException("No decrypt result returned.");

            Assert.Contains(EncryptionAlgorithmCatalog.ChaCha20Poly1305, GetStringProperty(verifyResult, "Message"));
            Assert.Contains(EncryptionAlgorithmCatalog.ChaCha20Poly1305, GetStringProperty(decryptResult, "Message"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EncryptFileAdvancedV3_WritesNormalizedCatalogMetadata()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(
            compressFiles: false,
            algorithm: "chacha20 poly1305 ietf",
            keySizeBits: 128);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "metadata-normalization.txt");
        File.WriteAllText(inputPath, "metadata normalization probe");

        try
        {
            MethodInfo encryptMethod = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");

            encryptMethod.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);
            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();

            using FileStream encryptedStream = File.OpenRead(lockedPath);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                encryptedStream,
                new PayloadUnlockInputs("CorrectHorseBatteryStaple!123", null, null),
                CancellationToken.None);
            FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes)
                ?? throw new InvalidOperationException("Payload metadata could not be read.");

            Assert.Equal(EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305, payload.Header.AlgorithmId);
            Assert.Equal(EncryptionAlgorithmCatalog.ChaCha20Poly1305, metadata.Algorithm);
            Assert.Equal(256, metadata.KeySizeBits);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VerifyLockedFileV3_AuthenticatesCompressedPayloadPadding()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(compressFiles: true);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string inputPath = Path.Combine(tempRoot, "compressed-padding.txt");
        File.WriteAllText(inputPath, new string('A', 300_000));

        try
        {
            MethodInfo encryptMethod = typeof(MainWindow).GetMethod("EncryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EncryptFileAdvancedV3 method not found.");
            MethodInfo verifyMethod = typeof(MainWindow).GetMethod("VerifyLockedFileV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("VerifyLockedFileV3 method not found.");

            encryptMethod.Invoke(window, [inputPath, "CorrectHorseBatteryStaple!123", options, null]);
            string lockedPath = Directory.EnumerateFiles(tempRoot, "*.locked").Single();
            byte[] payload = File.ReadAllBytes(lockedPath);
            payload[payload.Length - sizeof(int) - 1] ^= 0x40;
            File.WriteAllBytes(lockedPath, payload);

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
                verifyMethod.Invoke(window, [lockedPath, "CorrectHorseBatteryStaple!123", options, null]));

            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Aes256GcmV3CompatibilityFixture_StillDecrypts()
    {
        byte[] fixtureBytes = Convert.FromBase64String(
            "RkxLUgMBAQEDAAAAAAABAAgAAAAAgAAACOwLt+DfThb2AQEhxPQvXokBMbyrErRlgI1DctRj6PSrge2svZ1/x8jm9ZisfMJqm7EHUg+EvqTky9R24tW8YvA5E17VpTrqXe0ocbaI+rdaLg8JCtH0yUAR+uAA+QMzdMfp7DaIuS8CAAC8lsjejkP2VRMBq86uGpi1tEnOpI4tI6oVZh/PZLD2hhvZZHaxJffhlLjognrCCfb+w9tASAC2S7fMmA00HfAd4eVzK0bSkatFAS2k04YO9kWa9j2+adxKYAj+TIVabP74qfDHcXsrHUPcikGZypNvwmxMHTlnQuVpbVb5iAoeONCcl489QvhtvSUucwCxdG+QePH/TW+Sssi7jEvJ/16R2rtAktEpT+4KDKeYxiJKxEsi1Bc8ssgMztQM/DwaZt2gKk+SY6O3jvu7C6ibOhQje9qwkkEVjZ2lbsXMUxRVq06+voCWVgi7ZEZhuDDZQzIjVVAzEo6nYbOQDAC3RFbnUDHA1oe30mMfdDbZ8raOtzyaBrTXBHtRc8twzLgZaBfUBgiAPPMFR6WPVGDmb2J1+1MqMeZHKwU7y+a+YXSZyyuE/3dxI+0GdO3QxW4hgbOVHF8HoerKOZJ/OY5crsJgoSyrmCcV8ln7qRAkrsmtdDVF7RWRHRnGKwTOA8xcz5gVg+wPKjM51XCMBHIzeEdf6oUKIAQQQKeYvdhAv7PArEGFfbqUx6lYjb7v7CoUm32HOnmJhBNXk5qsnMxN7Mtli2u8+8x3YtFX3P92Zbg/sdv6dKjDUOAJbdDzE6PNyScE+QRMIgGFwWQAfJjvNN5B07etxIYvB83/244ftynCkM2g86esj8J5vq2vaAUUK7N1Sfyagp8czoPijKYRdcFx0feo8023dBs3SXwzQL9Hav8AzrTRN1D5C9WEjESkNQAAAAA=");
        using var input = new MemoryStream(fixtureBytes);
        using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
            input,
            new PayloadUnlockInputs("CompatibilityFixturePassword!234", null, null),
            CancellationToken.None);
        FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes)
            ?? throw new InvalidOperationException("Fixture metadata could not be read.");
        using var plaintext = new MemoryStream();

        payload.PlaintextStream.CopyTo(plaintext);

        Assert.Equal(PayloadChunkedService.LegacyVersion, payload.Header.Version);
        Assert.Equal(EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm, payload.Header.AlgorithmId);
        Assert.Equal(EncryptionAlgorithmCatalog.Aes256Gcm, metadata.Algorithm);
        MainWindow.ValidateFilePayloadMetadata(metadata);
        MainWindow.ValidatePayloadMetadataAlgorithm(
            metadata.Algorithm,
            metadata.KeySizeBits,
            payload.Header.AlgorithmId,
            payload.Header.Version,
            "File payload metadata");
        Assert.Equal("aes-v3-fixture.txt", metadata.OriginalFileName);
        Assert.Equal("FileLocker AES v3 fixture content", System.Text.Encoding.UTF8.GetString(plaintext.ToArray()));
    }

    [Fact]
    public void DecryptFileAdvancedV3_InvalidMetadataTimestampDoesNotLeaveOutput()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(compressFiles: false);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string lockedPath = Path.Combine(tempRoot, "invalid-metadata.locked");
        string outputPath = Path.Combine(tempRoot, "bad-date.txt");
        byte[] plaintext = Encoding.UTF8.GetBytes("payload with invalid metadata timestamp");
        var metadata = new FilePayloadMetadata
        {
            OriginalFileName = "bad-date.txt",
            OriginalSize = plaintext.Length,
            CreationTimeUtc = new DateTime(1600, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            LastWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastAccessTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentHashBase64 = Convert.ToBase64String(SHA256.HashData(plaintext)),
            Algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
            KeySizeBits = 256
        };

        try
        {
            using (FileStream output = File.Create(lockedPath))
            {
                PayloadChunkedService.WritePayload(
                    output,
                    JsonSerializer.SerializeToUtf8Bytes(metadata),
                    (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
                    new PayloadWriteInputs("CorrectHorseBatteryStaple!123", null, null, 32_768, 0x01),
                    CancellationToken.None);
            }

            MethodInfo decryptMethod = typeof(MainWindow).GetMethod("DecryptFileAdvancedV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DecryptFileAdvancedV3 method not found.");

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
                decryptMethod.Invoke(window, [lockedPath, "CorrectHorseBatteryStaple!123", options, null, null]));

            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Contains("timestamp", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outputPath));
            Assert.DoesNotContain(Directory.EnumerateFiles(tempRoot), path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VerifyLockedFileV3_RejectsMetadataAlgorithmMismatch()
    {
        object window = CreateWindowWithoutConstructor();
        object options = CreateDefaultOptions(compressFiles: false);
        string tempRoot = Path.Combine(Path.GetTempPath(), "filelocker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string lockedPath = Path.Combine(tempRoot, "algorithm-mismatch.locked");
        byte[] plaintext = Encoding.UTF8.GetBytes("payload with mismatched algorithm metadata");
        var metadata = new FilePayloadMetadata
        {
            OriginalFileName = "algorithm-mismatch.txt",
            OriginalSize = plaintext.Length,
            CreationTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastAccessTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentHashBase64 = Convert.ToBase64String(SHA256.HashData(plaintext)),
            Algorithm = EncryptionAlgorithmCatalog.ChaCha20Poly1305,
            KeySizeBits = 256
        };

        try
        {
            using (FileStream output = File.Create(lockedPath))
            {
                PayloadChunkedService.WritePayload(
                    output,
                    JsonSerializer.SerializeToUtf8Bytes(metadata),
                    (stream, _) => stream.Write(plaintext, 0, plaintext.Length),
                    new PayloadWriteInputs("CorrectHorseBatteryStaple!123", null, null, 32_768, 0x01),
                    CancellationToken.None);
            }

            MethodInfo verifyMethod = typeof(MainWindow).GetMethod("VerifyLockedFileV3", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("VerifyLockedFileV3 method not found.");

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
                verifyMethod.Invoke(window, [lockedPath, "CorrectHorseBatteryStaple!123", options, null]));

            Assert.IsType<InvalidDataException>(ex.InnerException);
            Assert.Contains("algorithm", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static object CreateWindowWithoutConstructor()
    {
        return FormatterServices.GetUninitializedObject(typeof(MainWindow));
    }

    private static object CreateDefaultOptions(
        bool compressFiles = true,
        string algorithm = "AES-GCM",
        int keySizeBits = 256)
    {
        Type metadataType = typeof(MainWindow).GetNestedType("MetadataOverridesSnapshot", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MetadataOverridesSnapshot type not found.");
        Type optionsType = typeof(MainWindow).GetNestedType("ProcessingRunOptions", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProcessingRunOptions type not found.");

        object metadata = Activator.CreateInstance(
            metadataType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["", "", false, "", ""],
            culture: null) ?? throw new InvalidOperationException("Could not create metadata snapshot.");

        return Activator.CreateInstance(
            optionsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                compressFiles,
                false,
                false,
                algorithm,
                "Encrypt / Decrypt",
                keySizeBits,
                false,
                false,
                true,
                false,
                "",
                false,
                "",
                true,
                false,
                false,
                "Current time",
                "",
                null,
                null,
                null,
                "Recommended",
                metadata
            ],
            culture: null) ?? throw new InvalidOperationException("Could not create processing options.");
    }

    private static long? GetNullableLongProperty(object target, string propertyName)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property not found: {propertyName}");
        return (long?)property.GetValue(target);
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property not found: {propertyName}");
        return (string?)property.GetValue(target) ?? string.Empty;
    }
}
#pragma warning restore SYSLIB0050

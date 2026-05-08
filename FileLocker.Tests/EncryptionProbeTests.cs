using System.Reflection;
using System.Runtime.Serialization;
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

    private static object CreateWindowWithoutConstructor()
    {
        return FormatterServices.GetUninitializedObject(typeof(MainWindow));
    }

    private static object CreateDefaultOptions(bool compressFiles = true)
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
                "AES-GCM",
                "Encrypt / Decrypt",
                256,
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
}
#pragma warning restore SYSLIB0050

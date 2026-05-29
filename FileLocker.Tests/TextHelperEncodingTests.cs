using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace FileLocker.Tests;

#pragma warning disable SYSLIB0050
public sealed class TextHelperEncodingTests
{
    [Fact]
    public void RunHashOrEncode_ProducesAesGcmPayloadWithExpectedFieldLengths()
    {
        string output = InvokeRunHashOrEncode(
            "secret text",
            "AES-GCM",
            256,
            "CorrectHorseBatteryStaple!123");

        const string prefix = "AES-GCM (256-bit): ";
        Assert.StartsWith(prefix, output);

        byte[] payload = Convert.FromBase64String(output[prefix.Length..]);
        using var stream = new MemoryStream(payload);

        Assert.Equal(16, ReadLengthPrefixed(stream).Length);
        Assert.Equal(12, ReadLengthPrefixed(stream).Length);
        Assert.Equal(16, ReadLengthPrefixed(stream).Length);
        Assert.Equal(Encoding.UTF8.GetByteCount("secret text"), ReadLengthPrefixed(stream).Length);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void RunHashOrEncode_StillProducesSha256Hex()
    {
        string output = InvokeRunHashOrEncode("secret text", "SHA-256", 256, "");

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("secret text"))),
            output);
    }

    [Fact]
    public void RunHashOrEncode_UsesHashAlgorithmInsteadOfKeySizeForSha512()
    {
        string output = InvokeRunHashOrEncode("secret text", "SHA-512", 256, "");

        Assert.Equal(
            Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes("secret text"))),
            output);
    }

    [Fact]
    public void RunHashOrEncode_RejectsUnsupportedShaTextHelperAlgorithm()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeRunHashOrEncode("secret text", "SHA-1", 256, ""));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("Unsupported hash algorithm", ex.InnerException?.Message);
    }

    [Fact]
    public void RunHashOrEncode_RejectsAlgorithmContainingBase64WhenNotExact()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeRunHashOrEncode(
                "secret text",
                "NotBase64",
                256,
                "CorrectHorseBatteryStaple!123"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("AES-GCM helpers", ex.InnerException?.Message);
    }

    [Fact]
    public void RunHashOrEncode_RejectsAesGcmSivForAesGcmTextHelper()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeRunHashOrEncode(
                "secret text",
                EncryptionAlgorithmCatalog.Aes256GcmSiv,
                256,
                "CorrectHorseBatteryStaple!123"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("AES-GCM helpers", ex.InnerException?.Message);
    }

    [Fact]
    public void RunHashOrEncode_RejectsInvalidAesGcmTextHelperKeySize()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeRunHashOrEncode(
                "secret text",
                EncryptionAlgorithmCatalog.Aes256Gcm,
                512,
                "CorrectHorseBatteryStaple!123"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("256-bit key", ex.InnerException?.Message);
    }

    [Fact]
    public void RunHashOrEncode_RejectsAesGcmTextHelperInputsTooLargeForLengthPrefix()
    {
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() =>
            InvokeRunHashOrEncode(
                new string('A', ushort.MaxValue + 1),
                EncryptionAlgorithmCatalog.Aes256Gcm,
                256,
                "CorrectHorseBatteryStaple!123"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("too large", ex.InnerException?.Message);
    }

    private static string InvokeRunHashOrEncode(string input, string algorithm, int keySize, string password)
    {
        object window = FormatterServices.GetUninitializedObject(typeof(MainWindow));
        MethodInfo method = typeof(MainWindow).GetMethod("RunHashOrEncode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunHashOrEncode method not found.");
        return (string?)method.Invoke(window, [input, algorithm, keySize, password, null])
            ?? throw new InvalidOperationException("RunHashOrEncode returned null.");
    }

    private static byte[] ReadLengthPrefixed(Stream stream)
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(ushort)];
        int read = stream.Read(lengthBytes);
        if (read != sizeof(ushort))
        {
            throw new EndOfStreamException();
        }

        byte[] value = new byte[BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes)];
        read = stream.Read(value, 0, value.Length);
        if (read != value.Length)
        {
            throw new EndOfStreamException();
        }

        return value;
    }
}
#pragma warning restore SYSLIB0050

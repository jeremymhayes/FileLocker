using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;

namespace FileLocker.Tests;

public sealed class OperationFailureClassifierTests
{
    [Theory]
    [InlineData(typeof(FileNotFoundException), "Missing file")]
    [InlineData(typeof(DirectoryNotFoundException), "Missing folder")]
    [InlineData(typeof(PathTooLongException), "Path too long")]
    [InlineData(typeof(InvalidDataException), "Unsupported or corrupted payload")]
    [InlineData(typeof(CryptographicException), "Authentication failed or corrupted payload")]
    [InlineData(typeof(InvalidCipherTextException), "Authentication failed or corrupted payload")]
    [InlineData(typeof(UnauthorizedAccessException), "Access denied or wrong unlock secret")]
    [InlineData(typeof(OperationCanceledException), "Cancelled")]
    [InlineData(typeof(ArgumentException), "Invalid input")]
    [InlineData(typeof(NotSupportedException), "Unsupported operation")]
    public void Classify_ReturnsSpecificCategoryForKnownFailures(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        string category = OperationFailureClassifier.Classify(exception);

        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData("Unsupported file encryption algorithm.")]
    [InlineData("Unsupported payload algorithm.")]
    [InlineData("Legacy payload headers support only AES-256-GCM.")]
    [InlineData("File payload metadata references an unsupported payload header algorithm.")]
    [InlineData("Folder package metadata contains an unsupported algorithm name.")]
    [InlineData("ChaCha20-Poly1305 is supported for reading existing payloads but is not available for new encrypted files.")]
    [InlineData("AES-256-GCM-SIV is recognized but is not supported by this build.")]
    public void Classify_SeparatesUnsupportedEncryptionFailuresFromNotSupportedMessages(string message)
    {
        string category = OperationFailureClassifier.Classify(new NotSupportedException(message));

        Assert.Equal("Unsupported encryption algorithm or runtime", category);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), "Unsupported file encryption algorithm.")]
    [InlineData(typeof(InvalidOperationException), "ChaCha20-Poly1305 is not available for new encrypted files.")]
    public void Classify_SeparatesUnsupportedEncryptionFailuresAcrossWrapperTypes(Type exceptionType, string message)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, message)!;

        string category = OperationFailureClassifier.Classify(exception);

        Assert.Equal("Unsupported encryption algorithm or runtime", category);
    }

    [Fact]
    public void Classify_SeparatesUnsupportedPayloadAlgorithmFromGenericCorruption()
    {
        string category = OperationFailureClassifier.Classify(new InvalidDataException("Unsupported payload algorithm."));

        Assert.Equal("Unsupported encryption algorithm or runtime", category);
    }

    [Fact]
    public void Classify_SeparatesUnsupportedPlatformCryptoFailures()
    {
        string category = OperationFailureClassifier.Classify(
            new PlatformNotSupportedException("ChaCha20-Poly1305 is not supported on this Windows runtime."));

        Assert.Equal("Unsupported encryption algorithm or runtime", category);
    }

    [Fact]
    public void Classify_UsesBaseExceptionForWrappedFailures()
    {
        var exception = new InvalidOperationException("Wrapped failure.", new PathTooLongException());

        string category = OperationFailureClassifier.Classify(exception);

        Assert.Equal("Path too long", category);
    }

    [Fact]
    public void Classify_UsesWrapperMessageForUnsupportedEncryptionFailures()
    {
        var exception = new InvalidOperationException(
            "Unsupported payload algorithm.",
            new IOException("Simulated low-level I/O failure."));

        string category = OperationFailureClassifier.Classify(exception);

        Assert.Equal("Unsupported encryption algorithm or runtime", category);
    }

    [Fact]
    public void Classify_SeparatesIntegrityValidationFailuresFromAccessDenied()
    {
        string category = OperationFailureClassifier.Classify(
            new UnauthorizedAccessException("File failed integrity validation after decryption."));

        Assert.Equal("Integrity verification failed", category);
    }

    [Fact]
    public void Classify_KeepsGenericUnauthorizedAccessCategory()
    {
        string category = OperationFailureClassifier.Classify(
            new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Equal("Access denied or wrong unlock secret", category);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void Classify_SeparatesFileInUseIoFailures(int hresult)
    {
        string category = OperationFailureClassifier.Classify(new IOExceptionWithHResult(hresult));

        Assert.Equal("File in use", category);
    }

    [Fact]
    public void Classify_KeepsGenericIoFallback()
    {
        string category = OperationFailureClassifier.Classify(new IOExceptionWithHResult(unchecked((int)0x8007001F)));

        Assert.Equal("File in use or I/O error", category);
    }

    private sealed class IOExceptionWithHResult : IOException
    {
        public IOExceptionWithHResult(int hresult)
            : base("Simulated I/O failure.")
        {
            HResult = hresult;
        }
    }
}

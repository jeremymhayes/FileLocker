namespace FileLocker.Tests;

public sealed class OperationHistoryAlgorithmTests
{
    [Fact]
    public void Format_DoesNotInventKeySizeForUnknownAlgorithms()
    {
        string formatted = OperationHistoryAlgorithm.Format(null, 256);

        Assert.Equal(OperationHistoryAlgorithm.Unknown, formatted);
    }

    [Fact]
    public void Format_UsesFallbackForUnknownAlgorithms()
    {
        string formatted = OperationHistoryAlgorithm.Format(null, 256, "Secure delete");

        Assert.Equal("Secure delete", formatted);
    }

    [Fact]
    public void Format_AppendsKeySizeForKnownAlgorithmWithoutSize()
    {
        string formatted = OperationHistoryAlgorithm.Format("ChaCha20-Poly1305", 256);

        Assert.Equal("ChaCha20-Poly1305 (256-bit)", formatted);
    }

    [Theory]
    [InlineData("AES-GCM", EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData("aes256gcm", EncryptionAlgorithmCatalog.Aes256Gcm)]
    [InlineData("SHA512", FileHashService.Sha512)]
    [InlineData(" sha-256 ", FileHashService.Sha256)]
    public void NormalizeName_MapsKnownAlgorithmAliasesToCanonicalNames(string algorithm, string expected)
    {
        string normalized = OperationHistoryAlgorithm.NormalizeName(algorithm);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizeName_LeavesNonCryptoHistoryLabelsAlone()
    {
        string normalized = OperationHistoryAlgorithm.NormalizeName("Secure delete");

        Assert.Equal("Secure delete", normalized);
    }

    [Fact]
    public void NormalizeName_CleansAndCapsUnknownAlgorithmLabels()
    {
        string normalized = OperationHistoryAlgorithm.NormalizeName(
            $"  Custom\r\nCipher\t{new string('A', OperationHistoryAlgorithm.MaxAlgorithmLabelLength + 20)}  ");

        Assert.StartsWith("Custom Cipher ", normalized);
        Assert.DoesNotContain('\r', normalized);
        Assert.DoesNotContain('\n', normalized);
        Assert.DoesNotContain('\t', normalized);
        Assert.Equal(OperationHistoryAlgorithm.MaxAlgorithmLabelLength, normalized.Length);
    }

    [Fact]
    public void NormalizeName_RemovesUnicodeFormatCharacters()
    {
        string normalized = OperationHistoryAlgorithm.NormalizeName("Custom\u202ECipher");

        Assert.Equal("Custom Cipher", normalized);
    }

    [Fact]
    public void Format_DoesNotAppendKeySizeForNonCryptoLabels()
    {
        string formatted = OperationHistoryAlgorithm.Format("Secure delete", 3);

        Assert.Equal("Secure delete", formatted);
    }
}

using FileLocker;

namespace FileLocker.Tests;

public sealed class HashInputNormalizerTests
{
    [Fact]
    public void Normalize_ReturnsPlainHashLowercased()
    {
        string hash = new('A', 64);

        string normalized = HashInputNormalizer.Normalize(hash);

        Assert.Equal(new string('a', 64), normalized);
    }

    [Fact]
    public void Normalize_ExtractsHashFromChecksumLine()
    {
        string hash = new('b', 64);

        string normalized = HashInputNormalizer.Normalize($"{hash}  payload.locked");

        Assert.Equal(hash, normalized);
    }

    [Fact]
    public void Normalize_ExtractsHashFromSha256PrefixLine()
    {
        string hash = new('C', 64);

        string normalized = HashInputNormalizer.Normalize($"SHA256 (payload.locked) = {hash}");

        Assert.Equal(new string('c', 64), normalized);
    }

    [Fact]
    public void Normalize_PrefersStandaloneHashRunOverCompactedAdjacentHashes()
    {
        string firstHash = new('a', 64);
        string secondHash = new('b', 64);

        string normalized = HashInputNormalizer.Normalize($"{firstHash}{Environment.NewLine}{secondHash}");

        Assert.Equal(firstHash, normalized);
    }

    [Fact]
    public void Normalize_AllowsSeparatedHexDigest()
    {
        string hash = new('d', 64);
        string separated = string.Join(" ", Enumerable.Range(0, 32).Select(index => hash.Substring(index * 2, 2)));

        string normalized = HashInputNormalizer.Normalize(separated);

        Assert.Equal(hash, normalized);
    }

    [Fact]
    public void Normalize_ExtractsColonSeparatedHashAfterAlgorithmLabel()
    {
        string hash = new('e', 64);
        string separated = string.Join(":", Enumerable.Range(0, 32).Select(index => hash.Substring(index * 2, 2)));

        string normalized = HashInputNormalizer.Normalize($"SHA256: {separated}");

        Assert.Equal(hash, normalized);
    }

    [Fact]
    public void Normalize_ExtractsHyphenSeparatedSha512AfterAlgorithmLabel()
    {
        string hash = new('F', 128);
        string separated = string.Join("-", Enumerable.Range(0, 64).Select(index => hash.Substring(index * 2, 2)));

        string normalized = HashInputNormalizer.Normalize($"sha-512: {separated}");

        Assert.Equal(new string('f', 128), normalized);
    }

    [Fact]
    public void Normalize_PreservesOldFallbackForInvalidInput()
    {
        string normalized = HashInputNormalizer.Normalize("ab cd not-a-hash");

        Assert.Equal("abcdnot-a-hash", normalized);
    }

    [Fact]
    public void TryNormalizeSupportedHash_RejectsInvalidFallbackText()
    {
        bool normalized = HashInputNormalizer.TryNormalizeSupportedHash("ab cd not-a-hash", out string hash);

        Assert.False(normalized);
        Assert.Equal("abcdnot-a-hash", hash);
    }

    [Fact]
    public void TryNormalizeSupportedHash_AcceptsChecksumLine()
    {
        string expected = new('A', 64);

        bool normalized = HashInputNormalizer.TryNormalizeSupportedHash($"{expected}  payload.locked", out string hash);

        Assert.True(normalized);
        Assert.Equal(new string('a', 64), hash);
    }
}

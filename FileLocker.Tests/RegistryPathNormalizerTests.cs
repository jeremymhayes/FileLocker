using FileLocker;

namespace FileLocker.Tests;

public sealed class RegistryPathNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("  \"  \"  ")]
    public void Normalize_ReturnsNullForBlankValues(string? value)
    {
        Assert.Null(RegistryPathNormalizer.Normalize(value));
    }

    [Fact]
    public void Normalize_TrimsOuterQuotesAndWhitespace()
    {
        string? normalized = RegistryPathNormalizer.Normalize("  \"C:\\Program Files\\Vendor\\App.exe\"  ");

        Assert.Equal("C:\\Program Files\\Vendor\\App.exe", normalized);
    }

    [Fact]
    public void Normalize_ExpandsEnvironmentVariables()
    {
        string variableName = $"FILELOCKER_TEST_ROOT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "C:\\FileLockerTest");

        try
        {
            string? normalized = RegistryPathNormalizer.Normalize($"%{variableName}%\\Vendor");

            Assert.Equal("C:\\FileLockerTest\\Vendor", normalized);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void Normalize_ReturnsNullForOversizedValues()
    {
        Assert.Null(RegistryPathNormalizer.Normalize(new string('A', RegistryPathNormalizer.MaxRegistryPathChars + 1)));
    }

    [Fact]
    public void Normalize_ReturnsNullForControlCharactersBeforeExpansion()
    {
        Assert.Null(RegistryPathNormalizer.Normalize("C:\\FileLocker\0Bad\\startup.exe"));
    }

    [Fact]
    public void Normalize_ReturnsNullForUnicodeFormatCharactersBeforeExpansion()
    {
        Assert.Null(RegistryPathNormalizer.Normalize("C:\\FileLocker\\Bad\u202E\\startup.exe"));
    }

    [Fact]
    public void Normalize_ReturnsNullWhenReferencedEnvironmentVariableContainsControlCharacters()
    {
        string variableName = $"FILELOCKER_TEST_BAD_ROOT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "C:\\FileLocker\r\nBad");

        try
        {
            Assert.Null(RegistryPathNormalizer.Normalize($"%{variableName}%\\startup.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void Normalize_ReturnsNullWhenReferencedEnvironmentVariableContainsUnicodeFormatCharacters()
    {
        string variableName = $"FILELOCKER_TEST_BAD_FORMAT_ROOT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "C:\\FileLocker\\Bad\u202E");

        try
        {
            Assert.Null(RegistryPathNormalizer.Normalize($"%{variableName}%\\startup.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void Normalize_ReturnsNullForOversizedExpandedValues()
    {
        string variableName = $"FILELOCKER_TEST_ROOT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, new string('A', RegistryPathNormalizer.MaxRegistryPathChars + 1));

        try
        {
            Assert.Null(RegistryPathNormalizer.Normalize($"%{variableName}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }
}

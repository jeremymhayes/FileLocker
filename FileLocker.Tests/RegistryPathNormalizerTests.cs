using FileLocker;

namespace FileLocker.Tests;

public sealed class RegistryPathNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
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
}

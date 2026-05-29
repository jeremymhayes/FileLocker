using System.Reflection;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;

namespace FileLocker.Tests;

public sealed class FriendlyExceptionMessageTests
{
    [Theory]
    [InlineData(typeof(CryptographicException))]
    [InlineData(typeof(InvalidCipherTextException))]
    public void GetFriendlyExceptionMessage_UsesSafeMessageForAuthenticatedDecryptionFailures(Type exceptionType)
    {
        var exception = new InvalidOperationException(
            "Decryption failed.",
            (Exception)Activator.CreateInstance(exceptionType, "raw crypto failure")!);

        string message = InvokeGetFriendlyExceptionMessage(exception);

        Assert.Equal("The encrypted payload could not be authenticated. The password may be wrong or the file may be corrupted.", message);
        Assert.DoesNotContain("raw crypto failure", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetFriendlyExceptionMessage_UsesSafeMessageForPayloadUnlockFailures()
    {
        var exception = new UnauthorizedAccessException("The supplied password, keyfile, or recovery key could not unlock this payload.");

        string message = InvokeGetFriendlyExceptionMessage(exception);

        Assert.Equal("The supplied password, keyfile, or recovery key could not unlock this payload.", message);
    }

    [Fact]
    public void GetFriendlyExceptionMessage_RetainsUnsupportedPayloadMessages()
    {
        var exception = new InvalidDataException("Unsupported payload algorithm.");

        string message = InvokeGetFriendlyExceptionMessage(exception);

        Assert.Equal("Unsupported payload algorithm.", message);
    }

    [Fact]
    public void GetFriendlyExceptionMessage_RedactsAndCapsLongMessages()
    {
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exception = new InvalidOperationException($"{profilePath}\\Documents\\secret.txt {new string('A', 4096)}");

        string message = InvokeGetFriendlyExceptionMessage(exception);

        Assert.True(message.Length <= 2048);
        Assert.DoesNotContain(profilePath, message);
        Assert.Contains("%USERPROFILE%", message);
        Assert.EndsWith("Message truncated.", message);
    }

    private static string InvokeGetFriendlyExceptionMessage(Exception exception)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("GetFriendlyExceptionMessage", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetFriendlyExceptionMessage was not found.");

        return (string)method.Invoke(null, [exception, "Fallback failure."])!;
    }
}

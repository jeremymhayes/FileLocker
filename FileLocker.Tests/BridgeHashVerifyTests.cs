using System.Reflection;

namespace FileLocker.Tests;

public sealed class BridgeHashVerifyTests
{
    [Fact]
    public void VerifyHashFromBridge_RejectsInvalidExpectedHash()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeVerifyHashFromBridge(new string('a', 64), "not-a-hash"));

        Assert.Equal("Paste a SHA-256 or SHA-512 hash before verifying.", ex.Message);
    }

    [Fact]
    public void VerifyHashFromBridge_AcceptsChecksumLine()
    {
        string hash = new('b', 64);

        object result = InvokeVerifyHashFromBridge(hash, $"{hash}  payload.locked");
        object? match = result.GetType().GetProperty("match")?.GetValue(result);

        Assert.Equal(true, match);
    }

    private static object InvokeVerifyHashFromBridge(string generatedHash, string expectedHash)
    {
        Type requestType = typeof(MainWindow).GetNestedType("HashVerifyRequest", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HashVerifyRequest type not found.");
        object request = Activator.CreateInstance(requestType)
            ?? throw new InvalidOperationException("HashVerifyRequest could not be created.");
        requestType.GetProperty("GeneratedHash")?.SetValue(request, generatedHash);
        requestType.GetProperty("ExpectedHash")?.SetValue(request, expectedHash);

        MethodInfo method = typeof(MainWindow).GetMethod("VerifyHashFromBridge", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VerifyHashFromBridge method not found.");

        try
        {
            return method.Invoke(null, [request])
                ?? throw new InvalidOperationException("VerifyHashFromBridge returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}

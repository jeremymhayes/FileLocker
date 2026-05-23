using System.Reflection;
using System.Text.Json;

namespace FileLocker.Tests;

public sealed class BridgePayloadTests
{
    [Fact]
    public void ReadPayload_RejectsUndefinedPayloadWithBridgeMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => InvokeReadPayload<Dictionary<string, string>>(default));

        Assert.Equal("Bridge payload was empty or invalid.", ex.Message);
    }

    [Fact]
    public void ReadPayload_RejectsNullPayloadWithBridgeMessage()
    {
        using JsonDocument document = JsonDocument.Parse("null");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => InvokeReadPayload<Dictionary<string, string>>(document.RootElement));

        Assert.Equal("Bridge payload was empty or invalid.", ex.Message);
    }

    [Fact]
    public void ReadPayload_DeserializesValidPayload()
    {
        using JsonDocument document = JsonDocument.Parse("""{"path":"C:\\Temp\\demo.txt"}""");

        Dictionary<string, string> payload = InvokeReadPayload<Dictionary<string, string>>(document.RootElement);

        Assert.Equal(@"C:\Temp\demo.txt", payload["path"]);
    }

    [Fact]
    public void ReadPayload_RejectsMalformedPayloadWithBridgeMessage()
    {
        using JsonDocument document = JsonDocument.Parse("[1]");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => InvokeReadPayload<Dictionary<string, string>>(document.RootElement));

        Assert.Equal("Bridge payload was empty or invalid.", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    private static T InvokeReadPayload<T>(JsonElement payload)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("ReadPayload", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReadPayload helper was not found.");

        try
        {
            return (T)(method.MakeGenericMethod(typeof(T)).Invoke(null, [payload])
                ?? throw new InvalidOperationException("ReadPayload returned null."));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}

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

    [Theory]
    [InlineData("""{"path":"C:\\Temp\\safe.txt","Path":"C:\\Temp\\other.txt"}""")]
    [InlineData("""{"paths":[{"path":"C:\\Temp\\safe.txt","PATH":"C:\\Temp\\other.txt"}]}""")]
    public void ReadPayload_RejectsDuplicatePayloadFields(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => InvokeReadPayload<Dictionary<string, object>>(document.RootElement));

        Assert.Equal("Bridge payload contains duplicate fields.", ex.Message);
    }

    [Theory]
    [InlineData("""{"id":"1","action":"app.getInitialState","Action":"settings.get","payload":{}}""")]
    [InlineData("""{"id":"1","Id":"2","action":"app.getInitialState","payload":{}}""")]
    public void ReadBridgeRequest_RejectsDuplicateEnvelopeFields(string json)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeReadBridgeRequest(json));

        Assert.Equal("Bridge request contains duplicate fields.", ex.Message);
    }

    [Fact]
    public void ReadBridgeRequest_RejectsMissingIdOrAction()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeReadBridgeRequest("""{"id":"1","payload":{}}"""));

        Assert.Equal("Bridge request is missing an id or action.", ex.Message);
    }

    [Fact]
    public void ReadBridgeRequest_RejectsOversizedIdOrAction()
    {
        string oversizedId = new('i', 129);
        string oversizedAction = new('a', 129);

        InvalidOperationException idEx = Assert.Throws<InvalidOperationException>(() =>
            InvokeReadBridgeRequest($"{{\"id\":\"{oversizedId}\",\"action\":\"app.getInitialState\",\"payload\":{{}}}}"));

        InvalidOperationException actionEx = Assert.Throws<InvalidOperationException>(() =>
            InvokeReadBridgeRequest($"{{\"id\":\"1\",\"action\":\"{oversizedAction}\",\"payload\":{{}}}}"));

        Assert.Equal("Bridge request id or action is too long.", idEx.Message);
        Assert.Equal("Bridge request id or action is too long.", actionEx.Message);
    }

    [Theory]
    [InlineData("""{"id":"1\r\n2","action":"app.getInitialState","payload":{}}""")]
    [InlineData("""{"id":"1","action":"app.getInitialState\u202E","payload":{}}""")]
    public void ReadBridgeRequest_RejectsUnsafeIdOrActionText(string json)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeReadBridgeRequest(json));

        Assert.Equal("Bridge request id or action is invalid.", ex.Message);
    }

    [Fact]
    public void ReadBridgeRequest_RejectsMalformedEnvelopeWithStableMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        InvokeReadBridgeRequest("{not-json"));

        Assert.Equal("Bridge request was empty or invalid.", ex.Message);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
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

    private static object InvokeReadBridgeRequest(string json)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("ReadBridgeRequest", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReadBridgeRequest helper was not found.");

        try
        {
            return method.Invoke(null, [json])
                ?? throw new InvalidOperationException("ReadBridgeRequest returned null.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}

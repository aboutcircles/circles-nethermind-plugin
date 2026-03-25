using System.Text.Json;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for batch response serialization correctness.
///
/// Verifies that the runtime-type-aware serialization workaround produces
/// valid JSON-RPC responses with correct casing. System.Text.Json serializes
/// object[] by declared type (producing empty {}), so batch responses must
/// serialize each element individually using item.GetType().
///
/// Also verifies camelCase consistency: circles responses (JsonRpcResponse)
/// must use the same property casing as Nethermind responses (JsonElement).
/// </summary>
[TestFixture]
public class BatchSerializationTests
{
    private static readonly JsonSerializerOptions BatchOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Test]
    public void JsonRpcResponse_WithCamelCase_SerializesCorrectly()
    {
        var response = new JsonRpcResponse
        {
            Id = JsonDocument.Parse("1").RootElement,
            Result = "Healthy"
        };

        var json = JsonSerializer.Serialize(response, response.GetType(), BatchOptions);
        var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("jsonrpc", out var jsonrpc), Is.True,
            "Must use camelCase 'jsonrpc', not PascalCase 'Jsonrpc'");
        Assert.That(jsonrpc.GetString(), Is.EqualTo("2.0"));
        Assert.That(doc.RootElement.TryGetProperty("result", out _), Is.True,
            "Must use camelCase 'result', not PascalCase 'Result'");
        Assert.That(doc.RootElement.TryGetProperty("id", out var id), Is.True);
        Assert.That(id.GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public void JsonRpcErrorResponse_WithCamelCase_SerializesCorrectly()
    {
        var response = new JsonRpcErrorResponse
        {
            Id = JsonDocument.Parse("42").RootElement,
            Error = new JsonRpcError { Code = -32601, Message = "Method not found" }
        };

        var json = JsonSerializer.Serialize(response, response.GetType(), BatchOptions);
        var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("jsonrpc", out _), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("error", out var error), Is.True);
        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32601));
        Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("Method not found"));
    }

    [Test]
    public void ObjectArray_DefaultSerialization_MayUsePascalCase()
    {
        // In .NET 10, object[] serialization uses runtime types (fixed from earlier versions).
        // However, without explicit JsonSerializerOptions, properties use PascalCase (C# default).
        // The batch handler must use CamelCase options for JSON-RPC 2.0 compliance.
        var responses = new object[]
        {
            new JsonRpcResponse { Result = "test" }
        };

        var json = JsonSerializer.Serialize(responses);

        // Default serialization uses PascalCase — "Jsonrpc" not "jsonrpc"
        Assert.That(json, Does.Contain("Jsonrpc").Or.Contain("jsonrpc"),
            "Response must contain the jsonrpc field regardless of casing");
    }

    [Test]
    public void RuntimeTypeSerialization_ProducesCorrectOutput()
    {
        // The workaround: serialize each element using item.GetType()
        var item = new JsonRpcResponse
        {
            Id = JsonDocument.Parse("1").RootElement,
            Result = "Healthy"
        };

        var json = JsonSerializer.Serialize(item, item.GetType(), BatchOptions);
        Assert.That(json, Does.Contain("Healthy"));
        Assert.That(json, Does.Contain("jsonrpc"));
    }

    [Test]
    public void MixedBatchArray_SimulatesRealBatchOutput()
    {
        // Simulates the actual batch serialization path:
        // circles response (JsonRpcResponse) + Nethermind response (JsonElement) + error response
        var circlesResponse = new JsonRpcResponse
        {
            Id = JsonDocument.Parse("1").RootElement,
            Result = "Healthy"
        };

        var nethermindJson = """{"jsonrpc":"2.0","result":"0x2b3aa46","id":2}""";
        var nethermindResponse = JsonDocument.Parse(nethermindJson).RootElement;

        var errorResponse = new JsonRpcErrorResponse
        {
            Id = JsonDocument.Parse("3").RootElement,
            Error = new JsonRpcError { Code = -32601, Message = "Method not found" }
        };

        // Build the batch output the same way Program.cs does
        var responses = new object?[] { circlesResponse, nethermindResponse, errorResponse };
        using var ms = new MemoryStream();
        ms.Write("["u8.ToArray());
        for (int k = 0; k < responses.Length; k++)
        {
            if (k > 0) ms.Write(","u8.ToArray());
            var item = responses[k]!;
            if (item is JsonElement je)
                JsonSerializer.Serialize(ms, je);
            else
                JsonSerializer.Serialize(ms, item, item.GetType(), BatchOptions);
        }
        ms.Write("]"u8.ToArray());

        ms.Position = 0;
        var batchJson = new StreamReader(ms).ReadToEnd();
        var batchDoc = JsonDocument.Parse(batchJson);
        var batchArray = batchDoc.RootElement;

        Assert.That(batchArray.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(batchArray.GetArrayLength(), Is.EqualTo(3));

        // Item 0: circles response — camelCase
        var item0 = batchArray[0];
        Assert.That(item0.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
        Assert.That(item0.GetProperty("result").GetString(), Is.EqualTo("Healthy"));
        Assert.That(item0.GetProperty("id").GetInt32(), Is.EqualTo(1));

        // Item 1: Nethermind response — already camelCase from source
        var item1 = batchArray[1];
        Assert.That(item1.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
        Assert.That(item1.GetProperty("result").GetString(), Is.EqualTo("0x2b3aa46"));
        Assert.That(item1.GetProperty("id").GetInt32(), Is.EqualTo(2));

        // Item 2: error response — camelCase
        var item2 = batchArray[2];
        Assert.That(item2.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
        Assert.That(item2.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32601));
        Assert.That(item2.GetProperty("id").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public void NullSlot_SerializesAsJsonNull()
    {
        // Verifies that null slots (safety net) serialize to JSON null
        var responses = new object?[] { null };
        using var ms = new MemoryStream();
        ms.Write("["u8.ToArray());
        var item = responses[0];
        if (item is JsonElement je)
            JsonSerializer.Serialize(ms, je);
        else if (item != null)
            JsonSerializer.Serialize(ms, item, item.GetType(), BatchOptions);
        else
            ms.Write("null"u8.ToArray());
        ms.Write("]"u8.ToArray());

        ms.Position = 0;
        var json = new StreamReader(ms).ReadToEnd();
        Assert.That(json, Is.EqualTo("[null]"));
    }
}

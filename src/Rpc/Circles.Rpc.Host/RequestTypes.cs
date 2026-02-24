using System.Text.Json;

namespace Circles.Rpc.Host;

/// <summary>
/// JSON-RPC 2.0 spec-compliant null ID. Used for error responses when the request ID
/// cannot be determined (parse errors, batch errors, etc.).
/// </summary>
public static class JsonRpcId
{
    private static readonly JsonDocument NullDoc = JsonDocument.Parse("null");
    private static readonly JsonDocument EmptyArrayDoc = JsonDocument.Parse("[]");

    public static JsonElement Null => NullDoc.RootElement;
    public static JsonElement EmptyArray => EmptyArrayDoc.RootElement;

    /// <summary>
    /// Coerces a JsonElement id to a safe value for serialization.
    /// Undefined → JSON null (valid per JSON-RPC 2.0 spec for indeterminate id).
    /// </summary>
    public static JsonElement CoerceId(JsonElement id)
        => id.ValueKind == JsonValueKind.Undefined ? Null : id;

    /// <summary>
    /// Coerces a JsonElement params to a safe value for serialization.
    /// Undefined → empty array (valid per JSON-RPC 2.0 spec for no params).
    /// </summary>
    public static JsonElement CoerceParams(JsonElement @params)
        => @params.ValueKind == JsonValueKind.Undefined ? EmptyArray : @params;
}

public class JsonRpcRequest
{
    public string? Jsonrpc { get; set; }
    public string? Method { get; set; }
    public JsonElement Params { get; set; }
    public JsonElement Id { get; set; }
}

public class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Result { get; set; }
    public JsonElement Id { get; set; } = JsonRpcId.Null;
}

public class JsonRpcErrorResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonRpcError? Error { get; set; }
    public JsonElement Id { get; set; } = JsonRpcId.Null;
}

public class JsonRpcError
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
}

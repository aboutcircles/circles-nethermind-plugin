using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Common.Dto;

namespace Circles.Rpc.Host;

// Define the structure for the incoming request body
public class JsonRpcRequest
{
    public string? Jsonrpc { get; set; } // Should be "2.0"
    public string? Method { get; set; }  // e.g., "circles_getTotalBalance"
    public JsonElement Params { get; set; } // Holds the array of parameters
    public int Id { get; set; }
}

public class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Result { get; set; }
    public int Id { get; set; }
}

// Define the structure for the outgoing error response
public class JsonRpcErrorResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonRpcError? Error { get; set; }
    public int Id { get; set; }
}

public class JsonRpcError
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
}

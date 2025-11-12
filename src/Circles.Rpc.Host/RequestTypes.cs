using System.Text.Json;

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

// Pathfinder DTOs
public class FlowRequest
{
    public string Source { get; set; } = "";
    public string Sink { get; set; } = "";
    public string TargetFlow { get; set; } = "0";
    public bool WithWrap { get; set; }
    public ISet<string> FromTokens { get; set; } = new HashSet<string>();
    public ISet<string> ToTokens { get; set; } = new HashSet<string>();
    public ISet<string> ExcludedFromTokens { get; set; } = new HashSet<string>();
    public ISet<string> ExcludedToTokens { get; set; } = new HashSet<string>();
    public IDictionary<string, string> SimulatedBalances { get; set; } = new Dictionary<string, string>();
    public IDictionary<string, IDictionary<string, int>> SimulatedTrusts { get; set; } = new Dictionary<string, IDictionary<string, int>>();
    public int MaxTransfers { get; set; } = 10;
}

public class Transfer
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Token { get; set; } = "";
    public string Flow { get; set; } = "0";
}

public class MaxFlowResponse
{
    public string Source { get; set; } = "";
    public string Sink { get; set; } = "";
    public List<Transfer> Path { get; set; } = new();
    public string TotalFlow { get; set; } = "0";
}

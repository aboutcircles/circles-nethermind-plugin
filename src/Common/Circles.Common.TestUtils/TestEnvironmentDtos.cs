using System.Text.Json;

namespace Circles.Common.TestUtils;

/// <summary>
/// Response from GET /health endpoint.
/// </summary>
public record HealthResponse
{
    public string Status { get; init; } = "";
    public int ActiveSessions { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Response from GET /api/v1/blocks/current endpoint.
/// </summary>
public record BlockInfo
{
    public long BlockNumber { get; init; }
}

/// <summary>
/// Response from GET /api/v1/blocks/{blockNumber}/exists endpoint.
/// </summary>
public record BlockExistsInfo
{
    public long BlockNumber { get; init; }
    public bool Exists { get; init; }
}

/// <summary>
/// Request body for POST /api/v1/session endpoint.
/// </summary>
public record SessionRequest
{
    public long BlockNumber { get; init; }
    public string[] Features { get; init; } = ["db"];
    public string Ttl { get; init; } = "10m";
}

/// <summary>
/// Response from session endpoints.
/// </summary>
public record SessionResponse
{
    public string SessionId { get; init; } = "";
    public long BlockNumber { get; init; }
    public PostgresInfo? Postgres { get; init; }
    public AnvilInfo? Anvil { get; init; }
    public RpcInfo? Rpc { get; init; }
    /// <summary>
    /// Session status (numeric enum: 0=Created, 1=Active, 2=Expired, 3=Terminated).
    /// </summary>
    public int Status { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// PostgreSQL connection information from a session.
/// </summary>
public record PostgresInfo
{
    public string ConnectionString { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Database { get; init; } = "";
}

/// <summary>
/// Anvil (EVM fork) information from a session.
/// </summary>
public record AnvilInfo
{
    public string RpcUrl { get; init; } = "";
    public string[] Accounts { get; init; } = [];
    public long ChainId { get; init; }
}

/// <summary>
/// Circles RPC information from a session.
/// </summary>
public record RpcInfo
{
    public string Url { get; init; } = "";
}

/// <summary>
/// Request to execute a SQL query through the test environment proxy.
/// </summary>
public record QueryRequest
{
    /// <summary>
    /// The SQL query to execute.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// Parameters for the query (optional).
    /// </summary>
    public Dictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Maximum number of rows to return (default: 1000).
    /// </summary>
    public int MaxRows { get; init; } = 1000;
}

/// <summary>
/// Response from a SQL query execution.
/// </summary>
public record QueryResponse
{
    /// <summary>
    /// Column names in the result set.
    /// </summary>
    public List<string> Columns { get; init; } = [];

    /// <summary>
    /// Rows of data, where each row is an array of values.
    /// </summary>
    public List<object?[]> Rows { get; init; } = [];

    /// <summary>
    /// Number of rows returned.
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Whether the result was truncated due to MaxRows limit.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Execution time in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; init; }
}

/// <summary>
/// Response for scalar query execution.
/// </summary>
public record ScalarResponse
{
    /// <summary>
    /// The scalar result value.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Execution time in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; init; }
}

/// <summary>
/// Exception thrown when a JSON-RPC call returns an error.
/// </summary>
public class JsonRpcException : Exception
{
    /// <summary>
    /// The raw JSON error object from the RPC response.
    /// </summary>
    public JsonElement ErrorObject { get; }

    /// <summary>
    /// The error code from the JSON-RPC error, if available.
    /// </summary>
    public int? ErrorCode { get; }

    public JsonRpcException(string message, JsonElement errorObject) : base(message)
    {
        ErrorObject = errorObject;
        if (errorObject.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.Number)
        {
            ErrorCode = codeElement.GetInt32();
        }
    }
}

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
    public string Status { get; init; } = "";
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

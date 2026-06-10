using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Circles.Common.TestUtils;

/// <summary>
/// Client for the Circles Test Environment API.
/// Provides session-based access to PostgreSQL databases filtered to specific block numbers.
///
/// Usage:
/// <code>
/// await using var session = await TestEnvironmentClient.CreateSessionAsync(43193632);
/// await using var conn = await session.OpenConnectionAsync();
/// // Query database at block 43193632 state
/// </code>
/// </summary>
public class TestEnvironmentClient : IAsyncDisposable
{
    private static readonly string DefaultTestEnvUrl =
        Environment.GetEnvironmentVariable("TEST_ENV_URL") ?? "http://localhost:5200";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private SessionResponse? _session;
    private bool _disposed;

    /// <summary>
    /// Session ID for this client instance.
    /// </summary>
    public string? SessionId => _session?.SessionId;

    /// <summary>
    /// Block number this session is frozen at.
    /// </summary>
    public long BlockNumber => _session?.BlockNumber ?? 0;

    /// <summary>
    /// PostgreSQL connection string with block filter pre-configured.
    /// </summary>
    public string? PostgresConnectionString => _session?.Postgres?.ConnectionString;

    /// <summary>
    /// Anvil RPC URL for contract testing (if anvil feature was requested).
    /// Note: This returns the internal URL which is not accessible externally.
    /// Use ExecuteAnvilRpcAsync for proxied access.
    /// </summary>
    [Obsolete("Use ExecuteAnvilRpcAsync for external access. This URL is only accessible internally.")]
    public string? AnvilRpcUrl => _session?.Anvil?.RpcUrl;

    /// <summary>
    /// Circles RPC URL with session parameters (if rpc feature was requested).
    /// Note: This returns the internal URL which is not accessible externally.
    /// Use ExecuteCirclesRpcAsync for proxied access.
    /// </summary>
    [Obsolete("Use ExecuteCirclesRpcAsync for external access. This URL is only accessible internally.")]
    public string? CirclesRpcUrl => _session?.Rpc?.Url;

    /// <summary>
    /// Session expiration time.
    /// </summary>
    public DateTimeOffset? ExpiresAt => _session?.ExpiresAt;

    /// <summary>
    /// Full session response object.
    /// </summary>
    public SessionResponse? Session => _session;

    // Backoff schedule for transient saturation (429 rate limit, 503 session-cap).
    // The server enforces a global concurrent-session cap and a per-IP rate limit;
    // parallel test assemblies routinely trip both. Total wait ≈ 77s, comfortably
    // under the 5-minute session TTL so a saturated cap can drain.
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)
    ];

    private static bool IsTransientStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// POSTs JSON, retrying on 429/503 with the backoff schedule above.
    /// Throws HttpRequestException for any non-transient failure or once retries are exhausted.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithTransientRetryAsync<T>(string uri, T request, string label)
    {
        var attempt = 0;
        while (true)
        {
            var response = await _httpClient.PostAsJsonAsync(uri, request);
            if (response.IsSuccessStatusCode)
                return response;

            var error = await response.Content.ReadAsStringAsync();
            if (IsTransientStatus(response.StatusCode) && attempt < TransientRetryDelays.Length)
            {
                await Task.Delay(TransientRetryDelays[attempt]);
                attempt++;
                continue;
            }

            throw new HttpRequestException(
                $"{label} failed: {response.StatusCode} - {error}" +
                (attempt > 0 ? $" (after {attempt} retries)" : ""));
        }
    }

    private TestEnvironmentClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        // Must end with / for relative URLs to work correctly with BaseAddress
        // 5-minute timeout for heavy queries (trust graph = 2M rows via HTTP proxy)
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Creates a new test session at the specified block number.
    /// </summary>
    /// <param name="blockNumber">Block number to freeze the session at.</param>
    /// <param name="features">Features to enable: "db", "anvil", "rpc". Default: ["db"]</param>
    /// <param name="ttl">Session time-to-live. Default: "5m", Max: "10m" (server enforced).</param>
    /// <param name="testEnvUrl">Test environment URL. Default: TEST_ENV_URL env var or http://localhost:5200</param>
    /// <returns>A TestEnvironmentClient with an active session.</returns>
    /// <exception cref="HttpRequestException">If the session creation fails.</exception>
    public static async Task<TestEnvironmentClient> CreateSessionAsync(
        long blockNumber,
        string[]? features = null,
        string ttl = "5m",
        string? testEnvUrl = null)
    {
        var client = new TestEnvironmentClient(testEnvUrl ?? DefaultTestEnvUrl);

        var request = new SessionRequest
        {
            BlockNumber = blockNumber,
            Features = features ?? ["db"],
            Ttl = ttl
        };

        var response = await client.PostWithTransientRetryAsync(
            "api/v1/session", request, "Session creation");

        client._session = await response.Content.ReadFromJsonAsync<SessionResponse>();

        if (client._session == null)
        {
            throw new InvalidOperationException("Session response was null");
        }

        return client;
    }

    /// <summary>
    /// Gets the current (latest indexed) block number from the test environment.
    /// </summary>
    public static async Task<long> GetCurrentBlockAsync(string? testEnvUrl = null)
    {
        var baseUrl = (testEnvUrl ?? DefaultTestEnvUrl).TrimEnd('/') + "/";
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var response = await client.GetFromJsonAsync<BlockInfo>("api/v1/blocks/current");
        return response?.BlockNumber ?? 0;
    }

    /// <summary>
    /// Checks if a specific block exists in the test environment database.
    /// </summary>
    public static async Task<bool> BlockExistsAsync(long blockNumber, string? testEnvUrl = null)
    {
        var baseUrl = (testEnvUrl ?? DefaultTestEnvUrl).TrimEnd('/') + "/";
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var response = await client.GetFromJsonAsync<BlockExistsInfo>(
            $"api/v1/blocks/{blockNumber}/exists");
        return response?.Exists ?? false;
    }

    /// <summary>
    /// Gets the health status of the test environment.
    /// </summary>
    public static async Task<HealthResponse?> GetHealthAsync(string? testEnvUrl = null)
    {
        var baseUrl = (testEnvUrl ?? DefaultTestEnvUrl).TrimEnd('/') + "/";
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return await client.GetFromJsonAsync<HealthResponse>("health");
    }

    /// <summary>
    /// Opens a new PostgreSQL connection using the session's filtered connection string.
    /// The connection is pre-configured with circles.max_block_number set.
    /// </summary>
    /// <returns>An open NpgsqlConnection.</returns>
    /// <exception cref="InvalidOperationException">If no session exists or postgres feature wasn't requested.</exception>
    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        if (_session?.Postgres?.ConnectionString == null)
        {
            throw new InvalidOperationException(
                "No PostgreSQL connection available. Ensure session was created with 'db' feature.");
        }

        var conn = new NpgsqlConnection(_session.Postgres.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Creates an NpgsqlDataSource for connection pooling with the session's filtered connection.
    /// </summary>
    /// <returns>An NpgsqlDataSource configured with block filtering.</returns>
    public NpgsqlDataSource CreateDataSource()
    {
        if (_session?.Postgres?.ConnectionString == null)
        {
            throw new InvalidOperationException(
                "No PostgreSQL connection available. Ensure session was created with 'db' feature.");
        }

        return NpgsqlDataSource.Create(_session.Postgres.ConnectionString);
    }

    /// <summary>
    /// Refreshes the session status from the server.
    /// </summary>
    public async Task RefreshSessionAsync()
    {
        if (_session?.SessionId == null)
        {
            throw new InvalidOperationException("No active session");
        }

        var response = await _httpClient.GetAsync($"api/v1/session/{_session.SessionId}");
        response.EnsureSuccessStatusCode();

        _session = await response.Content.ReadFromJsonAsync<SessionResponse>();
    }

    /// <summary>
    /// Explicitly terminates the session. Called automatically by DisposeAsync.
    /// </summary>
    public async Task TerminateSessionAsync()
    {
        if (_session?.SessionId == null) return;

        try
        {
            await _httpClient.DeleteAsync($"api/v1/session/{_session.SessionId}");
        }
        catch
        {
            // Ignore cleanup errors - session may have already expired
        }

        _session = null;
    }

    /// <summary>
    /// Executes a SQL query through the test environment proxy.
    /// Use this when direct database connection is not available (remote access).
    /// </summary>
    /// <param name="sql">The SQL SELECT query to execute.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="maxRows">Maximum rows to return (default: 1000).</param>
    /// <returns>Query response with columns and rows.</returns>
    public async Task<QueryResponse> ExecuteQueryAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        int maxRows = 1000)
    {
        if (_session?.SessionId == null)
        {
            throw new InvalidOperationException("No active session");
        }

        var request = new QueryRequest
        {
            Sql = sql,
            Parameters = parameters,
            MaxRows = maxRows
        };

        var response = await PostWithTransientRetryAsync(
            $"api/v1/session/{_session.SessionId}/query", request, "Query");

        return await response.Content.ReadFromJsonAsync<QueryResponse>()
            ?? throw new InvalidOperationException("Query response was null");
    }

    /// <summary>
    /// Executes a scalar query through the test environment proxy.
    /// </summary>
    /// <param name="sql">The SQL query that returns a single value.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <returns>The scalar result.</returns>
    public async Task<object?> ExecuteScalarAsync(
        string sql,
        Dictionary<string, object?>? parameters = null)
    {
        if (_session?.SessionId == null)
        {
            throw new InvalidOperationException("No active session");
        }

        var request = new QueryRequest
        {
            Sql = sql,
            Parameters = parameters
        };

        var response = await PostWithTransientRetryAsync(
            $"api/v1/session/{_session.SessionId}/query/scalar", request, "Scalar query");

        var result = await response.Content.ReadFromJsonAsync<ScalarResponse>();
        return result?.Value;
    }

    /// <summary>
    /// Checks if direct database connection is available.
    /// When false, use ExecuteQueryAsync instead of OpenConnectionAsync.
    /// </summary>
    public bool IsDirectConnectionAvailable
    {
        get
        {
            // Must have a valid connection string
            if (string.IsNullOrEmpty(_session?.Postgres?.ConnectionString)) return false;
            if (string.IsNullOrEmpty(_session.Postgres.Host)) return false;

            // Check if we can reach the postgres host
            // Internal docker hostnames like "postgres-gnosis" are not reachable externally
            var host = _session.Postgres.Host;

            // If we're running locally, we can reach local postgres
            if (IsLocalhost()) return true;

            // External access: can't reach internal docker hostnames
            return !host.Contains("postgres-") && host != "localhost";
        }
    }

    private bool IsLocalhost()
    {
        return _baseUrl.Contains("localhost") || _baseUrl.Contains("127.0.0.1");
    }

    /// <summary>
    /// Executes a JSON-RPC call to the session's Anvil fork through the test environment proxy.
    /// This is the recommended way to interact with Anvil from external clients.
    /// </summary>
    /// <param name="method">The JSON-RPC method name (e.g., "eth_blockNumber", "eth_call").</param>
    /// <param name="parameters">The method parameters.</param>
    /// <returns>The JSON-RPC result.</returns>
    /// <exception cref="InvalidOperationException">If no session exists or anvil feature wasn't requested.</exception>
    /// <exception cref="HttpRequestException">If the request fails.</exception>
    public async Task<JsonElement> ExecuteAnvilRpcAsync(string method, params object[] parameters)
    {
        if (_session?.SessionId == null)
        {
            throw new InvalidOperationException("No active session");
        }

        if (_session.Anvil == null)
        {
            throw new InvalidOperationException(
                "Anvil not available. Ensure session was created with 'anvil' feature.");
        }

        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = 1
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"api/v1/session/{_session.SessionId}/anvil", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Anvil RPC failed: {response.StatusCode} - {error}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Check for JSON-RPC error
        if (json.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : "Unknown error";
            throw new JsonRpcException(errorMessage ?? "Unknown error", errorElement);
        }

        if (json.TryGetProperty("result", out var result))
        {
            return result;
        }

        throw new InvalidOperationException("Invalid JSON-RPC response: missing 'result' field");
    }

    /// <summary>
    /// Executes a JSON-RPC call to the Circles RPC service through the test environment proxy.
    /// The proxy passes X-Max-Block-Number header which filters database queries to the session's block.
    /// </summary>
    /// <param name="method">The Circles RPC method name (e.g., "circles_getAvatarInfo").</param>
    /// <param name="parameters">The method parameters.</param>
    /// <returns>The JSON-RPC result.</returns>
    /// <exception cref="InvalidOperationException">If no session exists.</exception>
    /// <exception cref="HttpRequestException">If the request fails.</exception>
    public async Task<JsonElement> ExecuteCirclesRpcAsync(string method, params object[] parameters)
    {
        if (_session?.SessionId == null)
        {
            throw new InvalidOperationException("No active session");
        }

        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = 1
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"api/v1/session/{_session.SessionId}/rpc", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Circles RPC failed: {response.StatusCode} - {error}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Check for JSON-RPC error
        if (json.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : "Unknown error";
            throw new JsonRpcException(errorMessage ?? "Unknown error", errorElement);
        }

        if (json.TryGetProperty("result", out var result))
        {
            return result;
        }

        throw new InvalidOperationException("Invalid JSON-RPC response: missing 'result' field");
    }

    /// <summary>
    /// Checks if the session has Anvil available.
    /// </summary>
    public bool HasAnvil => _session?.Anvil != null;

    /// <summary>
    /// Pre-funded test accounts from Anvil (if available).
    /// </summary>
    public string[] AnvilAccounts => _session?.Anvil?.Accounts ?? [];

    /// <summary>
    /// Chain ID of the Anvil fork.
    /// </summary>
    public long AnvilChainId => _session?.Anvil?.ChainId ?? 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await TerminateSessionAsync();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}

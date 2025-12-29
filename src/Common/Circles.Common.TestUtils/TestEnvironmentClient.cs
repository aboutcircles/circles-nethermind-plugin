using System.Net.Http.Json;
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
    /// </summary>
    public string? AnvilRpcUrl => _session?.Anvil?.RpcUrl;

    /// <summary>
    /// Circles RPC URL with session parameters (if rpc feature was requested).
    /// </summary>
    public string? CirclesRpcUrl => _session?.Rpc?.Url;

    /// <summary>
    /// Session expiration time.
    /// </summary>
    public DateTimeOffset? ExpiresAt => _session?.ExpiresAt;

    /// <summary>
    /// Full session response object.
    /// </summary>
    public SessionResponse? Session => _session;

    private TestEnvironmentClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        // Must end with / for relative URLs to work correctly with BaseAddress
        _httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl + "/") };
    }

    /// <summary>
    /// Creates a new test session at the specified block number.
    /// </summary>
    /// <param name="blockNumber">Block number to freeze the session at.</param>
    /// <param name="features">Features to enable: "db", "anvil", "rpc". Default: ["db"]</param>
    /// <param name="ttl">Session time-to-live. Default: "10m". Examples: "1h", "30m", "2h30m"</param>
    /// <param name="testEnvUrl">Test environment URL. Default: TEST_ENV_URL env var or http://localhost:5200</param>
    /// <returns>A TestEnvironmentClient with an active session.</returns>
    /// <exception cref="HttpRequestException">If the session creation fails.</exception>
    public static async Task<TestEnvironmentClient> CreateSessionAsync(
        long blockNumber,
        string[]? features = null,
        string ttl = "10m",
        string? testEnvUrl = null)
    {
        var client = new TestEnvironmentClient(testEnvUrl ?? DefaultTestEnvUrl);

        var request = new SessionRequest
        {
            BlockNumber = blockNumber,
            Features = features ?? ["db"],
            Ttl = ttl
        };

        var response = await client._httpClient.PostAsJsonAsync("api/v1/session", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Failed to create test session: {response.StatusCode} - {error}");
        }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await TerminateSessionAsync();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}

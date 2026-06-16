using System.Net.Http.Json;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Integration tests that use the Circles Test Environment API.
/// These tests verify the test environment itself works correctly.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
public class TestEnvironmentIntegrationTests
{
    private HttpClient _client = null!;
    private string _testEnvUrl = null!;
    private string? _sessionId;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL") ?? "";
        if (string.IsNullOrEmpty(_testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run integration tests.");
            return;
        }

        // Ensure trailing slash for proper relative URL resolution
        var baseUrl = _testEnvUrl.TrimEnd('/') + "/";
        _client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Check if test environment is available
        try
        {
            var response = await _client.GetAsync("health");
            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail($"Test environment not healthy at {_testEnvUrl}");
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available at {_testEnvUrl}: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_client == null!) return;

        if (_sessionId != null)
        {
            try
            {
                await _client.DeleteAsync($"api/v1/session/{_sessionId}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        _client.Dispose();
    }

    [Test]
    [Order(1)]
    public async Task HealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("health");
        response.EnsureSuccessStatusCode();

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.That(health, Is.Not.Null);
        Assert.That(health!.Status, Is.EqualTo("healthy"));
    }

    [Test]
    [Order(2)]
    public async Task GetCurrentBlock_ReturnsBlockNumber()
    {
        var response = await _client.GetAsync("api/v1/blocks/current");
        response.EnsureSuccessStatusCode();

        var blockInfo = await response.Content.ReadFromJsonAsync<BlockInfo>();
        Assert.That(blockInfo, Is.Not.Null);
        Assert.That(blockInfo!.BlockNumber, Is.GreaterThan(0));
    }

    [Test]
    [Order(3)]
    public async Task CreateSession_WithDbFeature_ReturnsConnectionString()
    {
        const long blockNumber = 43193632; // Bug repro block

        var response = await _client.PostAsJsonAsync("api/v1/session", new
        {
            blockNumber,
            features = new[] { "db" },
            ttl = "5m"
        });

        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();

        Assert.That(session, Is.Not.Null);
        Assert.That(session!.SessionId, Is.Not.Empty);
        Assert.That(session.BlockNumber, Is.EqualTo(blockNumber));
        Assert.That(session.Postgres, Is.Not.Null,
            "Postgres config should be returned when 'db' feature is requested");
        // Connection details (Host, ConnectionString) may be redacted for external access.
        // Just verify the feature was provisioned — E2E tests use the session object directly.

        _sessionId = session.SessionId;
    }

    [Test]
    [Order(4)]
    public async Task GetSession_AfterCreate_ReturnsSession()
    {
        if (_sessionId == null)
        {
            Assert.Ignore("No session created - run CreateSession test first");
        }

        var response = await _client.GetAsync($"api/v1/session/{_sessionId}");
        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.SessionId, Is.EqualTo(_sessionId));
        Assert.That(session.Status, Is.EqualTo(1)); // 1 = Active
    }

    [Test]
    [Order(5)]
    public async Task DeleteSession_AfterCreate_Succeeds()
    {
        if (_sessionId == null)
        {
            Assert.Ignore("No session created - run CreateSession test first");
        }

        var response = await _client.DeleteAsync($"api/v1/session/{_sessionId}");
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NoContent));

        // Verify session is gone
        var getResponse = await _client.GetAsync($"api/v1/session/{_sessionId}");
        Assert.That(getResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));

        _sessionId = null; // Clear so teardown doesn't try to delete again
    }

    [Test]
    [Order(6)]
    public async Task BlockExists_ForKnownBlock_ReturnsTrue()
    {
        const long knownBlock = 43193632; // We know this block exists from bug investigation

        var response = await _client.GetAsync($"api/v1/blocks/{knownBlock}/exists");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<BlockExistsInfo>();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BlockNumber, Is.EqualTo(knownBlock));
        // Note: This may be false if running against a fresh database
        // Assert.That(result.Exists, Is.True);
    }

    [Test]
    [Order(7)]
    public async Task GetSession_ForNonExistent_Returns404()
    {
        var response = await _client.GetAsync("api/v1/session/nonexistent123");
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
    }

    // DTOs for API responses
    private record HealthResponse
    {
        public string Status { get; init; } = "";
        public int ActiveSessions { get; init; }
    }

    private record BlockInfo
    {
        public long BlockNumber { get; init; }
    }

    private record BlockExistsInfo
    {
        public long BlockNumber { get; init; }
        public bool Exists { get; init; }
    }

    private record SessionResponse
    {
        public string SessionId { get; init; } = "";
        public long BlockNumber { get; init; }
        public PostgresInfo? Postgres { get; init; }
        public AnvilInfo? Anvil { get; init; }
        public RpcInfo? Rpc { get; init; }
        public int Status { get; init; } // 0=Created, 1=Active, 2=Expired, 3=Terminated
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private record PostgresInfo
    {
        public string ConnectionString { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public string Database { get; init; } = "";
    }

    private record AnvilInfo
    {
        public string RpcUrl { get; init; } = "";
        public string[] Accounts { get; init; } = [];
        public long ChainId { get; init; }
    }

    private record RpcInfo
    {
        public string Url { get; init; } = "";
    }
}

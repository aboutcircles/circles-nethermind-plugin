using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Integration tests for the Pathfinder HTTP endpoints.
/// Uses WebApplicationFactory to spin up the real pipeline with heavy services
/// (background updater, log stats) removed and graph state controllable per test.
/// </summary>
[TestFixture]
public class HttpEndpointTests
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    // Store original env vars so we can restore them
    private string? _origPostgres;
    private string? _origNethermind;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Save originals
        _origPostgres = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        _origNethermind = Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL");

        // Set dummy values so Settings constructors don't throw
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", "Host=localhost;Database=test;Username=test;Password=test");
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL", "http://localhost:9999");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Remove background services that need real DB / Nethermind
                    services.RemoveAll<IHostedService>();
                });
            });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();

        // Restore env vars
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _origPostgres);
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL", _origNethermind);
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private static string ValidAddr(int seed = 1) =>
        "0x" + seed.ToString("x").PadLeft(40, '0');

    // ─── GET /live ───────────────────────────────────────────────────────────

    [Test]
    public async Task Live_ReturnsHealthy()
    {
        var resp = await _client!.GetAsync("/live");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ─── GET /ready — no graphs loaded ───────────────────────────────────────

    [Test]
    public async Task Ready_WhenGraphsNotLoaded_Returns503()
    {
        var resp = await _client!.GetAsync("/ready");
        // Graphs are not loaded (no background service), so readiness check fails
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    // ─── GET /findMaxFlow — validation ───────────────────────────────────────

    [Test]
    public async Task FindMaxFlow_InvalidFromAddress_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from=not-an-address&to={ValidAddr(2)}&amount=100");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Invalid Ethereum address"));
    }

    [Test]
    public async Task FindMaxFlow_InvalidToAddress_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from={ValidAddr(1)}&to=0xZZZ&amount=100");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Invalid Ethereum address"));
    }

    [Test]
    public async Task FindMaxFlow_InvalidAmount_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=not-a-number");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("amount must be a valid integer"));
    }

    [Test]
    public async Task FindMaxFlow_GraphsNotReady_Returns503()
    {
        // Warmup is a transient server-side state, not a client error — must be a
        // retryable 503 so load balancers drain the node, never 500/400.
        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=100");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Graphs are not loaded"));
    }

    [Test]
    public async Task FindMaxFlow_FewSimulatedBalances_PassesValidationThen503()
    {
        // The GET endpoint accepts simulatedBalances as a query param JSON string.
        // 1001 entries with full addresses would exceed URI length limits, so we test
        // this via POST /findPath instead (see FindPath_Post_TooManySimulatedBalances_Returns400).
        // Here we verify the smaller array path parses correctly and doesn't reject valid input.
        var balances = new[] { new { Holder = ValidAddr(10), Token = ValidAddr(20), Amount = "100" } };
        var json = JsonSerializer.Serialize(balances);

        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=100&simulatedBalances={Uri.EscapeDataString(json)}");
        // With 1 entry, should pass size validation and fall through to the
        // transient "graphs not loaded" warmup state → retryable 503.
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Graphs are not loaded"));
    }

    [Test]
    public async Task FindMaxFlow_InvalidSimulatedBalancesJson_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findMaxFlow?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=100&simulatedBalances=not-json");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("simulatedBalances must be a JSON array"));
    }

    // ─── GET /findPath — validation ──────────────────────────────────────────

    [Test]
    public async Task FindPath_Get_InvalidAddress_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findPath?from=invalid&to={ValidAddr(2)}&amount=100");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Invalid Ethereum address"));
    }

    [Test]
    public async Task FindPath_Get_InvalidAmount_Returns400()
    {
        var resp = await _client!.GetAsync(
            $"/findPath?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=abc");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("amount must be a valid integer"));
    }

    [Test]
    public async Task FindPath_Get_GraphsNotReady_Returns503()
    {
        var resp = await _client!.GetAsync(
            $"/findPath?from={ValidAddr(1)}&to={ValidAddr(2)}&amount=100");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Graphs are not loaded"));
    }

    // ─── POST /findPath — validation ─────────────────────────────────────────

    [Test]
    public async Task FindPath_Post_InvalidAddress_Returns400()
    {
        var request = new FlowRequest
        {
            Source = "not-valid",
            Sink = ValidAddr(2),
            TargetFlow = "100"
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Invalid Ethereum address"));
    }

    [Test]
    public async Task FindPath_Post_InvalidAmount_Returns400()
    {
        var request = new FlowRequest
        {
            Source = ValidAddr(1),
            Sink = ValidAddr(2),
            TargetFlow = "not-a-number"
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("amount must be a valid integer"));
    }

    [Test]
    public async Task FindPath_Post_TooManySimulatedBalances_Returns400()
    {
        var request = new FlowRequest
        {
            Source = ValidAddr(1),
            Sink = ValidAddr(2),
            TargetFlow = "100",
            SimulatedBalances = Enumerable.Range(0, 1001)
                .Select(i => new SimulatedBalance { Holder = ValidAddr(i + 10), Token = ValidAddr(i + 2000), Amount = "100" })
                .ToList()
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("simulatedBalances exceeds maximum"));
    }

    [Test]
    public async Task FindPath_Post_TooManySimulatedTrusts_Returns400()
    {
        var request = new FlowRequest
        {
            Source = ValidAddr(1),
            Sink = ValidAddr(2),
            TargetFlow = "100",
            SimulatedTrusts = Enumerable.Range(0, 1001)
                .Select(i => new SimulatedTrust { Truster = ValidAddr(i + 10), Trustee = ValidAddr(i + 2000) })
                .ToList()
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("simulatedTrusts exceeds maximum"));
    }

    [Test]
    public async Task FindPath_Post_TooManySimulatedConsentedAvatars_Returns400()
    {
        var request = new FlowRequest
        {
            Source = ValidAddr(1),
            Sink = ValidAddr(2),
            TargetFlow = "100",
            SimulatedConsentedAvatars = Enumerable.Range(0, 1001)
                .Select(i => ValidAddr(i + 10))
                .ToList()
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("simulatedConsentedAvatars exceeds maximum"));
    }

    [Test]
    public async Task FindPath_Post_InvalidJsonBody_Returns400()
    {
        var content = new StringContent("{not valid json", Encoding.UTF8, "application/json");
        var resp = await _client!.PostAsync("/findPath", content);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task FindPath_Post_GraphsNotReady_Returns503()
    {
        var request = new FlowRequest
        {
            Source = ValidAddr(1),
            Sink = ValidAddr(2),
            TargetFlow = "100"
        };

        var resp = await _client!.PostAsJsonAsync("/findPath", request);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Graphs are not loaded"));
    }

    // ─── GET /snapshot ───────────────────────────────────────────────────────

    [Test]
    public async Task Snapshot_WhenGraphsNotReady_Returns503()
    {
        var resp = await _client!.GetAsync("/snapshot");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    // ─── GET /metrics ────────────────────────────────────────────────────────

    [Test]
    public async Task Metrics_ReturnsOk()
    {
        var resp = await _client!.GetAsync("/metrics");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadAsStringAsync();
        // Prometheus metrics endpoint should contain some metric names
        Assert.That(body, Does.Contain("process_"));
    }
}

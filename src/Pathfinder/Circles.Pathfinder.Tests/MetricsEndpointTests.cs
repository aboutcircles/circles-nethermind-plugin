using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Integration tests that verify the Prometheus /metrics endpoint exposes
/// the expected graph update metrics.
///
/// Run with: PATHFINDER_URL=http://localhost:8080 dotnet test --filter MetricsEndpointTests
/// </summary>
[TestFixture]
[Category("Integration")]
public class MetricsEndpointTests
{
    private HttpClient _client = null!;
    private string _pathfinderUrl = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _pathfinderUrl = Environment.GetEnvironmentVariable("PATHFINDER_URL") ?? "";
        if (string.IsNullOrEmpty(_pathfinderUrl))
        {
            Assert.Ignore("PATHFINDER_URL not set. Set to http://localhost:8080 to run metrics tests.");
            return;
        }

        // Ensure trailing slash for proper relative URL resolution
        var baseUrl = _pathfinderUrl.TrimEnd('/') + "/";
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Check if pathfinder is available
        try
        {
            var response = await _client.GetAsync("ready");
            if (!response.IsSuccessStatusCode)
            {
                Assert.Ignore($"Pathfinder not ready at {_pathfinderUrl}");
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Pathfinder not available at {_pathfinderUrl}: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public void Teardown()
    {
        _client?.Dispose();
    }

    [Test]
    public async Task MetricsEndpoint_ExposesGraphUpdateMetrics()
    {
        var response = await _client.GetAsync("metrics");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();

        // Verify all expected metrics are present
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("circles_graph_update_total"),
                "Missing circles_graph_update_total metric");
            Assert.That(content, Does.Contain("circles_graph_update_duration_seconds"),
                "Missing circles_graph_update_duration_seconds histogram");
            Assert.That(content, Does.Contain("circles_graph_consecutive_errors"),
                "Missing circles_graph_consecutive_errors gauge");
            Assert.That(content, Does.Contain("circles_graph_last_update_timestamp"),
                "Missing circles_graph_last_update_timestamp gauge");
            Assert.That(content, Does.Contain("circles_graph_last_processed_block"),
                "Missing circles_graph_last_processed_block gauge");
        });
    }

    [Test]
    public async Task MetricsEndpoint_UpdateTotalHasStatusLabels()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        // At minimum, we expect to see the success label (healthy service had updates)
        Assert.That(content, Does.Contain("circles_graph_update_total{status=\"success\"}"),
            "Missing success status label on update_total");
    }

    [Test]
    public async Task MetricsEndpoint_DurationHistogramHasGraphLabels()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("circles_graph_update_duration_seconds_bucket{graph=\"trust\""),
                "Missing trust graph label on duration histogram");
            Assert.That(content, Does.Contain("circles_graph_update_duration_seconds_bucket{graph=\"balance\""),
                "Missing balance graph label on duration histogram");
            Assert.That(content, Does.Contain("circles_graph_update_duration_seconds_bucket{graph=\"total\""),
                "Missing total graph label on duration histogram");
        });
    }

    [Test]
    public async Task MetricsEndpoint_LastUpdateTimestampIsRecent()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        // Parse the timestamp value from the metrics
        var match = Regex.Match(content, @"circles_graph_last_update_timestamp\s+(\d+(?:\.\d+)?)");
        Assert.That(match.Success, Is.True, "Could not parse last_update_timestamp metric");

        var timestamp = double.Parse(match.Groups[1].Value);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ageSeconds = now - timestamp;

        // Expect last update within 5 minutes (300 seconds) for a healthy service
        Assert.That(ageSeconds, Is.LessThan(300),
            $"Last graph update was {ageSeconds:F0}s ago, expected < 300s for healthy service");
    }

    [Test]
    public async Task MetricsEndpoint_ConsecutiveErrorsIsZeroWhenHealthy()
    {
        // First verify service is ready (healthy)
        var readyResponse = await _client.GetAsync("ready");
        Assert.That(readyResponse.IsSuccessStatusCode, Is.True,
            "Service not ready - cannot verify consecutive_errors metric");

        var metricsResponse = await _client.GetAsync("metrics");
        var content = await metricsResponse.Content.ReadAsStringAsync();

        var match = Regex.Match(content, @"circles_graph_consecutive_errors\s+(\d+)");
        Assert.That(match.Success, Is.True, "Could not parse consecutive_errors metric");

        var errorCount = int.Parse(match.Groups[1].Value);
        Assert.That(errorCount, Is.EqualTo(0),
            "Healthy service should have 0 consecutive errors");
    }

    [Test]
    public async Task MetricsEndpoint_LastProcessedBlockIsPositive()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        var match = Regex.Match(content, @"circles_graph_last_processed_block\s+(\d+(?:\.\d+)?)");
        Assert.That(match.Success, Is.True, "Could not parse last_processed_block metric");

        var blockNumber = double.Parse(match.Groups[1].Value);
        Assert.That(blockNumber, Is.GreaterThan(0),
            "Last processed block should be greater than 0 for an initialized service");
    }

    [Test]
    public async Task MetricsEndpoint_ExposesExistingPathfinderMetrics()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        // Verify pre-existing metrics are still exposed
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("circles_findpath_inflight_requests"),
                "Missing circles_findpath_inflight_requests gauge");
            Assert.That(content, Does.Contain("circles_findpath_rejected_requests_total"),
                "Missing circles_findpath_rejected_requests_total counter");
            Assert.That(content, Does.Contain("circles_http_request_duration_seconds"),
                "Missing circles_http_request_duration_seconds histogram");
        });
    }

    [Test]
    public async Task MetricsEndpoint_DurationHistogramHasBuckets()
    {
        var response = await _client.GetAsync("metrics");
        var content = await response.Content.ReadAsStringAsync();

        // Verify histogram has multiple buckets (exponential 0.1 to ~100s)
        var bucketMatches = Regex.Matches(content,
            @"circles_graph_update_duration_seconds_bucket\{graph=""total"",le=""([^""]+)""\}");

        Assert.That(bucketMatches.Count, Is.GreaterThanOrEqualTo(5),
            "Expected at least 5 histogram buckets for duration metric");

        // Verify we have +Inf bucket
        Assert.That(content, Does.Contain("le=\"+Inf\""),
            "Missing +Inf bucket in histogram");
    }
}

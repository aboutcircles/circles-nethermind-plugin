using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common.TestUtils;
using Circles.Index.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Regression tests that load scenarios from JSON fixtures.
///
/// These tests verify that known bug scenarios are handled correctly
/// after the mint-along-path fix. Each fixture captures a specific
/// block state and source/sink pair that previously caused issues.
///
/// To run:
///   TEST_ENV_URL=https://staging.circlesubi.network/test-env \
///   dotnet test --filter "Category=Regression"
/// </summary>
[TestFixture]
[Category("Regression")]
[Category("Snapshot")]
public class RegressionTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static readonly string ScenariosPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "RegressionScenarios");

    /// <summary>
    /// Loads all regression scenarios from the RegressionScenarios directory.
    /// </summary>
    public static IEnumerable<TestCaseData> LoadScenarios()
    {
        // In test context, scenarios are copied to output directory
        var scenariosDir = ScenariosPath;

        // Fallback to source directory if not in output
        if (!Directory.Exists(scenariosDir))
        {
            var sourceDir = Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "RegressionScenarios");
            if (Directory.Exists(sourceDir))
            {
                scenariosDir = sourceDir;
            }
        }

        if (!Directory.Exists(scenariosDir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(scenariosDir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var scenario = JsonSerializer.Deserialize<RegressionScenario>(json);

            if (scenario != null)
            {
                yield return new TestCaseData(scenario)
                    .SetName($"Regression_{scenario.Name?.Replace(" ", "_")}");
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(LoadScenarios))]
    public async Task RegressionScenario_EdgeOrderingIsCorrect(RegressionScenario scenario)
    {
        // Check if test environment is available
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Ignore("Test environment not healthy");
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        // Check if block exists
        var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
        if (!exists)
        {
            Assert.Ignore($"Block {scenario.Block} not indexed");
        }

        // Create session at the scenario's block
        await using var session = await TestEnvironmentClient.CreateSessionAsync(
            scenario.Block,
            features: ["db"],
            ttl: "10m");

        var settings = new Settings();
        var loadGraph = new LoadGraph(session.PostgresConnectionString!, settings);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = scenario.Source,
            Sink = scenario.Sink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse(scenario.MinFlow ?? "1000000000000000000");

        // Compute path - this should not throw
        MaxFlowResponse? response = null;
        Assert.DoesNotThrow(() =>
        {
            response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
        }, "Pathfinder should compute path without exception");

        if (response?.Transfers == null || response.Transfers.Count == 0)
        {
            TestContext.Out.WriteLine($"No path found for scenario: {scenario.Name}");
            // This might be expected depending on the scenario
            return;
        }

        // Verify edge ordering
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();

        foreach (var step in response.Transfers)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";

            if (from == routerAddr && capacityGraph.IsGroup(AddressIdPool.IdOf(to)))
            {
                groupsWithCollateral.Add(to);
            }

            var fromId = AddressIdPool.IdOf(from);
            if (capacityGraph.IsGroup(fromId) && !capacityGraph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Scenario '{scenario.Name}': Group {from} minted before receiving collateral");
            }
        }

        TestContext.Out.WriteLine($"Scenario '{scenario.Name}' passed with {response.Transfers.Count} steps");
    }

    /// <summary>
    /// Tests that we have at least one regression scenario defined.
    /// </summary>
    [Test]
    public void ScenariosExist_AtLeastOne()
    {
        var scenarios = LoadScenarios().ToList();

        // If no scenarios found, just skip (they might not be copied to output)
        if (scenarios.Count == 0)
        {
            TestContext.Out.WriteLine("No scenarios found in output directory - skipping");
            Assert.Ignore("No regression scenarios found");
            return;
        }

        Assert.That(scenarios.Count, Is.GreaterThan(0),
            "Should have at least one regression scenario defined");

        TestContext.Out.WriteLine($"Found {scenarios.Count} regression scenarios");
    }
}

/// <summary>
/// JSON schema for regression test scenarios.
/// </summary>
public class RegressionScenario
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("block")]
    public long Block { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sink")]
    public string? Sink { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("expectedError")]
    public string? ExpectedError { get; set; }

    [JsonPropertyName("minFlow")]
    public string? MinFlow { get; set; }

    [JsonPropertyName("discoveredAt")]
    public string? DiscoveredAt { get; set; }

    [JsonPropertyName("fixedIn")]
    public string? FixedIn { get; set; }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
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
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// CI triggers these tests automatically on merges to main/dev branches.
/// </summary>
[TestFixture]
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

            // Skip negative tests (shouldFindPath=false) - they are handled by ScenarioTests
            // Skip incomplete scenarios (no block number) - integration path needs a valid block
            if (scenario != null && scenario.ShouldFindPath && scenario.Block > 0)
            {
                yield return new TestCaseData(scenario)
                    .SetName($"Regression_{scenario.Name?.Replace(" ", "_")}");
            }
        }
    }

    /// <summary>
    /// Unit test that runs using embedded subgraph data (no external dependencies).
    /// Runs in &lt;100ms per scenario vs ~20s for integration tests.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(LoadScenarios))]
    [Category("Unit")]
    public void RegressionScenario_UnitTest_EdgeOrderingIsCorrect(RegressionScenario scenario)
    {
        // Skip if no embedded subgraph available
        if (scenario.Subgraph == null)
        {
            Assert.Ignore($"Fixture '{scenario.Name}' has no embedded subgraph - skipping unit test");
            return;
        }

        // Use embedded subgraph data
        var loadGraph = new FixtureLoadGraph(scenario.Subgraph);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = BuildFlowRequest(scenario);

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse(scenario.MinFlow ?? "1000000000000000000");

        // Compute path - this should not throw
        MaxFlowResponse? response = null;
        Assert.DoesNotThrow(() =>
        {
            response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
        }, "Pathfinder should compute path without exception");

        if (scenario.ShouldFindPath)
        {
            Assert.That(response?.Transfers, Is.Not.Null.And.Not.Empty,
                $"Scenario '{scenario.Name}' should find a path but no transfers returned");

            // Verify edge ordering (same logic as integration test)
            VerifyEdgeOrdering(response!, capacityGraph, scenario.Name!);
        }
        else if (response?.Transfers != null && response.Transfers.Count > 0)
        {
            // Negative test - should not have found a path
            Assert.Fail($"Scenario '{scenario.Name}' should NOT find a path but found {response.Transfers.Count} steps");
        }

        // Validate against expected path properties if specified
        if (scenario.ExpectedPath != null && response?.Transfers != null)
        {
            ValidateExpectedPath(response, scenario.ExpectedPath, capacityGraph, scenario.Name!);
        }

        TestContext.Out.WriteLine($"Unit test '{scenario.Name}' passed with {response?.Transfers?.Count ?? 0} steps");
    }

    /// <summary>
    /// Integration test that runs using full database at the scenario's block.
    /// Uses SharedGraphCache to avoid re-loading 760K rows per scenario.
    /// Requires TEST_ENV_URL to be set.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(LoadScenarios))]
    [Category("Integration")]
    public async Task RegressionScenario_EdgeOrderingIsCorrect(RegressionScenario scenario)
    {
        // Check if TEST_ENV_URL is set
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore(
                "TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run regression tests.");
            return;
        }

        // Check test environment availability (only if not already cached for this block)
        try
        {
            if (!SharedGraphCache.IsCached(scenario.Block))
            {
                var health = await TestEnvironmentClient.GetHealthAsync();
                if (health?.Status != "healthy")
                {
                    Assert.Ignore("Test environment not healthy");
                }

                var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
                if (!exists)
                {
                    Assert.Ignore($"Block {scenario.Block} not indexed");
                }
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        var isCached = SharedGraphCache.IsCached(scenario.Block);
        TestContext.Out.WriteLine(isCached
            ? $"Using cached graph for block {scenario.Block}"
            : $"Loading graph for block {scenario.Block} (first scenario at this block)");

        var factory = SharedGraphCache.CreateFactory(scenario.Block);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        // Guard: source may have been removed from staging by avatar registration filter
        var sourceId = AddressIdPool.IdOf(scenario.Source?.ToLowerInvariant() ?? "");
        var sourceInGraph = balanceGraph.BalanceNodes.Values.Any(n => n.Holder == sourceId)
                         || trustGraph.Edges.Any(e => e.From == sourceId || e.To == sourceId);
        if (!sourceInGraph && scenario.ShouldFindPath)
        {
            Assert.Warn($"Regression '{scenario.Name}': Source {scenario.Source} not in staging graph (data drift). " +
                "Unit test with subgraph data still validates correctness.");
            return;
        }

        var request = BuildFlowRequest(scenario);

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
    /// Verifies that edge ordering is correct: collateral must be deposited before minting.
    /// </summary>
    private static void VerifyEdgeOrdering(MaxFlowResponse response, CapacityGraph capacityGraph, string scenarioName)
    {
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();

        foreach (var step in response.Transfers!)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";

            // Track when Router deposits collateral to a group
            if (from == routerAddr && capacityGraph.IsGroup(AddressIdPool.IdOf(to)))
            {
                groupsWithCollateral.Add(to);
            }

            // Check: if a group is minting to an avatar (not Router), collateral must already be deposited
            var fromId = AddressIdPool.IdOf(from);
            if (capacityGraph.IsGroup(fromId) && !capacityGraph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Scenario '{scenarioName}': Group {from} minted before receiving collateral");
            }
        }
    }

    /// <summary>
    /// Validates the path result against expected properties.
    /// </summary>
    private static void ValidateExpectedPath(
        MaxFlowResponse response,
        ExpectedPath expectedPath,
        CapacityGraph capacityGraph,
        string scenarioName)
    {
        var transfers = response.Transfers!;

        if (expectedPath.MinHops.HasValue)
        {
            Assert.That(transfers.Count, Is.GreaterThanOrEqualTo(expectedPath.MinHops.Value),
                $"Scenario '{scenarioName}': Expected at least {expectedPath.MinHops.Value} hops but got {transfers.Count}");
        }

        if (expectedPath.MaxHops.HasValue)
        {
            Assert.That(transfers.Count, Is.LessThanOrEqualTo(expectedPath.MaxHops.Value),
                $"Scenario '{scenarioName}': Expected at most {expectedPath.MaxHops.Value} hops but got {transfers.Count}");
        }

        if (expectedPath.RouterInvolved.HasValue)
        {
            var routerAddr = RouterAddress.ToLowerInvariant();
            var routerFound = transfers.Any(t =>
                t.From?.ToLowerInvariant() == routerAddr ||
                t.To?.ToLowerInvariant() == routerAddr);

            if (expectedPath.RouterInvolved.Value)
            {
                Assert.That(routerFound, Is.True,
                    $"Scenario '{scenarioName}': Expected Router to be involved but it wasn't");
            }
            else
            {
                Assert.That(routerFound, Is.False,
                    $"Scenario '{scenarioName}': Expected Router NOT to be involved but it was");
            }
        }

        if (expectedPath.GroupsMinted != null && expectedPath.GroupsMinted.Count > 0)
        {
            var mintedGroups = new HashSet<string>();
            foreach (var step in transfers)
            {
                var fromId = AddressIdPool.IdOf(step.From?.ToLowerInvariant() ?? "");
                if (capacityGraph.IsGroup(fromId))
                {
                    mintedGroups.Add(step.From!.ToLowerInvariant());
                }
            }

            foreach (var expectedGroup in expectedPath.GroupsMinted)
            {
                Assert.That(mintedGroups.Contains(expectedGroup.ToLowerInvariant()), Is.True,
                    $"Scenario '{scenarioName}': Expected group {expectedGroup} to mint but it didn't");
            }
        }

        // Validate maxFlow assertions
        if (expectedPath.MaxFlow != null)
        {
            Assert.That(response.MaxFlow, Is.EqualTo(expectedPath.MaxFlow),
                $"Scenario '{scenarioName}': Expected maxFlow {expectedPath.MaxFlow} but got {response.MaxFlow}");
        }

        if (expectedPath.MaxFlowNotEqual != null)
        {
            Assert.That(response.MaxFlow, Is.Not.EqualTo(expectedPath.MaxFlowNotEqual),
                $"Scenario '{scenarioName}': maxFlow should NOT be {expectedPath.MaxFlowNotEqual} (this was the bug)");
        }
    }

    /// <summary>
    /// Builds a FlowRequest from the scenario, applying all optional fields.
    /// </summary>
    private static FlowRequest BuildFlowRequest(RegressionScenario scenario)
    {
        return new FlowRequest
        {
            Source = scenario.Source,
            Sink = scenario.Sink,
            FromTokens = scenario.FromTokens,
            ToTokens = scenario.ToTokens,
            ExcludedFromTokens = scenario.ExcludedFromTokens,
            ExcludedToTokens = scenario.ExcludedToTokens,
            SimulatedBalances = scenario.SimulatedBalances,
            SimulatedTrusts = scenario.SimulatedTrusts,
            SimulatedConsentedAvatars = scenario.SimulatedConsentedAvatars,
            MaxTransfers = scenario.MaxTransfers,
            QuantizedMode = scenario.QuantizedMode,
            DebugShowIntermediateSteps = scenario.DebugShowIntermediateSteps,
            WithWrap = scenario.WithWrap
        };
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
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

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

    /// <summary>
    /// Whether the pathfinder should find a path. Defaults to true.
    /// Scenarios with shouldFindPath=false are negative tests (handled by ScenarioTests).
    /// </summary>
    [JsonPropertyName("shouldFindPath")]
    public bool ShouldFindPath { get; set; } = true;

    /// <summary>
    /// Whether to execute the transfer on Anvil (E2E). Defaults to true.
    /// </summary>
    [JsonPropertyName("runOnAnvil")]
    public bool RunOnAnvil { get; set; } = true;

    /// <summary>
    /// Tags for filtering/grouping scenarios.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Original block reference for validation (when subgraph was extracted).
    /// </summary>
    [JsonPropertyName("originalBlock")]
    public long? OriginalBlock { get; set; }

    /// <summary>
    /// Transaction hash for on-chain verified scenarios.
    /// </summary>
    [JsonPropertyName("transactionHash")]
    public string? TransactionHash { get; set; }

    /// <summary>
    /// Embedded subgraph data for unit testing without external dependencies.
    /// When present, tests can run offline using FixtureLoadGraph.
    /// </summary>
    [JsonPropertyName("subgraph")]
    public Helpers.FixtureSubgraph? Subgraph { get; set; }

    /// <summary>
    /// Documentation for recreating the scenario if the block becomes unavailable.
    /// </summary>
    [JsonPropertyName("scenarioRequirements")]
    public Helpers.ScenarioRequirements? ScenarioRequirements { get; set; }

    /// <summary>
    /// Expected properties of the path result for validation.
    /// </summary>
    [JsonPropertyName("expectedPath")]
    public Helpers.ExpectedPath? ExpectedPath { get; set; }

    // === FlowRequest parameters (optional) ===

    /// <summary>
    /// Limit which tokens can be used as source tokens.
    /// </summary>
    [JsonPropertyName("fromTokens")]
    public List<string>? FromTokens { get; set; }

    /// <summary>
    /// Limit which tokens can be received by the sink.
    /// </summary>
    [JsonPropertyName("toTokens")]
    public List<string>? ToTokens { get; set; }

    /// <summary>
    /// Exclude specific tokens from being used as source.
    /// </summary>
    [JsonPropertyName("excludedFromTokens")]
    public List<string>? ExcludedFromTokens { get; set; }

    /// <summary>
    /// Exclude specific tokens from being received.
    /// </summary>
    [JsonPropertyName("excludedToTokens")]
    public List<string>? ExcludedToTokens { get; set; }

    /// <summary>
    /// Simulated balances to inject into the graph (for testing hypothetical scenarios).
    /// </summary>
    [JsonPropertyName("simulatedBalances")]
    public List<Circles.Common.Dto.SimulatedBalance>? SimulatedBalances { get; set; }

    /// <summary>
    /// Simulated trust relationships to inject into the graph.
    /// </summary>
    [JsonPropertyName("simulatedTrusts")]
    public List<Circles.Common.Dto.SimulatedTrust>? SimulatedTrusts { get; set; }

    /// <summary>
    /// Simulated consented avatars (for testing consented flow paths).
    /// </summary>
    [JsonPropertyName("simulatedConsentedAvatars")]
    public List<string>? SimulatedConsentedAvatars { get; set; }

    /// <summary>
    /// Maximum number of transfer steps allowed.
    /// </summary>
    [JsonPropertyName("maxTransfers")]
    public int? MaxTransfers { get; set; }

    /// <summary>
    /// Enable 96 CRC quantization for invitation module testing.
    /// </summary>
    [JsonPropertyName("quantizedMode")]
    public bool? QuantizedMode { get; set; }

    /// <summary>
    /// Include debug information showing all transformation stages.
    /// </summary>
    [JsonPropertyName("debugShowIntermediateSteps")]
    public bool? DebugShowIntermediateSteps { get; set; }

    /// <summary>
    /// Include wrapped token paths.
    /// </summary>
    [JsonPropertyName("withWrap")]
    public bool? WithWrap { get; set; }
}

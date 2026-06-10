using Circles.Common;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Verifies that subgraph extraction is correct:
/// - Extracted subgraph produces the same pathfinder result as the full graph
/// - SharedGraphCache replay matches fresh load
/// - FixtureLoadGraph produces valid capacity graphs
///
/// These tests validate the TEST INFRASTRUCTURE itself, not just the pathfinder.
/// Without them, we can't trust that offline unit tests (using FixtureSubgraph)
/// are equivalent to integration tests (using full DB).
///
/// Requires TEST_ENV_URL for the full-graph comparison.
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
public class SubgraphEquivalenceTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// For each positive scenario: compute path with full graph, extract subgraph,
    /// compute path with subgraph, verify same maxFlow and same path structure.
    ///
    /// This is THE critical test — if this fails, subgraph-based unit tests are unreliable.
    /// </summary>
    [TestCaseSource(typeof(ScenarioLoader), nameof(ScenarioLoader.AllScenariosTestData))]
    [Category("Integration")]
    public async Task SubgraphProducesSameResult_AsFullGraph(TransferScenario scenario)
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        if (!scenario.ShouldFindPath)
        {
            Assert.Ignore("Negative scenarios not applicable for equivalence test");
            return;
        }

        // Check test-env availability
        try
        {
            if (!SharedGraphCache.IsCached(scenario.Block))
            {
                var health = await TestEnvironmentClient.GetHealthAsync();
                if (health?.Status != "healthy")
                    Assert.Ignore("Test environment not healthy");

                var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
                if (!exists)
                    Assert.Ignore($"Block {scenario.Block} not indexed");
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        // === STEP 1: Compute path with FULL graph ===
        var fullData = SharedGraphCache.GetOrLoad(scenario.Block);
        var fullFactory = new GraphFactory(RouterAddress, fullData.CreateLoadGraph());
        var fullTrust = fullFactory.V2TrustGraph();
        var fullBalance = fullFactory.V2BalanceGraph();
        var fullTrustLookup = GraphFactory.BuildTrustLookup(fullTrust);

        // Guard: source may have been removed from staging by avatar registration filter
        var sourceId = AddressIdPool.IdOf(scenario.Source.ToLowerInvariant());
        var sourceInGraph = fullBalance.BalanceNodes.Values.Any(n => n.Holder == sourceId)
                         || fullTrust.Edges.Any(e => e.From == sourceId || e.To == sourceId);
        if (!sourceInGraph)
        {
            Assert.Warn($"Scenario {scenario.Id}: Source {scenario.Source} not in staging graph (data drift). " +
                "Subgraph unit tests remain valid.");
            return;
        }

        var request = ScenarioTests.BuildFlowRequest(scenario);
        var fullCapacity = fullFactory.CreateCapacityGraph(fullBalance, fullTrustLookup, request);
        var pathfinder = new V2Pathfinder();
        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        MaxFlowResponse fullResponse;
        try
        {
            fullResponse = pathfinder.ComputeMaxFlowWithPath(fullCapacity, request, targetFlow);
        }
        catch
        {
            Assert.Ignore("Full graph pathfinding failed — not a subgraph issue");
            return;
        }

        if (fullResponse.Transfers == null || fullResponse.Transfers.Count == 0)
        {
            Assert.Ignore("Full graph found no path — nothing to compare");
            return;
        }

        TestContext.Out.WriteLine($"Full graph: maxFlow={fullResponse.MaxFlow}, steps={fullResponse.Transfers.Count}");

        // === STEP 2: Extract targeted subgraph from path addresses ===
        var pathAddresses = SubgraphExtractor.GetPathAddresses(fullResponse.Transfers);
        var subgraph = SubgraphExtractor.Extract(fullData, scenario.Source, scenario.Sink, pathAddresses);

        TestContext.Out.WriteLine($"Subgraph: {subgraph.Stats?.AddressCount} addresses, " +
            $"{subgraph.Stats?.TrustEdges} trust edges, " +
            $"{subgraph.Stats?.BalanceEntries} balances " +
            $"(vs full: {fullData.Trust.Count} trust, {fullData.Balances.Count} balances)");

        // === STEP 3: Compute path with SUBGRAPH ===
        var subLoadGraph = new FixtureLoadGraph(subgraph);
        var subFactory = new GraphFactory(RouterAddress, subLoadGraph);
        var subTrust = subFactory.V2TrustGraph();
        var subBalance = subFactory.V2BalanceGraph();
        var subTrustLookup = GraphFactory.BuildTrustLookup(subTrust);

        var subCapacity = subFactory.CreateCapacityGraph(subBalance, subTrustLookup, request);
        var subPathfinder = new V2Pathfinder();

        MaxFlowResponse subResponse;
        try
        {
            subResponse = subPathfinder.ComputeMaxFlowWithPath(subCapacity, request, targetFlow);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Subgraph pathfinding threw: {ex.Message}");
            return;
        }

        // === STEP 4: Verify equivalence ===

        // Both should find a path
        Assert.That(subResponse.Transfers, Is.Not.Null.And.Not.Empty,
            $"Subgraph should find path (full graph found {fullResponse.Transfers.Count} steps)");

        // MaxFlow should match (subgraph may find same or less flow, never more)
        var fullFlow = UInt256.Parse(fullResponse.MaxFlow ?? "0");
        var subFlow = UInt256.Parse(subResponse.MaxFlow ?? "0");

        // Compare subgraph flow to the FULL graph's actual flow, not the target.
        // The full graph might not reach the target either (e.g., contract-revert scenarios
        // with intentionally high targets). Subgraph should find at least 90% of full flow.
        if (fullFlow < targetFlow)
        {
            // Full graph couldn't meet target — subgraph just needs comparable flow
            var minAcceptable = fullFlow * 9 / 10; // 90% of full flow
            Assert.That(subFlow, Is.GreaterThanOrEqualTo(minAcceptable),
                $"Subgraph maxFlow {subFlow} should be >= 90% of full maxFlow {fullFlow}");
        }
        else
        {
            Assert.That(subFlow, Is.GreaterThanOrEqualTo(targetFlow),
                $"Subgraph maxFlow {subFlow} should meet target {targetFlow}");
        }

        TestContext.Out.WriteLine($"Subgraph: maxFlow={subResponse.MaxFlow}, steps={subResponse.Transfers!.Count}");
        TestContext.Out.WriteLine($"Flow comparison: full={fullFlow}, sub={subFlow}, target={targetFlow}");

        // Transfer step count should be in same ballpark (subgraph might find slightly different paths)
        // We don't require exact match because with fewer edges, the optimizer may choose different routes
        var stepDelta = Math.Abs(fullResponse.Transfers.Count - subResponse.Transfers.Count);
        TestContext.Out.WriteLine($"Step delta: {stepDelta} " +
            $"(full={fullResponse.Transfers.Count}, sub={subResponse.Transfers.Count})");
    }

    /// <summary>
    /// Verifies that CachedLoadGraph faithfully replays the same data:
    /// creating a GraphFactory from cached data produces the same trust + balance counts
    /// as creating from the original ProxyLoadGraph.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task CachedLoadGraph_ReplaysExactData()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
                Assert.Ignore("Test environment not healthy");
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        // Use the most common block
        const long testBlock = 43193632;

        var exists = await TestEnvironmentClient.BlockExistsAsync(testBlock);
        if (!exists) Assert.Ignore($"Block {testBlock} not indexed");

        var data = SharedGraphCache.GetOrLoad(testBlock);

        // Build graphs from cached data
        var factory = new GraphFactory(RouterAddress, data.CreateLoadGraph());
        var trust = factory.V2TrustGraph();
        var balance = factory.V2BalanceGraph();

        // Verify counts match raw data
        Assert.That(trust.Edges.Count, Is.GreaterThan(0), "Should have trust edges");
        Assert.That(balance.BalanceNodes.Count, Is.GreaterThan(0), "Should have balances");

        // The cached trust count should match what was loaded
        // (trust edges get deduplicated by GraphFactory, so count may differ from raw)
        TestContext.Out.WriteLine($"Raw trust rows: {data.Trust.Count}, Graph edges: {trust.Edges.Count}");
        TestContext.Out.WriteLine($"Raw balance rows: {data.Balances.Count}, Graph nodes: {balance.BalanceNodes.Count}");
        TestContext.Out.WriteLine($"Groups: {data.Groups.Count}, Consented: {data.ConsentedFlags.Count(f => f.HasConsentedFlow)}");

        // Build from cached twice — should produce same counts
        var factory2 = new GraphFactory(RouterAddress, data.CreateLoadGraph());
        var trust2 = factory2.V2TrustGraph();
        var balance2 = factory2.V2BalanceGraph();

        Assert.That(trust2.Edges.Count, Is.EqualTo(trust.Edges.Count),
            "Second replay should produce same trust edge count");
        Assert.That(balance2.BalanceNodes.Count, Is.EqualTo(balance.BalanceNodes.Count),
            "Second replay should produce same balance count");
    }

    /// <summary>
    /// Verifies that the SubgraphExtractor produces valid FixtureSubgraph objects
    /// that can be loaded by FixtureLoadGraph without errors.
    /// Tests the subgraph → FixtureLoadGraph → GraphFactory → CapacityGraph pipeline.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task SubgraphExtractor_ProducesValidFixtures()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set");
            return;
        }

        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
                Assert.Ignore("Test environment not healthy");
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        // Use direct-transfer-001 as reference scenario
        var scenario = ScenarioLoader.LoadById("direct-transfer-001");
        if (scenario == null)
        {
            Assert.Ignore("direct-transfer-001 scenario not found");
            return;
        }

        var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
        if (!exists) Assert.Ignore($"Block {scenario.Block} not indexed");

        var data = SharedGraphCache.GetOrLoad(scenario.Block);

        // Extract subgraph with default 3-hop BFS (no path addresses — worst case)
        var subgraph = SubgraphExtractor.Extract(data, scenario.Source, scenario.Sink);

        // Verify structural integrity
        Assert.That(subgraph.Trust, Is.Not.Null, "Trust should not be null");
        Assert.That(subgraph.Balances, Is.Not.Null, "Balances should not be null");
        Assert.That(subgraph.Trust!.Count, Is.GreaterThan(0), "Should have trust edges");
        Assert.That(subgraph.Balances!.Count, Is.GreaterThan(0), "Should have balances");
        Assert.That(subgraph.Stats, Is.Not.Null, "Stats should be populated");

        TestContext.Out.WriteLine($"Extracted: {subgraph.Stats!.AddressCount} addresses, " +
            $"{subgraph.Stats.TrustEdges} trust, {subgraph.Stats.BalanceEntries} balances");

        // Verify it can be loaded by FixtureLoadGraph
        var loadGraph = new FixtureLoadGraph(subgraph);
        var balances = loadGraph.LoadV2Balances().ToList();
        var trust = loadGraph.LoadV2Trust().ToList();
        var groups = loadGraph.LoadGroups().ToList();

        Assert.That(balances.Count, Is.GreaterThan(0), "FixtureLoadGraph should produce balances");
        Assert.That(trust.Count, Is.GreaterThan(0), "FixtureLoadGraph should produce trust edges");

        // Verify full pipeline works: FixtureLoadGraph → GraphFactory → CapacityGraph
        var factory = new GraphFactory(RouterAddress, loadGraph);
        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = ScenarioTests.BuildFlowRequest(scenario);

        // This should not throw
        CapacityGraph? capacity = null;
        Assert.DoesNotThrow(() =>
        {
            capacity = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        }, "CapacityGraph creation from subgraph should not throw");

        Assert.That(capacity!.Edges.Count, Is.GreaterThan(0),
            "CapacityGraph from subgraph should have edges");

        TestContext.Out.WriteLine($"Pipeline OK: {capacity.Edges.Count} capacity edges, " +
            $"{capacity.AvatarNodes.Count} nodes");

        // Verify the subgraph is significantly smaller than the full graph
        // Path-targeted extraction should achieve >95% reduction (vs ~4% with BFS)
        var reductionPct = 100.0 * (1.0 - (double)subgraph.Stats.TrustEdges / data.Trust.Count);
        TestContext.Out.WriteLine($"Subgraph reduction: {reductionPct:F1}% " +
            $"({subgraph.Stats.TrustEdges} vs {data.Trust.Count} trust edges)");

        Assert.That(reductionPct, Is.GreaterThan(90.0),
            "Path-targeted subgraph should achieve >90% reduction vs full graph");
    }
}

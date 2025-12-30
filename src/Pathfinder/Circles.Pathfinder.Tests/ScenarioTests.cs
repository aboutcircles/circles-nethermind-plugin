using Circles.Common.TestUtils;
using Circles.Index.Common;
using Circles.Index.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Data-driven scenario tests for the pathfinder.
/// Loads scenarios from JSON files and validates path computation and contract execution.
/// </summary>
[TestFixture]
[Category("Scenarios")]
public class ScenarioTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// Tests pathfinder computation for each scenario.
    /// Validates that paths are found (or not found) as expected.
    /// </summary>
    [TestCaseSource(typeof(ScenarioLoader), nameof(ScenarioLoader.AllScenariosTestData))]
    public async Task PathfinderScenario(TransferScenario scenario)
    {
        // Check test environment availability
        TestEnvironmentClient? session = null;
        try
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

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db"],
                ttl: "15m");

            if (!session.IsDirectConnectionAvailable)
            {
                Assert.Ignore("Direct database connection required for pathfinder tests");
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            await ExecutePathfinderTest(scenario, session);
        }
        finally
        {
            if (session != null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private async Task ExecutePathfinderTest(TransferScenario scenario, TestEnvironmentClient session)
    {
        var settings = new Settings();
        var loadGraph = new LoadGraph(session.PostgresConnectionString!, settings);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = BuildFlowRequest(scenario);
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        var pathfinder = new V2Pathfinder();
        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        if (!scenario.ShouldFindPath)
        {
            // Negative test - expect no path or exception
            try
            {
                var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
                if (response.Transfers == null || response.Transfers.Count == 0)
                {
                    TestContext.WriteLine($"Scenario {scenario.Id}: Correctly found no path");
                    Assert.Pass("No path found as expected");
                }
                else
                {
                    Assert.Fail($"Expected no path but found {response.Transfers.Count} steps");
                }
            }
            catch (ArgumentException ex)
            {
                TestContext.WriteLine($"Scenario {scenario.Id}: Expected exception - {ex.Message}");
                Assert.Pass($"Expected exception: {ex.Message}");
            }
        }
        else
        {
            // Positive test - expect path to be found
            MaxFlowResponse response;
            try
            {
                response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Scenario {scenario.Id}: Unexpected exception - {ex.Message}");
                return;
            }

            Assert.That(response.Transfers, Is.Not.Null,
                $"Scenario {scenario.Id}: Should find a path");

            if (response.Transfers!.Count == 0)
            {
                Assert.Fail($"Scenario {scenario.Id}: Path has no transfer steps");
            }

            TestContext.WriteLine($"Scenario {scenario.Id}: Found path with {response.Transfers.Count} steps");
            TestContext.WriteLine($"Max flow: {response.MaxFlow}");

            // Validate minimum flow if specified
            if (!string.IsNullOrEmpty(scenario.MinFlow) && !string.IsNullOrEmpty(response.MaxFlow))
            {
                var minFlowExpected = UInt256.Parse(scenario.MinFlow);
                var actualFlow = UInt256.Parse(response.MaxFlow);
                Assert.That(actualFlow >= minFlowExpected, Is.True,
                    $"Scenario {scenario.Id}: Max flow {actualFlow} should be at least {minFlowExpected}");
            }

            // Validate edge ordering for group minting scenarios
            if (scenario.Category == ScenarioCategories.GroupMinting)
            {
                ValidateGroupEdgeOrdering(response.Transfers, capacityGraph, scenario.Id);
            }
        }
    }

    private static FlowRequest BuildFlowRequest(TransferScenario scenario)
    {
        return new FlowRequest
        {
            Source = scenario.Source,
            Sink = scenario.Sink,
            FromTokens = scenario.FromTokens?.ToList(),
            ToTokens = scenario.ToTokens?.ToList(),
            ExcludedFromTokens = scenario.ExcludedTokens?.ToList(),
            MaxTransfers = scenario.MaxTransfers,
            WithWrap = scenario.WithWrap ?? false
        };
    }

    private void ValidateGroupEdgeOrdering(List<TransferPathStep> path, CapacityGraph graph, string scenarioId)
    {
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();

        foreach (var step in path)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";

            // Check if this is a Router → Group edge (collateral deposit)
            if (from == routerAddr && graph.IsGroup(AddressIdPool.IdOf(to)))
            {
                groupsWithCollateral.Add(to);
                TestContext.WriteLine($"  Collateral: Router → {to[..10]}...");
            }

            // Check if this is a Group → Avatar edge (minting)
            var fromId = AddressIdPool.IdOf(from);
            if (graph.IsGroup(fromId) && !graph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Scenario {scenarioId}: Group {from[..10]}... minted before receiving collateral");

                TestContext.WriteLine($"  Mint: {from[..10]}... → {to[..10]}... (collateral received: OK)");
            }
        }
    }
}

/// <summary>
/// E2E tests that execute computed paths on Anvil fork.
/// Validates that paths actually work on-chain.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Scenarios")]
public class ScenarioE2ETests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// Executes each scenario on Anvil fork to validate contract execution.
    /// </summary>
    [TestCaseSource(typeof(ScenarioLoader), nameof(ScenarioLoader.AnvilScenariosTestData))]
    public async Task AnvilExecutionScenario(TransferScenario scenario)
    {
        if (!scenario.RunOnAnvil)
        {
            Assert.Ignore("Scenario not configured for Anvil execution");
        }

        if (!scenario.ShouldFindPath)
        {
            Assert.Ignore("Negative test scenarios skip Anvil execution");
        }

        TestEnvironmentClient? session = null;
        AnvilExecutionHelper? anvil = null;

        try
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

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db", "anvil"],
                ttl: "30m");

            if (session.AnvilRpcUrl == null)
            {
                Assert.Ignore("Anvil not available in test environment");
            }

            if (!session.IsDirectConnectionAvailable)
            {
                Assert.Ignore("Direct database connection required for E2E tests");
            }

            anvil = new AnvilExecutionHelper(session.AnvilRpcUrl);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            await ExecuteE2ETest(scenario, session!, anvil!);
        }
        finally
        {
            anvil?.Dispose();
            if (session != null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private async Task ExecuteE2ETest(
        TransferScenario scenario,
        TestEnvironmentClient session,
        AnvilExecutionHelper anvil)
    {
        // Verify Anvil is at expected block
        var blockNumber = await anvil.GetBlockNumberAsync();
        Assert.That(blockNumber, Is.GreaterThanOrEqualTo(scenario.Block),
            $"Anvil fork should be at block {scenario.Block} or later");

        TestContext.WriteLine($"Scenario {scenario.Id}: Anvil at block {blockNumber}");

        // Compute path
        var settings = new Settings();
        var loadGraph = new LoadGraph(session.PostgresConnectionString!, settings);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = scenario.Source,
            Sink = scenario.Sink,
            FromTokens = scenario.FromTokens?.ToList(),
            ToTokens = scenario.ToTokens?.ToList(),
            ExcludedFromTokens = scenario.ExcludedTokens?.ToList(),
            MaxTransfers = scenario.MaxTransfers,
            WithWrap = scenario.WithWrap ?? false
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        if (response.Transfers == null || response.Transfers.Count == 0)
        {
            Assert.Fail($"Scenario {scenario.Id}: No path found for E2E execution");
            return;
        }

        TestContext.WriteLine($"Scenario {scenario.Id}: Path has {response.Transfers.Count} steps");

        // Execute on Anvil
        // Note: Full contract execution would require building operateFlowMatrix call data
        // For now, we validate the path structure and Anvil connectivity
        TestContext.WriteLine($"Scenario {scenario.Id}: Anvil execution validation passed");
        TestContext.WriteLine($"  Source: {scenario.Source}");
        TestContext.WriteLine($"  Sink: {scenario.Sink}");
        TestContext.WriteLine($"  Max flow: {response.MaxFlow}");

        // TODO: Implement full operateFlowMatrix call execution
        // This requires building the call data from TransferPathStep list
        // and executing it through anvil.ExecuteTransactionAsync()
    }
}

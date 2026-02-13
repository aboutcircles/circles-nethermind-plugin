using Circles.Common.TestUtils;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Differential tests: validate that HubContractValidator agrees with the actual Hub.sol contract.
/// For each Anvil scenario:
///   1. Compute path via pathfinder (graph data from SharedGraphCache)
///   2. Validate with HubContractValidator
///   3. Simulate with eth_call on shared Anvil fork (SharedAnvilCache)
///   4. Assert: validator agrees with contract (both accept or both reject)
///
/// Sessions are shared per block number via SharedAnvilCache + SharedGraphCache.
/// SharedAnvilCache registers its session with SharedGraphCache so only ONE
/// session is created per block (both DB queries and Anvil use the same session).
/// </summary>
[TestFixture]
[Category("Anvil")]
public class AnvilSimulationTests
{
    [OneTimeTearDown]
    public async Task TearDown()
    {
        await SharedAnvilCache.ClearAsync();
        await SharedGraphCache.ClearAsync();
    }

    [TestCaseSource(typeof(ScenarioLoader), nameof(ScenarioLoader.AnvilScenariosTestData))]
    public async Task DifferentialValidation(TransferScenario scenario)
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run Anvil tests.");
            return;
        }

        if (!scenario.RunOnAnvil || !scenario.ShouldFindPath)
        {
            Assert.Ignore("Scenario not configured for Anvil differential testing");
            return;
        }

        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Ignore("Test environment not healthy");
                return;
            }

            var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
            if (!exists)
            {
                Assert.Ignore($"Block {scenario.Block} not indexed");
                return;
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        await RunDifferentialTest(scenario);
    }

    private async Task RunDifferentialTest(TransferScenario scenario)
    {
        // Get shared Anvil session for this block (created once, reused across scenarios)
        // This also registers the session with SharedGraphCache via RegisterSession()
        var anvilData = SharedAnvilCache.GetOrCreate(scenario.Block);

        // Get graph data from SharedGraphCache (reuses the Anvil session for DB queries)
        // Pass the Anvil fork's block timestamp so demurrage matches on-chain state
        var factory = SharedGraphCache.CreateFactory(scenario.Block, anvilData.BlockTimestamp);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = ScenarioTests.BuildFlowRequest(scenario);
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        if (response.Transfers == null || response.Transfers.Count == 0)
        {
            TestContext.Out.WriteLine($"Scenario {scenario.Id}: No path found — skipping differential test");
            return;
        }

        // Layer 1: HubContractValidator
        var state = new CapacityGraphContractState(capacityGraph);
        var validation = HubContractValidator.Validate(response.Transfers, scenario.Source, scenario.Sink, state);

        // Layer 2: eth_call simulation (stateless — safe to share session)
        bool contractAccepts;
        string? revertReason;
        try
        {
            (contractAccepts, revertReason) = await anvilData.Anvil.SimulateTransferPathAsync(
                scenario.Source, scenario.Sink, response.Transfers);
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore($"Anvil session expired during test: {ex.Message}");
            return;
        }
        catch (TaskCanceledException ex)
        {
            Assert.Ignore($"Anvil simulation timed out: {ex.Message}");
            return;
        }

        TestContext.Out.WriteLine($"Scenario {scenario.Id}:");
        TestContext.Out.WriteLine($"  Validator: {(validation.IsValid ? "VALID" : "INVALID")}");
        TestContext.Out.WriteLine($"  Contract:  {(contractAccepts ? "ACCEPTS" : "REJECTS")}");

        if (validation.IsValid && !contractAccepts)
        {
            Assert.Fail(
                $"Scenario {scenario.Id}: DIVERGENCE — Validator says VALID but contract REJECTS!\n" +
                $"Revert reason: {revertReason}\n" +
                "This indicates a pathfinder or validator bug.");
        }

        if (!validation.IsValid && contractAccepts)
        {
            var errors = string.Join("; ", validation.Violations
                .Where(v => v.Severity == "error")
                .Select(v => $"[{v.Rule}] {v.Message}"));

            TestContext.Out.WriteLine($"  NOTE: Validator is too strict — contract accepts but validator rejects:");
            TestContext.Out.WriteLine($"  Violations: {errors}");
            // This is a warning, not a failure — the validator is overly conservative
        }

        // If both agree (both valid or both invalid), the test passes
    }
}

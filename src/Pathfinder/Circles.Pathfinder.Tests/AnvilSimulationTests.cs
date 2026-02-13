using Circles.Common;
using Circles.Common.TestUtils;
using Circles.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Differential tests: validate that HubContractValidator agrees with the actual Hub.sol contract.
/// For each Anvil scenario:
///   1. Compute path via pathfinder
///   2. Validate with HubContractValidator
///   3. Simulate with eth_call on Anvil fork
///   4. Assert: validator agrees with contract (both accept or both reject)
///
/// If validator says VALID but contract REJECTS → pathfinder or validator bug
/// If validator says INVALID but contract ACCEPTS → validator is too strict
/// </summary>
[TestFixture]
[Category("Anvil")]
public class AnvilSimulationTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

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

        TestEnvironmentClient? session = null;
        AnvilExecutionHelper? anvil = null;

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

            session = await TestEnvironmentClient.CreateSessionAsync(
                scenario.Block,
                features: ["db", "anvil"],
                ttl: "30m");

            if (!session.HasAnvil)
            {
                Assert.Ignore("Anvil not available in test environment");
                return;
            }

            anvil = new AnvilExecutionHelper(session);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            await RunDifferentialTest(scenario, session!, anvil!);
        }
        finally
        {
            anvil?.Dispose();
            if (session != null)
                await session.DisposeAsync();
        }
    }

    private async Task RunDifferentialTest(
        TransferScenario scenario,
        TestEnvironmentClient session,
        AnvilExecutionHelper anvil)
    {
        // Get block timestamp for demurrage
        var blockTimestamp = await anvil.GetBlockTimestampAsync();

        var settings = new Settings { TargetDemurrageTimestamp = blockTimestamp };
        ILoadGraph loadGraph = session.IsDirectConnectionAvailable
            ? new LoadGraph(session.PostgresConnectionString!, settings)
            : new ProxyLoadGraph(session, settings);

        var factory = new GraphFactory(RouterAddress, loadGraph);
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

        // Layer 2: eth_call simulation
        var (contractAccepts, revertReason) = await anvil.SimulateTransferPathAsync(
            scenario.Source, scenario.Sink, response.Transfers);

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

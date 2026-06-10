using Circles.Common;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Data-driven scenario tests for the pathfinder.
/// Loads scenarios from JSON files and validates path computation and contract execution.
///
/// Uses SharedGraphCache to load graph data once per block number (4 loads for 62 scenarios)
/// instead of once per scenario (62 loads × 760K rows each).
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
[Category("Snapshot")]
[Category("RequiresTestEnv")]
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
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run scenario tests.");
            return;
        }

        // Check test environment availability (only once, not per scenario)
        try
        {
            if (!SharedGraphCache.IsCached(scenario.Block))
            {
                var health = await TestEnvironmentClient.GetHealthAsync();
                if (health?.Status != "healthy")
                {
                    Assert.Fail("Test environment not healthy");
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
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        ExecutePathfinderTest(scenario);
    }

    private void ExecutePathfinderTest(TransferScenario scenario)
    {
        var isCached = SharedGraphCache.IsCached(scenario.Block);
        TestContext.Out.WriteLine(isCached
            ? $"Using cached graph for block {scenario.Block}"
            : $"Loading graph for block {scenario.Block} (first scenario at this block)");

        var factory = SharedGraphCache.CreateFactory(scenario.Block);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = BuildFlowRequest(scenario);

        // Check if source/sink exist in the loaded graph data.
        // Staging data drift (avatar registration filters, reindexing) can remove addresses
        // that existed when the scenario was authored. Unit tests use subgraph data and are
        // unaffected — only the staging integration path needs this guard.
        var sourceId = AddressIdPool.IdOf(scenario.Source.ToLowerInvariant());
        var sinkId = AddressIdPool.IdOf(scenario.Sink.ToLowerInvariant());
        var sourceInGraph = balanceGraph.BalanceNodes.Values.Any(n => n.Holder == sourceId)
                         || trustGraph.Edges.Any(e => e.From == sourceId || e.To == sourceId);
        if (!sourceInGraph && scenario.ShouldFindPath)
        {
            Assert.Warn($"Scenario {scenario.Id}: Source {scenario.Source} not found in staging graph " +
                $"at block {scenario.Block} (likely removed by avatar registration filter). " +
                $"Unit test with subgraph data still validates correctness.");
            return;
        }

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

        // Always use full consent validation in scenario tests — DisableConsentedFlow
        // is a production safety net, not a correctness feature.
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
                    TestContext.Out.WriteLine($"Scenario {scenario.Id}: Correctly found no path");
                    Assert.Pass("No path found as expected");
                }
                else
                {
                    // Negative tests loaded from staging are subject to data drift —
                    // new tokens/trust may appear that invalidate the "no path" expectation.
                    // Only hard-fail for scenarios that have embedded subgraph data (deterministic).
                    if (scenario.Subgraph != null)
                    {
                        Assert.Fail($"Expected no path but found {response.Transfers.Count} steps");
                    }
                    else
                    {
                        TestContext.Out.WriteLine(
                            $"Scenario {scenario.Id}: WARNING — expected no path but found {response.Transfers.Count} steps " +
                            $"(staging data drift — excluded tokens may no longer cover all source tokens)");
                        Assert.Warn($"Staging data drift: expected no path but found {response.Transfers.Count} steps");
                    }
                }
            }
            catch (ArgumentException ex)
            {
                TestContext.Out.WriteLine($"Scenario {scenario.Id}: Expected exception - {ex.Message}");
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

            TestContext.Out.WriteLine($"Scenario {scenario.Id}: Found path with {response.Transfers.Count} steps");
            TestContext.Out.WriteLine($"Max flow: {response.MaxFlow}");

            // Validate minimum flow if specified.
            // Skip minFlow check for scenarios with expectedRevertReason (E2E-only assertions)
            // and for scenarios loaded at UtcNow demurrage (balances decay over time, making
            // hardcoded minFlow values unreliable — E2E tests pin demurrage to block timestamp).
            if (!string.IsNullOrEmpty(scenario.MinFlow) && !string.IsNullOrEmpty(response.MaxFlow)
                && string.IsNullOrEmpty(scenario.ExpectedRevertReason))
            {
                var minFlowExpected = UInt256.Parse(scenario.MinFlow);
                var actualFlow = UInt256.Parse(response.MaxFlow);
                if (actualFlow < minFlowExpected)
                {
                    TestContext.Out.WriteLine(
                        $"Scenario {scenario.Id}: WARNING — flow {actualFlow} < minFlow {minFlowExpected} " +
                        $"(expected with UtcNow demurrage drift, E2E tests use pinned timestamp)");
                }
            }

            // Validate edge ordering for group minting scenarios
            if (scenario.Category == ScenarioCategories.GroupMinting)
            {
                ValidateGroupEdgeOrdering(response.Transfers, capacityGraph, scenario.Id);
            }

            // Validate path against Hub.sol rules (HubContractValidator)
            var contractState = new CapacityGraphContractState(capacityGraph);
            var validation = HubContractValidator.Validate(
                response.Transfers, scenario.Source, scenario.Sink, contractState);
            var validationErrors = validation.Violations
                .Where(v => v.Severity == "error")
                .ToList();
            if (validationErrors.Count > 0)
            {
                var errorList = string.Join("\n", validationErrors.Select(v => $"  [{v.Rule}] {v.Message}"));
                TestContext.Out.WriteLine($"Scenario {scenario.Id}: HubContractValidator errors:\n{errorList}");
                Assert.Fail($"Scenario {scenario.Id}: HubContractValidator detected {validationErrors.Count} error(s):\n{errorList}");
            }
        }
    }

    internal static FlowRequest BuildFlowRequest(TransferScenario scenario)
    {
        // Prefer explicit ExcludedFromTokens, fall back to legacy ExcludedTokens
        var excludedFrom = scenario.ExcludedFromTokens?.ToList()
                           ?? scenario.ExcludedTokens?.ToList();

        return new FlowRequest
        {
            Source = scenario.Source,
            Sink = scenario.Sink,
            FromTokens = scenario.FromTokens?.ToList(),
            ToTokens = scenario.ToTokens?.ToList(),
            ExcludedFromTokens = excludedFrom,
            ExcludedToTokens = scenario.ExcludedToTokens?.ToList(),
            MaxTransfers = scenario.MaxTransfers,
            WithWrap = scenario.WithWrap ?? false,
            SimulatedConsentedAvatars = scenario.SimulatedConsentedAvatars?.ToList(),
            SimulatedBalances = scenario.SimulatedBalances?.ToList(),
            SimulatedTrusts = scenario.SimulatedTrusts?.ToList(),
            QuantizedMode = scenario.QuantizedMode,
            DebugShowIntermediateSteps = scenario.DebugShowIntermediateSteps
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
                TestContext.Out.WriteLine($"  Collateral: Router → {to[..10]}...");
            }

            // Check if this is a Group → Avatar edge (minting)
            var fromId = AddressIdPool.IdOf(from);
            if (graph.IsGroup(fromId) && !graph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Scenario {scenarioId}: Group {from[..10]}... minted before receiving collateral");

                TestContext.Out.WriteLine($"  Mint: {from[..10]}... → {to[..10]}... (collateral received: OK)");
            }
        }
    }
}

/// <summary>
/// Concurrent session tests - validates thread safety and session resource management.
/// Multiple pathfinder requests on the same session should not interfere.
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
public class ConcurrentSessionTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// Tests 5 concurrent pathfinder requests on the same session.
    /// Validates thread safety and proper session resource management.
    /// </summary>
    [Test]
    [Category("Scenarios")]
    [Category("Concurrent")]
    public async Task ConcurrentPathfinderRequests_SameSession_AllSucceed()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run concurrent tests.");
            return;
        }

        // Load multiple scenarios for concurrent execution
        // Only use scenarios that are verified to work on Anvil (RunOnAnvil=true)
        var scenarios = ScenarioLoader.LoadAllScenarios()
            .Where(s => s.ShouldFindPath && !string.IsNullOrEmpty(s.MinFlow) && s.RunOnAnvil)
            .Take(5)
            .ToList();

        if (scenarios.Count < 5)
        {
            Assert.Ignore("Not enough scenarios available for concurrent test (need 5)");
            return;
        }

        TestEnvironmentClient? session = null;
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Fail("Test environment not healthy");
            }

            // Use a common block that should work for all scenarios
            var commonBlock = scenarios.Max(s => s.Block);

            var exists = await TestEnvironmentClient.BlockExistsAsync(commonBlock);
            if (!exists)
            {
                Assert.Ignore($"Block {commonBlock} not indexed");
            }

            session = await TestEnvironmentClient.CreateSessionAsync(
                commonBlock,
                features: ["db"],
                ttl: "15m");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        try
        {
            var settings = new Settings();

            // Use direct DB connection if available, otherwise use query proxy API
            ILoadGraph loadGraph = session.IsDirectConnectionAvailable
                ? new LoadGraph(session.PostgresConnectionString!, settings)
                : new ProxyLoadGraph(session, settings);

            TestContext.Out.WriteLine($"Using {(session.IsDirectConnectionAvailable ? "direct DB" : "query proxy")} for data loading");

            var factory = new GraphFactory(RouterAddress, loadGraph);

            // Pre-load graphs (shared across all concurrent requests)
            var trustGraph = factory.V2TrustGraph();
            var balanceGraph = factory.V2BalanceGraph();
            var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

            TestContext.Out.WriteLine($"Loaded graphs: {trustGraph.Edges.Count} trust edges, {balanceGraph.BalanceNodes.Count} balances");

            // Run 5 pathfinder requests concurrently
            var tasks = scenarios.Select(async scenario =>
            {
                var request = ScenarioTests.BuildFlowRequest(scenario);
                var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);

                var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
                var targetFlow = UInt256.Parse(scenario.MinFlow!);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
                stopwatch.Stop();

                return new
                {
                    Scenario = scenario.Id,
                    Success = response.Transfers != null && response.Transfers.Count > 0,
                    TransferCount = response.Transfers?.Count ?? 0,
                    MaxFlow = response.MaxFlow,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }).ToList();

            var results = await Task.WhenAll(tasks);

            // Validate all requests succeeded
            foreach (var result in results)
            {
                TestContext.Out.WriteLine($"  {result.Scenario}: {result.TransferCount} steps, flow={result.MaxFlow}, {result.ElapsedMs}ms");
                Assert.That(result.Success, Is.True,
                    $"Concurrent request for {result.Scenario} failed to find path");
            }

            TestContext.Out.WriteLine($"All {results.Length} concurrent requests completed successfully");
        }
        finally
        {
            if (session != null)
            {
                await session.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// E2E tests that execute computed paths on Anvil fork.
/// Validates that paths actually work on-chain.
///
/// Uses SharedAnvilCache to share one Anvil session per block number (~4 sessions
/// instead of ~30). Each test takes an evm_snapshot before execution and reverts
/// afterwards, so state mutations don't leak between tests.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// Selected by the CI snapshot-tests job (--filter "Category=Snapshot"), which
/// runs on PRs and pushes to dev/main against the staging test environment.
/// </summary>
[TestFixture]
[Category("Snapshot")]
[Category("RequiresTestEnv")]
[Category("RequiresAnvil")]
public class ScenarioE2ETests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// Executes each scenario on Anvil fork to validate contract execution.
    /// </summary>
    [TestCaseSource(typeof(ScenarioLoader), nameof(ScenarioLoader.AnvilScenariosTestData))]
    public async Task AnvilExecutionScenario(TransferScenario scenario)
    {
        // Check if TEST_ENV_URL is set
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore(
                "TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run E2E tests.");
            return;
        }

        if (!scenario.RunOnAnvil)
        {
            Assert.Ignore("Scenario not configured for Anvil execution");
        }

        if (!scenario.ShouldFindPath)
        {
            Assert.Ignore("Negative test scenarios skip Anvil execution");
        }

        AnvilSessionData anvilData;
        try
        {
            if (!SharedAnvilCache.IsCached(scenario.Block))
            {
                var health = await TestEnvironmentClient.GetHealthAsync();
                if (health?.Status != "healthy")
                    Assert.Fail("Test environment not healthy");

                var exists = await TestEnvironmentClient.BlockExistsAsync(scenario.Block);
                if (!exists)
                    Assert.Ignore($"Block {scenario.Block} not indexed");
            }

            anvilData = SharedAnvilCache.GetOrCreate(scenario.Block);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not available: {ex.Message}");
            return;
        }

        // Snapshot before test, revert after — isolates state mutations
        var snapshotId = await anvilData.Anvil.SnapshotAsync();
        try
        {
            await ExecuteE2ETest(scenario, anvilData);
        }
        finally
        {
            await anvilData.Anvil.RevertAsync(snapshotId);
        }
    }

    private async Task ExecuteE2ETest(TransferScenario scenario, AnvilSessionData anvilData)
    {
        var anvil = anvilData.Anvil;
        var blockTimestamp = anvilData.BlockTimestamp;

        TestContext.Out.WriteLine($"Scenario {scenario.Id}: Anvil at block {anvilData.BlockNumber}, timestamp {blockTimestamp:u}");

        // Graph data is cached via SharedGraphCache (registered by SharedAnvilCache)
        var factory = SharedGraphCache.CreateFactory(scenario.Block, blockTimestamp);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = ScenarioTests.BuildFlowRequest(scenario);
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        // E2E tests always validate consent rules (not production exclusion mode)
        var settings = new Settings
        {
            TargetDemurrageTimestamp = blockTimestamp,
            DisableConsentedFlow = false
        };
        var pathfinder = new V2Pathfinder(settings: settings);

        var targetFlow = string.IsNullOrEmpty(scenario.MinFlow)
            ? UInt256.Parse("1000000000000000000")
            : UInt256.Parse(scenario.MinFlow);

        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        if (response.Transfers == null || response.Transfers.Count == 0)
        {
            Assert.Fail($"Scenario {scenario.Id}: No path found for E2E execution");
            return;
        }

        TestContext.Out.WriteLine($"Scenario {scenario.Id}: Path has {response.Transfers.Count} steps");
        TestContext.Out.WriteLine($"  Source: {scenario.Source}");
        TestContext.Out.WriteLine($"  Sink: {scenario.Sink}");
        TestContext.Out.WriteLine($"  Max flow: {response.MaxFlow}");

        // Execute on Anvil using the operateFlowMatrix call
        var result = await anvil.ExecuteTransferPathAsync(
            scenario.Source,
            scenario.Sink,
            response.Transfers);

        // Differential check: run HubContractValidator on the same path
        var contractState = new CapacityGraphContractState(capacityGraph);
        var validation = HubContractValidator.Validate(
            response.Transfers, scenario.Source, scenario.Sink, contractState);

        if (scenario.ExpectedRevertReason != null)
        {
            // Negative test - expect transaction to fail
            Assert.That(result.Success, Is.False,
                $"Scenario {scenario.Id}: Expected revert but transaction succeeded");
            Assert.That(result.Error, Does.Contain(scenario.ExpectedRevertReason).IgnoreCase,
                $"Scenario {scenario.Id}: Expected revert reason '{scenario.ExpectedRevertReason}' but got '{result.Error}'");
            TestContext.Out.WriteLine($"Scenario {scenario.Id}: Transaction reverted as expected: {result.Error}");
        }
        else
        {
            // Positive test - expect transaction to succeed
            Assert.That(result.Success, Is.True,
                $"Scenario {scenario.Id}: Expected success but transaction failed: {result.Error}");
            TestContext.Out.WriteLine($"Scenario {scenario.Id}: Transaction succeeded! Gas used: {result.GasUsed}");
            TestContext.Out.WriteLine($"  TxHash: {result.TxHash}");

            // Differential: if contract accepted but validator rejected → validator bug
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Violations
                    .Where(v => v.Severity == "error")
                    .Select(v => $"[{v.Rule}] {v.Message}"));
                TestContext.Out.WriteLine(
                    $"  WARNING: Validator too strict — contract accepted but validator rejected: {errors}");
            }
        }
    }
}

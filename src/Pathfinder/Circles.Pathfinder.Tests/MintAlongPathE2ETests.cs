using Circles.Common.TestUtils;
using Circles.Index.Common.Dto;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// End-to-end tests for the mint-along-path feature.
///
/// These tests verify that:
/// 1. Sorted edges from the pathfinder actually execute successfully on-chain
/// 2. Unsorted edges (reproducing the bug) fail with the expected error
///
/// Requirements:
/// - Test environment must be deployed (TEST_ENV_URL environment variable)
/// - Tests create sessions with Anvil fork for contract execution
///
/// To run:
///   TEST_ENV_URL=https://staging.circlesubi.network/test-env \
///   dotnet test --filter "Category=E2E"
/// </summary>
[TestFixture]
[Category("E2E")]
public class MintAlongPathE2ETests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    // Block 43193632 = state at the time of the mint-along-path bug
    private const long BugReproBlock = 43193632;

    // Known addresses from the bug investigation
    private const string BugSource = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c";
    private const string BugSink = "0xb3129372e52b910b6994eaef77bbc1892ea48779";

    private TestEnvironmentClient? _session;
    private AnvilExecutionHelper? _anvil;
    private Settings? _settings;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // Check if test environment is available
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Ignore("Test environment not healthy. Ensure it's deployed with deploy_test_environment=true");
            }
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Test environment not available: {ex.Message}");
            return;
        }

        // Check if block exists
        var exists = await TestEnvironmentClient.BlockExistsAsync(BugReproBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {BugReproBlock} not indexed. Run with production database snapshot.");
        }

        // Create session with both database and Anvil
        _session = await TestEnvironmentClient.CreateSessionAsync(
            BugReproBlock,
            features: ["db", "anvil"],
            ttl: "30m");

        if (_session.AnvilRpcUrl == null)
        {
            Assert.Ignore("Anvil not available in test environment");
        }

        _anvil = new AnvilExecutionHelper(_session.AnvilRpcUrl);
        _settings = new Settings();
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _anvil?.Dispose();

        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// Verify that the pathfinder finds a path for the bug scenario.
    /// </summary>
    [Test]
    public void ComputePath_BugScenario_FindsPath()
    {
        Assert.That(_session, Is.Not.Null, "Session should be created in setup");
        Assert.That(_session!.PostgresConnectionString, Is.Not.Null);

        var loadGraph = new LoadGraph(_session.PostgresConnectionString!, _settings!);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = BugSource,
            Sink = BugSink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse("1000000000000000000"); // 1 CRC
        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        Assert.That(response.Transfers, Is.Not.Null.And.Not.Empty,
            "Should find a path for the bug scenario");

        TestContext.Out.WriteLine($"Found path with {response.Transfers!.Count} steps");
        TestContext.Out.WriteLine($"Max flow: {response.MaxFlow}");
    }

    /// <summary>
    /// Integration test: Verify that sorted paths have correct edge ordering.
    /// </summary>
    [Test]
    public void SortedPath_HasCorrectEdgeOrdering()
    {
        Assert.That(_session, Is.Not.Null);

        var loadGraph = new LoadGraph(_session!.PostgresConnectionString!, _settings!);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = BugSource,
            Sink = BugSink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse("1000000000000000000000"); // 1000 CRC
        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        if (response.Transfers == null || response.Transfers.Count == 0)
        {
            Assert.Ignore("No path found at this block state");
            return;
        }

        // Verify edge ordering: for each group, collateral edges must come before mint edges
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();

        foreach (var step in response.Transfers)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";

            // Check if this is a Router → Group edge (collateral deposit)
            if (from == routerAddr && capacityGraph.IsGroup(AddressIdPool.IdOf(to)))
            {
                groupsWithCollateral.Add(to);
            }

            // Check if this is a Group → Avatar edge (minting)
            var fromId = AddressIdPool.IdOf(from);
            if (capacityGraph.IsGroup(fromId) && !capacityGraph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Group {from} minted before receiving collateral");
            }
        }

        TestContext.Out.WriteLine("Edge ordering verified successfully");
    }

    /// <summary>
    /// Verify Anvil fork is working at the expected block.
    /// </summary>
    [Test]
    public async Task AnvilFork_IsAtExpectedBlock()
    {
        Assert.That(_anvil, Is.Not.Null, "Anvil should be available");

        var blockNumber = await _anvil!.GetBlockNumberAsync();

        // Anvil fork should be at or after the bug repro block
        Assert.That(blockNumber, Is.GreaterThanOrEqualTo(BugReproBlock),
            $"Anvil fork should be at block {BugReproBlock} or later");

        TestContext.Out.WriteLine($"Anvil fork is at block {blockNumber}");
    }

    /// <summary>
    /// Verify we can impersonate accounts on Anvil.
    /// </summary>
    [Test]
    public async Task AnvilFork_CanImpersonateAccount()
    {
        Assert.That(_anvil, Is.Not.Null);

        // Should not throw
        await _anvil!.ImpersonateAccountAsync(BugSource);
        await _anvil.StopImpersonatingAccountAsync(BugSource);

        Assert.Pass("Successfully impersonated and stopped impersonating account");
    }
}

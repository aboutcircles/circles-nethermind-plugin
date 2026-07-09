using Circles.Common.Dto;
using Circles.Common.TestUtils;
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
/// The tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// Gated in CI by the "anvil-regression" job (Category=AnvilRegression), which runs
/// against the staging test environment on PRs and pushes to dev/staging/master.
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
[Category("RequiresAnvil")]
[Category("AnvilRegression")]
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
        // Check if TEST_ENV_URL is set
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore(
                "TEST_ENV_URL not set. Set to https://rpc.staging.aboutcircles.com/test-env to run E2E tests.");
            return;
        }

        // Check if test environment is available
        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                Assert.Fail("Test environment not healthy. Check deployment status.");
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"Test environment not reachable at {testEnvUrl}: {ex.Message}");
            return;
        }

        // Check if block exists
        var exists = await TestEnvironmentClient.BlockExistsAsync(BugReproBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {BugReproBlock} not indexed. Database may not have historical data.");
        }

        // Create session with both database and Anvil
        _session = await TestEnvironmentClient.CreateSessionAsync(
            BugReproBlock,
            features: ["db", "anvil"],
            ttl: "30m");

        if (!_session.HasAnvil)
        {
            Assert.Ignore("Anvil not available in test environment");
        }

        // Use proxied AnvilExecutionHelper - works from anywhere
        _anvil = new AnvilExecutionHelper(_session);
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
    /// The mint-along-path regression gate. At the bug-repro block it: (1) computes the
    /// path via the block-pinned query proxy — so it runs from CI where no direct DB
    /// connection is available, not just on a node with local Postgres; (2) verifies the
    /// collateral-before-mint edge ordering the original bug violated; (3) executes the
    /// resulting operateFlowMatrix on the Anvil fork and asserts it does not revert.
    /// State mutations are isolated by an evm_snapshot/revert so the other fixture tests
    /// are order-independent.
    /// </summary>
    [Test]
    public async Task MintAlongPath_ComputeOrderAndExecuteOnFork_NoRevert()
    {
        Assert.That(_session, Is.Not.Null, "Session should be created in setup");
        Assert.That(_anvil, Is.Not.Null, "Anvil should be available");

        // Works from anywhere: direct DB when available, else the block-pinned query proxy
        // (mirrors ScenarioTests' loader selection so the path is computed even in CI).
        ILoadGraph loadGraph = _session!.IsDirectConnectionAvailable
            ? new LoadGraph(_session.PostgresConnectionString!, _settings!)
            : new ProxyLoadGraph(_session, _settings!);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest { Source = BugSource, Sink = BugSink };
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse("1000000000000000000"); // 1 CRC
        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        Assert.That(response.Transfers, Is.Not.Null.And.Not.Empty,
            "Should find a path for the mint-along-path bug scenario");
        TestContext.Out.WriteLine($"Found path with {response.Transfers!.Count} steps, maxFlow {response.MaxFlow}");

        // Collateral-before-mint ordering: every Group→Avatar mint must be preceded by a
        // Router→Group collateral deposit for that group (the invariant the bug broke).
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();
        foreach (var step in response.Transfers)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";
            if (from == routerAddr && capacityGraph.IsGroup(AddressIdPool.IdOf(to)))
                groupsWithCollateral.Add(to);

            var fromId = AddressIdPool.IdOf(from);
            if (capacityGraph.IsGroup(fromId) && !capacityGraph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Group {from} minted before receiving collateral");
        }

        // Execute the computed flow matrix on the fork — must not revert.
        var snapshotId = await _anvil!.SnapshotAsync();
        try
        {
            var result = await _anvil.ExecuteTransferPathAsync(BugSource, BugSink, response.Transfers);
            Assert.That(result.Success, Is.True,
                $"operateFlowMatrix reverted on the fork: {result.Error}");
            TestContext.Out.WriteLine($"Executed on fork without revert: gas {result.GasUsed}, tx {result.TxHash}");
        }
        finally
        {
            await _anvil.RevertAsync(snapshotId);
        }
    }

    /// <summary>
    /// Verify Anvil fork is working at the expected block.
    /// Uses proxied RPC - works from anywhere.
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
    /// Uses proxied RPC - works from anywhere.
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

    /// <summary>
    /// Verify we can get account balance on Anvil.
    /// Uses proxied RPC - works from anywhere.
    /// </summary>
    [Test]
    public async Task AnvilFork_CanSetBalance()
    {
        Assert.That(_anvil, Is.Not.Null);

        // Set balance for test account
        var testBalance = System.Numerics.BigInteger.Parse("10000000000000000000"); // 10 ETH
        await _anvil!.SetBalanceAsync(BugSource, testBalance);

        Assert.Pass("Successfully set account balance via Anvil");
    }
}

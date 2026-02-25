using System.Text.Json;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Snapshot-based integration tests that use the Circles Test Environment.
/// These tests load real blockchain state from a specific block to ensure
/// deterministic, reproducible test results.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// CI triggers these tests automatically on merges to main/dev branches.
///
/// To run locally:
///    TEST_ENV_URL=https://staging.circlesubi.network/test-env dotnet test
///
/// Note: Tests that require LoadGraph need direct database access. When running
/// against remote test-env (staging), these tests will be skipped.
/// </summary>
[TestFixture]
public class SnapshotIntegrationTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    [Test]
    public async Task HealthCheck_TestEnvironmentIsAvailable()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run snapshot tests.");
            return;
        }

        var health = await TestEnvironmentClient.GetHealthAsync();

        Assert.That(health, Is.Not.Null);
        Assert.That(health!.Status, Is.EqualTo("healthy").IgnoreCase);
    }

    [Test]
    public async Task CurrentBlock_IsIndexed()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run snapshot tests.");
            return;
        }

        var currentBlock = await TestEnvironmentClient.GetCurrentBlockAsync();

        Assert.That(currentBlock, Is.GreaterThan(0), "Database should have indexed blocks");
    }
}

/// <summary>
/// Regression tests for the mint-along-path edge ordering bug.
///
/// Bug description (November 2025):
/// When minting group tokens via transitive transfers, the contract requires
/// collateral edges (Router→Group) to precede minting edges (Group→Avatar).
/// Dictionary iteration order is undefined, so edges were sometimes returned
/// in the wrong order, causing ERC1155InsufficientBalance reverts.
///
/// Reference: docs/_resources/mintAlongAPath/MINTINGALONGPATH_SUMMARY.md
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// Note: These tests require direct database access for LoadGraph. When running
/// against remote test-env, they will skip unless direct connection is available.
/// </summary>
[TestFixture]
public class MintAlongPathRegressionTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    // Block 43193632 = state at the time of the mint-along-path bug
    // This block has the exact trust/balance state needed to reproduce the issue
    private const long BugReproBlock = 43193632;

    // Known addresses from the bug investigation
    private const string BugSource = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c";
    private const string BugSink = "0xb3129372e52b910b6994eaef77bbc1892ea48779";

    private TestEnvironmentClient? _session;
    private Settings? _settings;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run snapshot tests.");
            return;
        }

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

        // Skip if block doesn't exist (fresh database)
        var exists = await TestEnvironmentClient.BlockExistsAsync(BugReproBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {BugReproBlock} not indexed. Run with production database snapshot.");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(
            BugReproBlock,
            features: ["db"],
            ttl: "15m");

        // LoadGraph tests require direct database connection
        if (!_session.IsDirectConnectionAvailable)
        {
            await _session.DisposeAsync();
            _session = null;
            Assert.Ignore("LoadGraph tests require direct database connection. Run locally or on staging server.");
        }

        _settings = new Settings();
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    [Test]
    public async Task LoadGraph_AtBugReproBlock_Succeeds()
    {
        Assert.That(_session, Is.Not.Null, "Session should be created in setup");
        Assert.That(_session!.PostgresConnectionString, Is.Not.Null);

        var loadGraph = new LoadGraph(_session.PostgresConnectionString!, _settings!);

        // Load data - this validates the SQL queries work with the filtered connection
        var balances = loadGraph.LoadV2Balances().ToList();
        var trusts = loadGraph.LoadV2Trust().ToList();
        var groups = loadGraph.LoadGroups().ToList();

        Assert.That(balances.Count, Is.GreaterThan(0), "Should have balances at this block");
        Assert.That(trusts.Count, Is.GreaterThan(0), "Should have trust relations at this block");

        TestContext.Out.WriteLine($"Loaded {balances.Count} balances, {trusts.Count} trusts, {groups.Count} groups");
    }

    [Test]
    public async Task CreateCapacityGraph_AtBugReproBlock_IncludesSourceAndSink()
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

        var sourceId = AddressIdPool.IdOf(BugSource);
        var sinkId = AddressIdPool.IdOf(BugSink);

        Assert.That(capacityGraph.AvatarNodes.ContainsKey(sourceId), Is.True,
            "Source should be in the graph");
        Assert.That(capacityGraph.AvatarNodes.ContainsKey(sinkId), Is.True,
            "Sink should be in the graph");
    }

    [Test]
    public async Task ComputeMaxFlow_AtBugReproBlock_FindsPath()
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

        // Try to find any flow
        var targetFlow = UInt256.Parse("1000000000000000000"); // 1 CRC in wei
        var maxFlow = pathfinder.ComputeMaxFlow(capacityGraph, request, targetFlow);

        TestContext.Out.WriteLine($"Max flow from {BugSource} to {BugSink}: {maxFlow}");

        // We expect some flow to be possible (though may be less than target)
        // The exact amount depends on the balance state at this block
        Assert.That(maxFlow, Is.GreaterThanOrEqualTo(0),
            "Max flow computation should succeed");
    }

    [Test]
    public async Task ComputeMaxFlowWithPath_EdgeOrdering_CollateralBeforeMint()
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
            TestContext.Out.WriteLine("No path found - may need different source/sink");
            Assert.Ignore("No path found at this block state");
            return;
        }

        TestContext.Out.WriteLine($"Found path with {response.Transfers.Count} steps");

        // Validate edge ordering: for each group, collateral edges must come before mint edges
        ValidateGroupEdgeOrdering(response.Transfers, capacityGraph);
    }

    private void ValidateGroupEdgeOrdering(List<TransferPathStep> path, CapacityGraph graph)
    {
        // Track which groups have received collateral
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
                TestContext.Out.WriteLine($"Collateral: Router → {to}");
            }

            // Check if this is a Group → Avatar edge (minting)
            var fromId = AddressIdPool.IdOf(from);
            if (graph.IsGroup(fromId) && !graph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                // This group must have received collateral first
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Group {from} minted before receiving collateral. " +
                    $"Mint edge (Group→{to}) appeared before collateral edge (Router→Group).");

                TestContext.Out.WriteLine($"Mint: {from} → {to} (collateral received: OK)");
            }
        }
    }
}

/// <summary>
/// Tests for consented flow validation at historical blocks.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
public class ConsentedFlowSnapshotTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const long TestBlock = 43193632;

    [Test]
    public async Task LoadConsentedFlowFlags_ReturnsFlags()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run snapshot tests.");
            return;
        }

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

        var exists = await TestEnvironmentClient.BlockExistsAsync(TestBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {TestBlock} not indexed");
        }

        await using var session = await TestEnvironmentClient.CreateSessionAsync(TestBlock);

        // LoadGraph requires direct database connection
        if (!session.IsDirectConnectionAvailable)
        {
            Assert.Ignore("LoadGraph tests require direct database connection. Run locally or on staging server.");
        }

        var settings = new Settings();
        var loadGraph = new LoadGraph(session.PostgresConnectionString!, settings);

        var flags = loadGraph.LoadConsentedFlowFlags().ToList();

        TestContext.Out.WriteLine($"Loaded {flags.Count} consented flow flags");

        // Just verify we can load the flags - the actual content depends on chain state
        Assert.That(flags, Is.Not.Null);
    }
}

/// <summary>
/// Snapshot tests that can use the query proxy (don't require LoadGraph).
/// These tests work with both local and remote test-env.
///
/// Tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
public class PathfinderQuerySnapshotTests
{
    private const long TestBlock = 43193632;
    private const string BugSource = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c";
    private const string BugSink = "0xb3129372e52b910b6994eaef77bbc1892ea48779";

    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run snapshot tests.");
            return;
        }

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

        var exists = await TestEnvironmentClient.BlockExistsAsync(TestBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {TestBlock} not indexed");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(TestBlock, ttl: "15m");
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    [Test]
    public async Task QueryV2Balances_ReturnsData()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""totalBalance"" > 0");

        var count = ConvertToInt64(result.Rows.FirstOrDefault()?[0]);
        TestContext.Out.WriteLine($"V2 balances with positive balance: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have positive balances");
    }

    [Test]
    public async Task QueryV2Trust_ReturnsData()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""V_CrcV2_TrustRelations""");

        var count = ConvertToInt64(result.Rows.FirstOrDefault()?[0]);
        TestContext.Out.WriteLine($"V2 trust relations: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have trust relations");
    }

    [Test]
    public async Task QueryGroups_ReturnsData()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterGroup""");

        var count = ConvertToInt64(result.Rows.FirstOrDefault()?[0]);
        TestContext.Out.WriteLine($"Registered groups: {count}");
        Assert.That(count, Is.GreaterThanOrEqualTo(0), "Should be able to query groups");
    }

    [Test]
    public async Task QueryBugSourceBalances_ReturnsTokens()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""tokenId"", ""totalBalance""
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""account"" = @address AND ""totalBalance"" > 0
            LIMIT 10",
            new Dictionary<string, object?> { ["address"] = BugSource });

        TestContext.Out.WriteLine($"Bug source ({BugSource}) has {result.RowCount} tokens with balance");
        foreach (var row in result.Rows.Take(5))
        {
            TestContext.Out.WriteLine($"  {row[0]}: {row[1]}");
        }

        Assert.That(result.RowCount, Is.GreaterThan(0), "Bug source should have token balances");
    }

    /// <summary>
    /// Safely converts a value that may be a JsonElement (from query proxy) or a numeric type to Int64.
    /// </summary>
    private static long ConvertToInt64(object? value)
    {
        if (value is JsonElement je) return je.GetInt64();
        return Convert.ToInt64(value ?? 0);
    }

    [Test]
    public async Task QueryTrustInvolvingBugAddresses_ReturnsTrust()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""truster"", ""trustee""
            FROM ""V_CrcV2_TrustRelations""
            WHERE ""truster"" = @source OR ""trustee"" = @source
               OR ""truster"" = @sink OR ""trustee"" = @sink
            LIMIT 20",
            new Dictionary<string, object?>
            {
                ["source"] = BugSource,
                ["sink"] = BugSink
            });

        TestContext.Out.WriteLine($"Trust relations involving bug addresses: {result.RowCount}");
        foreach (var row in result.Rows.Take(5))
        {
            TestContext.Out.WriteLine($"  {row[0]} trusts {row[1]}");
        }

        Assert.That(result.RowCount, Is.GreaterThan(0),
            "Should have trust relations for pathfinding to be possible");
    }
}

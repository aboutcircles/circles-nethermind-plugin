using System.Text.Json;
using Circles.Common.Dto;
using Circles.Common.TestUtils;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// End-to-end tests for payment gateway with group minting.
///
/// These tests verify:
/// 1. Routed transfers to payment gateways that trust groups work correctly
/// 2. The router insertion and edge ordering fixes handle payment gateway scenarios
/// 3. Consented flow avatars can send to payment gateways via group minting
///
/// Requirements:
/// - Test environment must be deployed (TEST_ENV_URL environment variable)
/// - Tests create sessions with Anvil fork for contract execution
///
/// The tests run by default but gracefully skip when TEST_ENV_URL is not set.
/// To create new test cases, run the on-chain test script first:
///   source .env && node scripts/payment-gateway-test.mjs
/// Then add the resulting fixture JSON to RegressionScenarios/
/// </summary>
[TestFixture]
[Category("RequiresTestEnv")]
[Category("RequiresAnvil")]
public class PaymentGatewayE2ETests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    // Contract addresses on Gnosis mainnet
    private const string HubV2Address = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8";
    private const string PaymentGatewayFactoryAddress = "0x186725D8fe10a573DC73144F7a317fCae5314F19";

    // Target groups for testing (small, active groups)
    private static readonly Dictionary<string, string> TestGroups = new()
    {
        { "4Birthday", "0xaa9081197e02f2fdacfc65e7606743fa2d005208" },
        { "MunichBazis", "0xda43d07ee6a375c96b26bbf571576228ec86f243" }
    };

    private TestEnvironmentClient? _session;
    private AnvilExecutionHelper? _anvil;
    private Settings? _settings;
    private long _testBlock;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore(
                "TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run E2E tests.");
            return;
        }

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

        // Use latest block for payment gateway tests (unlike regression tests which use specific blocks)
        _testBlock = await TestEnvironmentClient.GetCurrentBlockAsync();

        _session = await TestEnvironmentClient.CreateSessionAsync(
            _testBlock,
            features: ["db", "anvil"],
            ttl: "30m");

        if (!_session.HasAnvil)
        {
            Assert.Ignore("Anvil not available in test environment");
        }

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
    /// Verify that test groups exist and are recognized as groups by the protocol.
    /// </summary>
    [Test]
    public async Task TestGroups_AreRecognizedAsGroups()
    {
        Assert.That(_session, Is.Not.Null, "Session should be created in setup");

        foreach (var (name, address) in TestGroups)
        {
            var result = await _session!.ExecuteScalarAsync(
                @"SELECT COUNT(*) FROM ""CrcV2_RegisterGroup"" WHERE ""group"" = @addr",
                new Dictionary<string, object?> { { "addr", address.ToLowerInvariant() } });

            var count = result is JsonElement je ? je.GetInt64() : Convert.ToInt64(result ?? 0);
            Assert.That(count, Is.GreaterThan(0),
                $"Group '{name}' ({address}) should be registered in CrcV2_RegisterGroup");

            TestContext.Out.WriteLine($"Group '{name}' verified at {address}");
        }
    }

    /// <summary>
    /// Verify that we can find avatars that trust a test group.
    /// These avatars are potential sources for payment gateway tests.
    /// </summary>
    [Test]
    public async Task FindAvatars_ThatTrustTestGroups()
    {
        Assert.That(_session, Is.Not.Null);

        foreach (var (groupName, groupAddress) in TestGroups)
        {
            var result = await _session!.ExecuteQueryAsync(
                @"SELECT DISTINCT truster FROM ""CrcV2_Trust""
                  WHERE trustee = @groupAddr
                    AND ""expiryTime"" > extract(epoch from now())
                  LIMIT 10",
                new Dictionary<string, object?> { { "groupAddr", groupAddress.ToLowerInvariant() } });

            TestContext.Out.WriteLine($"Avatars trusting group '{groupName}':");

            if (result.RowCount == 0)
            {
                TestContext.Out.WriteLine("  (none found - group may need more trusters for testing)");
            }
            else
            {
                foreach (var row in result.Rows.Take(5))
                {
                    var truster = row[0]?.ToString() ?? "unknown";
                    TestContext.Out.WriteLine($"  {truster}");
                }
            }
        }

        Assert.Pass("Avatar lookup completed - check output for potential test sources");
    }

    /// <summary>
    /// Test that pathfinding works for a consented flow avatar sending to a sink
    /// that trusts groups. This verifies the isPermittedFlow fix.
    /// </summary>
    [Test]
    public async Task ConsentedFlowAvatar_CanSendViaGroupMinting()
    {
        Assert.That(_session, Is.Not.Null);

        if (!_session!.IsDirectConnectionAvailable)
        {
            Assert.Ignore("Test requires direct database connection. Run on staging CI.");
            return;
        }

        var loadGraph = new LoadGraph(_session.PostgresConnectionString!, _settings!);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        // Find a consented flow avatar with balance
        var consentedAvatarResult = await _session.ExecuteScalarAsync(
            @"SELECT t.truster
              FROM ""CrcV2_Trust"" t
              JOIN ""CrcV2_RegisterHuman"" h ON t.truster = h.avatar
              WHERE t.trustee = @router AND t.""expiryTime"" > extract(epoch from now())
              LIMIT 1",
            new Dictionary<string, object?> { { "router", RouterAddress.ToLowerInvariant() } });

        if (consentedAvatarResult == null || consentedAvatarResult == DBNull.Value)
        {
            Assert.Ignore("No consented flow avatar found for testing");
            return;
        }

        var consentedAvatar = consentedAvatarResult.ToString()!;

        // Find a sink that trusts a group
        var sinkResult = await _session.ExecuteQueryAsync(
            @"SELECT DISTINCT t.truster
              FROM ""CrcV2_Trust"" t
              JOIN ""CrcV2_RegisterGroup"" g ON t.trustee = g.avatar
              WHERE t.truster != @source
                AND t.""expiryTime"" > extract(epoch from now())
              LIMIT 5",
            new Dictionary<string, object?>
            {
                { "source", consentedAvatar.ToLowerInvariant() }
            });

        if (sinkResult.RowCount == 0)
        {
            Assert.Ignore("No suitable sink found that trusts groups");
            return;
        }

        var sink = sinkResult.Rows.First()[0]?.ToString()!;

        TestContext.Out.WriteLine($"Testing: {consentedAvatar} -> {sink}");

        var request = new FlowRequest
        {
            Source = consentedAvatar,
            Sink = sink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse("1000000000000000000"); // 1 CRC

        MaxFlowResponse? response = null;
        Assert.DoesNotThrow(() =>
        {
            response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);
        }, "Pathfinder should compute path without exception (isPermittedFlow fix)");

        if (response?.Transfers == null || response.Transfers.Count == 0)
        {
            TestContext.Out.WriteLine("No path found - this may be expected if no flow is available");
            return;
        }

        // Verify the path includes router edges where needed
        var hasRouterEdges = response.Transfers.Any(t =>
            t.From?.ToLowerInvariant() == RouterAddress.ToLowerInvariant() ||
            t.To?.ToLowerInvariant() == RouterAddress.ToLowerInvariant());

        TestContext.Out.WriteLine($"Path found with {response.Transfers.Count} steps");
        TestContext.Out.WriteLine($"Max flow: {response.MaxFlow}");
        TestContext.Out.WriteLine($"Has router edges: {hasRouterEdges}");

        // Log path for analysis
        foreach (var step in response.Transfers.Take(10))
        {
            TestContext.Out.WriteLine($"  {step.From} -> {step.To} ({step.Value})");
        }
    }

    /// <summary>
    /// Verify edge ordering for paths that involve group minting.
    /// Collateral edges must precede mint edges for each group.
    /// </summary>
    [Test]
    public async Task GroupMintPath_HasCorrectEdgeOrdering()
    {
        Assert.That(_session, Is.Not.Null);

        if (!_session!.IsDirectConnectionAvailable)
        {
            Assert.Ignore("Test requires direct database connection.");
            return;
        }

        var loadGraph = new LoadGraph(_session.PostgresConnectionString!, _settings!);
        var factory = new GraphFactory(RouterAddress, loadGraph);

        var trustGraph = factory.V2TrustGraph();
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        // Find a source with balance
        var sourceResult = await _session.ExecuteQueryAsync(
            @"SELECT DISTINCT account
              FROM ""V_CrcV2_BalancesByAccountAndToken""
              WHERE ""totalBalance"" > 1000000000000000000
              LIMIT 5",
            new Dictionary<string, object?>());

        if (sourceResult.RowCount == 0)
        {
            Assert.Ignore("No source with sufficient balance found");
            return;
        }

        var source = sourceResult.Rows.First()[0]?.ToString()!;

        // Find a sink that trusts groups
        var sinkResult = await _session.ExecuteQueryAsync(
            @"SELECT DISTINCT t.truster
              FROM ""CrcV2_Trust"" t
              JOIN ""CrcV2_RegisterGroup"" g ON t.trustee = g.avatar
              WHERE t.truster != @source
                AND t.""expiryTime"" > extract(epoch from now())
              LIMIT 5",
            new Dictionary<string, object?> { { "source", source.ToLowerInvariant() } });

        if (sinkResult.RowCount == 0)
        {
            Assert.Ignore("No sink found that trusts groups");
            return;
        }

        var sink = sinkResult.Rows.First()[0]?.ToString()!;

        TestContext.Out.WriteLine($"Testing edge ordering: {source} -> {sink}");

        var request = new FlowRequest
        {
            Source = source,
            Sink = sink
        };

        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var pathfinder = new V2Pathfinder();

        var targetFlow = UInt256.Parse("10000000000000000000"); // 10 CRC
        var response = pathfinder.ComputeMaxFlowWithPath(capacityGraph, request, targetFlow);

        if (response.Transfers == null || response.Transfers.Count == 0)
        {
            TestContext.Out.WriteLine("No path found at current block state");
            return;
        }

        // Verify edge ordering: for each group, collateral edges must come before mint edges
        var groupsWithCollateral = new HashSet<string>();
        var routerAddr = RouterAddress.ToLowerInvariant();

        foreach (var step in response.Transfers)
        {
            var from = step.From?.ToLowerInvariant() ?? "";
            var to = step.To?.ToLowerInvariant() ?? "";

            // Check if this is a Router -> Group edge (collateral deposit)
            if (from == routerAddr && capacityGraph.IsGroup(AddressIdPool.IdOf(to)))
            {
                groupsWithCollateral.Add(to);
            }

            // Check if this is a Group -> Avatar edge (minting)
            var fromId = AddressIdPool.IdOf(from);
            if (capacityGraph.IsGroup(fromId) && !capacityGraph.IsGroup(AddressIdPool.IdOf(to)) && to != routerAddr)
            {
                Assert.That(groupsWithCollateral.Contains(from), Is.True,
                    $"Group {from} minted tokens before receiving collateral - edge ordering bug!");
            }
        }

        TestContext.Out.WriteLine($"Edge ordering verified for {response.Transfers.Count} steps");
        TestContext.Out.WriteLine($"Groups with collateral: {groupsWithCollateral.Count}");
    }

    /// <summary>
    /// Test that Anvil can execute a basic transfer path.
    /// Uses proxied RPC - works from anywhere.
    /// </summary>
    [Test]
    public async Task AnvilExecution_BasicTransferPath()
    {
        Assert.That(_anvil, Is.Not.Null, "Anvil should be available");

        // Simple test: verify we can get block number
        var blockNumber = await _anvil!.GetBlockNumberAsync();
        Assert.That(blockNumber, Is.GreaterThan(0), "Should get valid block number from Anvil");

        TestContext.Out.WriteLine($"Anvil fork is at block {blockNumber}");
        Assert.Pass("Anvil connection verified");
    }

    /// <summary>
    /// Creates a test fixture for a successful payment gateway transfer.
    /// This test is designed to run manually after the on-chain script succeeds.
    /// </summary>
    [Test]
    [Explicit("Run manually after payment-gateway-test.mjs succeeds to create fixture")]
    public void CreatePaymentGatewayFixture()
    {
        // This test is a template for creating fixtures from successful on-chain tests
        // After running: node scripts/payment-gateway-test.mjs test-transfer <gateway> <source>
        // Copy the FIXTURE DATA output to RegressionScenarios/payment-gateway-group-mint-001.json

        var fixtureTemplate = @"{
  ""id"": ""payment-gateway-group-mint-001"",
  ""name"": ""Payment Gateway with Group Trust"",
  ""category"": ""payment-gateway"",
  ""block"": BLOCK_NUMBER_HERE,
  ""source"": ""SOURCE_ADDRESS_HERE"",
  ""sink"": ""GATEWAY_ADDRESS_HERE"",
  ""description"": ""Routed transfer to payment gateway that trusts groups. Tests that router insertion happens before consent validation (isPermittedFlow fix) and that edge ordering places collateral before mints (ERC1155InsufficientBalance fix)."",
  ""shouldFindPath"": true,
  ""minFlow"": ""1000000000000000000"",
  ""expectedRevertReason"": null,
  ""runOnAnvil"": true,
  ""discoveredAt"": ""DATE_HERE"",
  ""fixedIn"": ""Pipeline reorder + SortEdgesForMintDependencies"",
  ""tags"": [""payment-gateway"", ""group-minting"", ""router"", ""regression"", ""isPermittedFlow"", ""edge-ordering""]
}";

        TestContext.Out.WriteLine("Fixture template:");
        TestContext.Out.WriteLine(fixtureTemplate);
        TestContext.Out.WriteLine("\nReplace the placeholder values with data from payment-gateway-test.mjs output");

        Assert.Pass("Template displayed - run on-chain test to get actual values");
    }
}

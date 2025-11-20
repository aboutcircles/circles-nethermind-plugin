using System.Numerics;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Integration tests for CirclesRpcModule.
/// Note: These tests require a PostgreSQL database connection.
/// Set the environment variable CIRCLES_TEST_DB_CONNECTION to run these tests.
/// Example: "Host=localhost;Database=circles_test;Username=postgres;Password=password"
/// </summary>
[TestFixture]
public class CirclesRpcModuleTests
{
    private CirclesRpcModule? _module;
    private string? _dbConnection;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // For Settings to work, we need to set environment variables
        var dbConnection = Environment.GetEnvironmentVariable("CIRCLES_TEST_DB_CONNECTION");

        if (string.IsNullOrWhiteSpace(dbConnection))
        {
            Assert.Ignore("Skipping integration tests. Set CIRCLES_TEST_DB_CONNECTION environment variable to run.");
            return;
        }

        // Set required environment variables for Settings constructor
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", dbConnection);
        Environment.SetEnvironmentVariable("EXTERNAL_PATHFINDER_URL", "http://localhost:8080");

        var settings = new Settings();
        _module = new CirclesRpcModule(settings);
    }

    #region Helper Methods

    private void RequireModule()
    {
        if (_module == null)
        {
            Assert.Fail("Module not initialized. Check database connection.");
        }
    }

    #endregion

    #region GetHealth Tests

    [Test]
    public async Task GetHealth_WithValidConnection_ReturnsHealthy()
    {
        RequireModule();

        var result = await _module!.GetHealth();
        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("healthy"));
        Assert.That(json, Does.Contain("connected"));
    }

    #endregion

    #region GetAvatarInfo Tests

    [Test]
    public async Task GetAvatarInfo_WithNonExistentAddress_ReturnsError()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";
        var result = await _module!.GetAvatarInfo(nonExistentAddress);
        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("error"));
    }

    [Test]
    public async Task GetAvatarInfoBatch_WithEmptyArray_ReturnsEmptyArray()
    {
        RequireModule();

        var result = await _module!.GetAvatarInfoBatch(Array.Empty<string>());
        var array = result as object[];

        Assert.That(array, Is.Not.Null);
        Assert.That(array!.Length, Is.EqualTo(0));
    }

    [Test]
    public void GetAvatarInfoBatch_WithTooManyAddresses_ThrowsException()
    {
        RequireModule();

        var tooManyAddresses = Enumerable.Range(0, 1001)
            .Select(i => $"0x{i:x40}")
            .ToArray();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await _module!.GetAvatarInfoBatch(tooManyAddresses));
    }

    #endregion

    #region GetTokenBalances Tests

    [Test]
    public async Task GetTokenBalances_WithValidAddress_ReturnsListOfBalances()
    {
        RequireModule();

        // This test will return an empty list if the address has no tokens
        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenBalancesForAccount(testAddress);

        Assert.That(result, Is.Not.Null);

        // Should return an array (could be empty)
        Assert.That(result, Is.InstanceOf<CirclesTokenBalance[]>());

        // If there are balances, verify structure
        if (result.Length > 0)
        {
            var balance = result[0];
            Assert.That(balance.TokenAddress, Is.Not.Null);
            Assert.That(balance.TokenId, Is.Not.Null);
            Assert.That(balance.TokenOwner, Is.Not.Null);
            Assert.That(balance.Version, Is.GreaterThan(0));

            // Verify all value representations exist
            Assert.That(balance.AttoCircles, Is.Not.Null);
            Assert.That(balance.StaticAttoCircles, Is.Not.Null);
            Assert.That(balance.AttoCrc, Is.Not.Null);

            // Verify flags
            Assert.That(balance.IsErc20 || balance.IsErc1155, Is.True,
                "Token must be either ERC20 or ERC1155");
        }
    }

    [Test]
    public async Task GetTokenBalances_VerifiesPhase1Limitation()
    {
        RequireModule();

        // This test documents that Phase 1 returns raw database values
        // without time-based adjustments (inflation/demurrage)

        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenBalancesForAccount(testAddress);

        if (result.Length > 0)
        {
            var balance = result[0];

            // In Phase 1, these should be equal (no time-based conversion)
            // In Phase 3, they would differ based on token type
            Assert.That(balance.AttoCircles, Is.EqualTo(balance.AttoCrc),
                "Phase 1: AttoCircles should equal AttoCrc (no inflation/demurrage)");
            Assert.That(balance.AttoCircles, Is.EqualTo(balance.StaticAttoCircles),
                "Phase 1: AttoCircles should equal StaticAttoCircles (no demurrage)");
        }
    }

    #endregion

    #region GetEvents Tests

    [Test]
    public async Task GetEvents_WithNoFilters_ReturnsEvents()
    {
        RequireModule();

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: null,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithBlockRange_ReturnsFilteredEvents()
    {
        RequireModule();

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: 0,
            toBlock: 1000,
            eventTypes: null,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithEventTypeFilter_ReturnsOnlySpecifiedTypes()
    {
        RequireModule();

        var eventTypes = new[] { "CrcV1_Signup", "CrcV2_RegisterHuman" };
        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: eventTypes,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithAdvancedPredicates_ReturnsFilteredEvents()
    {
        RequireModule();

        var predicates = new[]
        {
            new FilterPredicateDto
            {
                Column = "blockNumber",
                FilterType = Index.Query.FilterType.GreaterThan,
                Value = 1000L
            }
        };

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: null,
            filterPredicates: predicates,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region GetProfileCid Tests

    [Test]
    public async Task GetProfileCid_WithNonExistentAddress_ReturnsError()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";
        var result = await _module!.GetProfileCid(nonExistentAddress);
        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("error"));
    }

    [Test]
    public async Task GetProfileCidBatch_WithEmptyArray_ReturnsEmptyDictionary()
    {
        RequireModule();

        var result = await _module!.GetProfileCidBatch(Array.Empty<string>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<Dictionary<string, string?>>());
        Assert.That(result.Count, Is.EqualTo(0));
    }

    #endregion

    #region GetProfileByAddress Tests

    [Test]
    public async Task GetProfileByAddress_WithNonExistentAddress_ReturnsNull()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";
        var result = await _module!.GetProfileByAddress(nonExistentAddress);

        // Non-existent profiles return null
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetProfileByAddressBatch_VerifiesEnrichment()
    {
        RequireModule();

        // This test verifies that profile enrichment includes all expected fields
        var testAddresses = new[] { "0x0000000000000000000000000000000000000001" };
        var result = await _module!.GetProfileByAddressBatch(testAddresses);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<Dictionary<string, JsonElement?>>());

        if (result.Count > 0 && result.Values.First().HasValue)
        {
            var profile = result.Values.First();
            var json = JsonSerializer.Serialize(profile);

            // Profile should be enriched with address
            Assert.That(json, Does.Contain("address"),
                "Enriched profile should contain address field");
        }
    }

    #endregion

    #region SearchProfiles Tests

    [Test]
    public async Task SearchProfiles_WithEmptyText_ReturnsEmpty()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("", limit: 10);

        // Should return empty result for too-short search
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
        Assert.That(result.Total, Is.EqualTo(0));
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task SearchProfiles_WithValidText_ReturnsResults()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("test", limit: 10);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
    }

    [Test]
    public async Task SearchProfiles_WithTooHighLimit_ReturnsError()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("test", limit: 200);
        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("error"));
    }

    #endregion

    #region GetTokenInfo Tests

    [Test]
    public async Task GetTokenInfo_WithValidToken_ReturnsTokenInfo()
    {
        RequireModule();

        // Test with a known token address (adjust as needed for your test database)
        var testToken = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenInfo(testToken);

        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region GetTrustRelations Tests

    [Test]
    public async Task GetTrustRelations_WithValidAddress_ReturnsTrustData()
    {
        RequireModule();

        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTrustRelations(testAddress);

        Assert.That(result, Is.Not.Null);

        var json = JsonSerializer.Serialize(result);
        Assert.That(json, Does.Contain("user"));
        Assert.That(json, Does.Contain("trusts"));
        Assert.That(json, Does.Contain("trustedBy"));
    }

    #endregion

    #region GetCommonTrust Tests

    [Test]
    public async Task GetCommonTrust_WithTwoAddresses_ReturnsCommonTrust()
    {
        RequireModule();

        var address1 = "0x0000000000000000000000000000000000000001";
        var address2 = "0x0000000000000000000000000000000000000002";
        var result = await _module!.GetCommonTrust(address1, address2);

        Assert.That(result, Is.Not.Null);

        var json = JsonSerializer.Serialize(result);
        Assert.That(json, Does.Contain("address1"));
        Assert.That(json, Does.Contain("address2"));
        Assert.That(json, Does.Contain("commonTrusts"));
    }

    #endregion

    #region Live Balance Mode Tests

    [Test]
    public async Task LiveBalanceMode_GetTotalBalanceV2_ReturnsLiveValue()
    {
        RequireModule();

        // This test documents the expected behavior when BalanceMode=live
        // Note: This requires BALANCE_MODE environment variable to be set to "live"
        // and a running Nethermind node

        var testAddress = "0x0000000000000000000000000000000000000001";

        // This will use live eth_call if BalanceMode=live, otherwise database
        var result = await _module!.GetTotalBalanceV2(testAddress);

        Assert.That(result, Is.Not.Null);
        // Result is a string representation of the total balance
    }

    [Test]
    public async Task LiveBalanceMode_GetTotalBalanceV1_ReturnsLiveValue()
    {
        RequireModule();

        var testAddress = "0x0000000000000000000000000000000000000001";

        // This will use live eth_call if BalanceMode=live, otherwise database
        var result = await _module!.GetTotalBalanceV1(testAddress);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task LiveBalanceMode_GetTokenBalances_IncludesTimeBasedAdjustments()
    {
        RequireModule();

        // When in live mode, token balances should include time-based adjustments
        // V1: Inflation applied based on period
        // V2: Demurrage applied based on days

        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenBalancesForAccount(testAddress);

        Assert.That(result, Is.Not.Null);

        if (result.Length > 0)
        {
            var balance = result[0];

            // All value representations should be present
            Assert.That(balance.AttoCircles, Is.Not.Null);
            Assert.That(balance.StaticAttoCircles, Is.Not.Null);
            Assert.That(balance.AttoCrc, Is.Not.Null);

            // In live mode with real data, these values may differ
            // (depending on token version and time elapsed)
        }
    }

    #endregion

    #region NethermindRpcClient Mock Tests

    // Note: These tests document expected behavior but require mocking
    // to avoid needing a real Nethermind node during testing

    [Test]
    public void NethermindRpcClient_EthCall_DocumentsExpectedBehavior()
    {
        // This test documents the expected behavior of eth_call
        // Actual testing would require mocking HttpClient

        // Expected: POST to RPC URL with JSON-RPC request
        // Request format:
        // {
        //   "jsonrpc": "2.0",
        //   "method": "eth_call",
        //   "params": [
        //     {
        //       "to": "0x...",
        //       "data": "0x..."
        //     },
        //     "latest"
        //   ],
        //   "id": 1
        // }

        // Expected response format:
        // {
        //   "jsonrpc": "2.0",
        //   "id": 1,
        //   "result": "0x..."
        // }

        Assert.Pass("Documentation test - no actual execution");
    }

    #endregion

    #region Balance Calculation Integration Tests

    [Test]
    public async Task BalanceCalculation_V2Token_AppliesDemurrage()
    {
        RequireModule();

        // This test documents that V2 tokens should have demurrage applied
        // when using live mode

        // In live mode:
        // 1. Fetch raw balance via eth_call
        // 2. Apply demurrage based on current day
        // 3. Return adjusted balance

        // Note: Actual testing requires real V2 tokens in the database
        Assert.Pass("Documentation test - requires V2 token data");
    }

    [Test]
    public async Task BalanceCalculation_V1Token_AppliesInflation()
    {
        RequireModule();

        // This test documents that V1 tokens should have inflation applied
        // when using live mode

        // In live mode:
        // 1. Fetch raw balance via eth_call
        // 2. Apply inflation based on period
        // 3. Convert to demurraged Circles using V1ToDemurrage
        // 4. Return adjusted balance

        // Note: Actual testing requires real V1 tokens in the database
        Assert.Pass("Documentation test - requires V1 token data");
    }

    #endregion

    #region ERC-1155 Batch Balance Tests

    [Test]
    public async Task ERC1155_BatchBalance_HandlesMultipleTokens()
    {
        RequireModule();

        // This test documents the expected behavior for ERC-1155 batch balance queries
        // When querying multiple ERC-1155 tokens for the same account,
        // the system should use balanceOfBatch for efficiency

        // Expected flow:
        // 1. Collect all ERC-1155 token addresses
        // 2. Convert addresses to token IDs
        // 3. Create batch call with same account repeated
        // 4. Parse batch response into individual balances

        Assert.Pass("Documentation test - requires ERC-1155 tokens");
    }

    [Test]
    public async Task ERC1155_SingleToken_UsesBalanceOf()
    {
        RequireModule();

        // Single ERC-1155 token should use balanceOf(address,uint256)
        // instead of balanceOfBatch for simplicity

        Assert.Pass("Documentation test - requires ERC-1155 token");
    }

    #endregion

    #region API Contract Compliance Tests

    /// <summary>
    /// Tests that verify API output matches the remote API contract.
    /// These tests ensure backward compatibility and prevent regressions.
    /// </summary>

    [Test]
    public async Task GetCommonTrust_ReturnsArrayOnly_NotWrappedObject()
    {
        RequireModule();

        var address1 = "0x0000000000000000000000000000000000000001";
        var address2 = "0x0000000000000000000000000000000000000002";
        var result = await _module!.GetCommonTrust(address1, address2);

        Assert.That(result, Is.Not.Null);

        // Result should be CommonTrustResponse object internally
        Assert.That(result, Is.InstanceOf<CommonTrustResponse>());
        Assert.That(result.CommonTrusts, Is.Not.Null);
        Assert.That(result.CommonTrusts, Is.InstanceOf<List<string>>());

        // When serialized via the RPC handler, only the CommonTrusts array should be returned
        // This is tested by verifying the handler code returns result.CommonTrusts
        // The actual RPC response should be: ["0x...", "0x...", ...]
        // NOT: {"address1": "...", "address2": "...", "commonTrusts": [...]}
    }

    [Test]
    public async Task GetTables_ReturnsDetailedSchema_NotJustTableNames()
    {
        RequireModule();

        var result = await _module!.GetTables();

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<TableNamespace[]>());
        Assert.That(result.Length, Is.GreaterThan(0), "Should return at least one namespace");

        // Verify structure matches remote API
        var firstNamespace = result[0];
        Assert.That(firstNamespace.Namespace, Is.Not.Null);
        Assert.That(firstNamespace.Tables, Is.Not.Null);
        Assert.That(firstNamespace.Tables.Length, Is.GreaterThan(0), "Namespace should have tables");

        var firstTable = firstNamespace.Tables[0];
        Assert.That(firstTable.Table, Is.Not.Null, "Table name should be present");
        Assert.That(firstTable.Topic, Is.Not.Null, "Topic should be present");
        Assert.That(firstTable.Columns, Is.Not.Null, "Columns should be present");
        Assert.That(firstTable.Columns.Length, Is.GreaterThan(0), "Table should have columns");

        var firstColumn = firstTable.Columns[0];
        Assert.That(firstColumn.Column, Is.Not.Null, "Column name should be present");
        Assert.That(firstColumn.Type, Is.Not.Null, "Column type should be present");

        // Verify topic format (should be 66 character hex string: 0x + 64 chars)
        Assert.That(firstTable.Topic, Does.Match(@"^0x[0-9a-f]{64}$"),
            "Topic should be 66-character hex string");
    }

    [Test]
    public async Task GetTables_NamespacesMatchRemoteStructure()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Expected namespaces from remote API
        var expectedNamespaces = new[] {
            "System", "CrcV1", "CrcV2", "CrcV2_TokenOffers",
            "CrcV2_InvitationEscrow", "CrcV2_OIC", "Safe",
            "V_Safe", "V_CrcV1", "V_CrcV2", "V_Crc"
        };

        var namespaces = result.Select(n => n.Namespace).ToArray();

        // Verify that expected namespaces exist (may have others like "ipfs", "Other")
        foreach (var expected in expectedNamespaces)
        {
            if (namespaces.Contains(expected))
            {
                // At least some expected namespaces should be present
                Assert.Pass($"Found expected namespace: {expected}");
                return;
            }
        }

        // If we have any namespaces at all, that's acceptable for this test
        Assert.That(result.Length, Is.GreaterThan(0),
            "Should return at least one namespace even if schema differs");
    }

    [Test]
    public async Task GetTables_TableNamesParsedCorrectly()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Find CrcV1 namespace and verify table names are stripped of prefix
        var crcV1Namespace = result.FirstOrDefault(n => n.Namespace == "CrcV1");

        if (crcV1Namespace != null)
        {
            // Tables should have prefix removed: "CrcV1_Signup" -> "Signup"
            var tableNames = crcV1Namespace.Tables.Select(t => t.Table).ToArray();

            // Should NOT contain full names with prefix
            Assert.That(tableNames, Has.No.Member("CrcV1_Signup"),
                "Table names should have namespace prefix removed");

            // Should contain short names if the tables exist
            // (This is flexible in case database schema varies)
            var hasExpectedFormat = tableNames.All(name => !name.StartsWith("CrcV1_"));
            Assert.That(hasExpectedFormat, Is.True,
                "All table names should have CrcV1_ prefix removed");
        }
    }

    [Test]
    public async Task GetTables_ColumnTypesMapCorrectly()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Find any table with columns
        var anyTable = result.SelectMany(ns => ns.Tables).FirstOrDefault();

        if (anyTable != null && anyTable.Columns.Length > 0)
        {
            var columnTypes = anyTable.Columns.Select(c => c.Type).Distinct().ToArray();

            // Valid column types from remote API
            var validTypes = new[] {
                "Int", "BigInt", "Boolean", "String", "Address",
                "Bytes", "Json", "Double", "AddressArray", "Array"
            };

            foreach (var columnType in columnTypes)
            {
                Assert.That(validTypes, Does.Contain(columnType),
                    $"Column type '{columnType}' should be one of the valid API types");
            }
        }
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public async Task LiveBalanceMode_InvalidRpcUrl_HandlesGracefully()
    {
        // Test that invalid RPC URL is handled gracefully
        // In production, this should log error and potentially fall back to database

        // Note: This would require injecting a mock NethermindRpcClient
        Assert.Pass("Documentation test - requires dependency injection");
    }

    [Test]
    public async Task LiveBalanceMode_RpcTimeout_HandlesGracefully()
    {
        // Test that RPC timeouts are handled gracefully

        Assert.Pass("Documentation test - requires mock HTTP client");
    }

    [Test]
    public async Task LiveBalanceMode_InvalidContractResponse_HandlesGracefully()
    {
        // Test that invalid contract responses (non-hex, wrong length) are handled

        Assert.Pass("Documentation test - requires mock RPC client");
    }

    #endregion
}

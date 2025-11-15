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
        var result = await _module!.GetTokenBalances(testAddress);

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
        var result = await _module!.GetTokenBalances(testAddress);

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
}

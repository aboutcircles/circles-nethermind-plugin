using Circles.Common.TestUtils;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Integration tests that verify cache service queries work against real Circles data.
/// These tests run by default but skip gracefully when TEST_ENV_URL is not set.
/// </summary>
[Trait("Category", "RequiresTestEnv")]
public class SnapshotIntegrationTests : IAsyncLifetime
{
    private static readonly bool TestEnvConfigured =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL"));

    private const long TestBlock = 43193632;
    private TestEnvironmentClient? _session;

    public async Task InitializeAsync()
    {
        if (!TestEnvConfigured)
        {
            return;
        }

        try
        {
            var health = await TestEnvironmentClient.GetHealthAsync();
            if (health?.Status != "healthy")
            {
                return;
            }

            var exists = await TestEnvironmentClient.BlockExistsAsync(TestBlock);
            if (!exists)
            {
                return;
            }

            _session = await TestEnvironmentClient.CreateSessionAsync(TestBlock, ttl: "15m");
        }
        catch
        {
            // Swallow - tests will skip
        }
    }

    public async Task DisposeAsync()
    {
        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    [SkipIfNoTestEnvFact]
    public async Task V1Avatars_QueryReturnsData()
    {
        _session.Should().NotBeNull("Session should be created in setup");

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""user"", token
            FROM ""CrcV1_Signup""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V1 signups");
    }

    [SkipIfNoTestEnvFact]
    public async Task V2Avatars_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT avatar, inviter
            FROM ""CrcV2_RegisterHuman""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V2 registrations");
    }

    [SkipIfNoTestEnvFact]
    public async Task V2Groups_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""group"", name, symbol
            FROM ""CrcV2_RegisterGroup""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        // Groups may or may not exist at this block
        result.Should().NotBeNull();
    }

    [SkipIfNoTestEnvFact]
    public async Task V1TrustRelations_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""user"", ""canSendTo""
            FROM ""CrcV1_Trust""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V1 trust relations");
    }

    [SkipIfNoTestEnvFact]
    public async Task V2TrustRelations_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT truster, trustee, ""expiryTime""
            FROM ""CrcV2_Trust""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V2 trust relations");
    }

    [SkipIfNoTestEnvFact]
    public async Task V1Transfers_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""from"", ""to"", amount
            FROM ""CrcV1_Transfer""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V1 transfers");
    }

    [SkipIfNoTestEnvFact]
    public async Task V2TransferSingle_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""from"", ""to"", id, value
            FROM ""CrcV2_TransferSingle""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        result.RowCount.Should().BeGreaterThan(0, "Should have V2 transfers");
    }

    [SkipIfNoTestEnvFact]
    public async Task BalancesView_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT account, ""tokenId"", balance
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE balance > 0
            LIMIT 10");

        result.RowCount.Should().BeGreaterThan(0, "Should have positive balances");
    }

    [SkipIfNoTestEnvFact]
    public async Task TrustRelationsView_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT truster, trustee
            FROM ""V_CrcV2_TrustRelations""
            LIMIT 10");

        result.RowCount.Should().BeGreaterThan(0, "Should have trust relations in view");
    }

    [SkipIfNoTestEnvFact]
    public async Task AvatarsView_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT avatar, version, ""avatarType""
            FROM ""V_Crc_Avatars""
            LIMIT 10");

        result.RowCount.Should().BeGreaterThan(0, "Should have avatars in view");
    }

    [SkipIfNoTestEnvFact]
    public async Task ProfileMetadata_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        // V2 profile metadata
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT avatar, ""metadataDigest""
            FROM ""CrcV2_UpdateMetadataDigest""
            WHERE ""blockNumber"" <= @block
            LIMIT 10",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        // May or may not have data depending on block
        result.Should().NotBeNull();
    }

    [SkipIfNoTestEnvFact]
    public async Task LargeNumericValues_HandledCorrectly()
    {
        _session.Should().NotBeNull();

        // Query V2 transfers which have large token IDs and values
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT id, value
            FROM ""CrcV2_TransferSingle""
            WHERE ""blockNumber"" <= @block
            LIMIT 5",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        // Should not throw when handling large NUMERIC values
        result.RowCount.Should().BeGreaterThanOrEqualTo(0);

        foreach (var row in result.Rows)
        {
            var id = row[0]?.ToString();
            var value = row[1]?.ToString();

            // Token IDs and values should be readable as strings
            id.Should().NotBeNullOrEmpty("Token ID should be readable");
            value.Should().NotBeNullOrEmpty("Value should be readable");
        }
    }

    [SkipIfNoTestEnvFact]
    public async Task BlockFiltering_RespectsSessionBlock()
    {
        _session.Should().NotBeNull();

        // Query max block from System_Block
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT MAX(""blockNumber"") FROM ""System_Block""");

        var maxBlock = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);

        // Due to block filtering, should not exceed session block
        maxBlock.Should().BeLessThanOrEqualTo(TestBlock,
            $"Max block should be limited to session block {TestBlock}");
    }

    [SkipIfNoTestEnvFact]
    public async Task KnownAddress_HasExpectedData()
    {
        _session.Should().NotBeNull();

        // Known address from mint-along-path bug investigation
        var knownAddress = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c";

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*) FROM ""V_Crc_Avatars"" WHERE avatar = @addr",
            new Dictionary<string, object?> { ["addr"] = knownAddress });

        var count = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
        count.Should().Be(1, $"Known address {knownAddress} should exist in avatars");
    }

    [SkipIfNoTestEnvFact]
    public async Task Erc20Wrappers_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterERC20Wrapper""
            WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        // May or may not have wrappers at this block
        result.Should().NotBeNull();
    }

    [SkipIfNoTestEnvFact]
    public async Task GroupMemberships_QueryReturnsData()
    {
        _session.Should().NotBeNull();

        // CrcV2_ApprovalForAll is used for group memberships
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_ApprovalForAll""
            WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        // May or may not have data depending on block
        result.Should().NotBeNull();
    }
}

/// <summary>
/// Custom xUnit Fact attribute that skips when TEST_ENV_URL is not set.
/// </summary>
internal sealed class SkipIfNoTestEnvFactAttribute : FactAttribute
{
    private static readonly bool TestEnvConfigured =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_ENV_URL"));

    public SkipIfNoTestEnvFactAttribute()
    {
        if (!TestEnvConfigured)
        {
            Skip = "TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run integration tests.";
        }
    }
}

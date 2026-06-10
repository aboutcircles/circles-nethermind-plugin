using Circles.Common.TestUtils;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using NUnit.Framework;

namespace Circles.Index.Query.Tests;

/// <summary>
/// Integration tests that verify query generation works against real Circles data.
/// These tests run by default but skip gracefully when TEST_ENV_URL is not set.
/// </summary>
[TestFixture]
[Category("Snapshot")]
[Category("RequiresTestEnv")]
public class SnapshotIntegrationTests
{
    private const long TestBlock = 43193632;
    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var testEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
        if (string.IsNullOrEmpty(testEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set. Set to https://staging.circlesubi.network/test-env to run integration tests.");
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
    public async Task FilterEquals_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test that a simple equals filter works against real schema
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_Trust""
            WHERE truster = @truster",
            new Dictionary<string, object?>
            {
                ["truster"] = "0xde374ece6fa50e781e81aac78e811b33d16912c7"
            });

        Assert.That(result, Is.Not.Null);
        TestContext.Out.WriteLine($"Trust count for address: {result.Rows.FirstOrDefault()?[0]}");
    }

    [Test]
    public async Task FilterGreaterThan_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test greater than filter
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_Trust""
            WHERE ""blockNumber"" > @block",
            new Dictionary<string, object?>
            {
                ["block"] = TestBlock - 10000
            });

        Assert.That(result, Is.Not.Null);
        var count = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
        Assert.That(count, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"Trust events after block {TestBlock - 10000}: {count}");
    }

    [Test]
    public async Task FilterIn_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test IN filter with multiple addresses
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT truster, trustee
            FROM ""CrcV2_Trust""
            WHERE truster IN (@addr1, @addr2)
            LIMIT 10",
            new Dictionary<string, object?>
            {
                ["addr1"] = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                ["addr2"] = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c"
            });

        Assert.That(result, Is.Not.Null);
        TestContext.Out.WriteLine($"Trust relations found: {result.RowCount}");
    }

    [Test]
    public async Task OrderBy_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test ORDER BY
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT ""blockNumber"", truster, trustee
            FROM ""CrcV2_Trust""
            ORDER BY ""blockNumber"" DESC
            LIMIT 5");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.RowCount, Is.LessThanOrEqualTo(5));

        // Verify descending order
        long prevBlock = long.MaxValue;
        foreach (var row in result.Rows)
        {
            var blockNum = Convert.ToInt64(row[0]);
            Assert.That(blockNum, Is.LessThanOrEqualTo(prevBlock), "Should be in descending order");
            prevBlock = blockNum;
        }

        TestContext.Out.WriteLine($"Latest trust at block: {result.Rows.FirstOrDefault()?[0]}");
    }

    [Test]
    public async Task SelectDistinct_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test DISTINCT
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT DISTINCT truster
            FROM ""CrcV2_Trust""
            LIMIT 100");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.RowCount, Is.GreaterThan(0));

        // Verify uniqueness
        var trusters = result.Rows.Select(r => r[0]?.ToString()).ToList();
        Assert.That(trusters.Distinct().Count(), Is.EqualTo(trusters.Count), "All trusters should be unique");

        TestContext.Out.WriteLine($"Distinct trusters found: {result.RowCount}");
    }

    [Test]
    public async Task ComplexQuery_WithMultipleFiltersAndJoin_ExecutesSuccessfully()
    {
        Assert.That(_session, Is.Not.Null);

        // Test a more complex query pattern
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT t.truster, t.trustee, t.""blockNumber""
            FROM ""CrcV2_Trust"" t
            WHERE t.""blockNumber"" >= @minBlock
              AND t.""blockNumber"" <= @maxBlock
            ORDER BY t.""blockNumber"" DESC
            LIMIT 20",
            new Dictionary<string, object?>
            {
                ["minBlock"] = TestBlock - 50000,
                ["maxBlock"] = TestBlock
            });

        Assert.That(result, Is.Not.Null);
        TestContext.Out.WriteLine($"Trust events in range: {result.RowCount}");

        foreach (var row in result.Rows.Take(3))
        {
            TestContext.Out.WriteLine($"  Block {row[2]}: {row[0]} -> {row[1]}");
        }
    }

    [Test]
    public async Task V1Tables_AreQueryable()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*) FROM ""CrcV1_Signup"" WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        var count = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
        Assert.That(count, Is.GreaterThan(0), "Should have V1 signups");
        TestContext.Out.WriteLine($"V1 signups: {count}");
    }

    [Test]
    public async Task V2Tables_AreQueryable()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT COUNT(*) FROM ""CrcV2_RegisterHuman"" WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        var count = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
        Assert.That(count, Is.GreaterThan(0), "Should have V2 registrations");
        TestContext.Out.WriteLine($"V2 RegisterHuman events: {count}");
    }

    [Test]
    public async Task SystemBlock_IsAccessible()
    {
        Assert.That(_session, Is.Not.Null);

        var result = await _session!.ExecuteQueryAsync(@"
            SELECT MAX(""blockNumber"") FROM ""System_Block""");

        var maxBlock = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
        Assert.That(maxBlock, Is.GreaterThanOrEqualTo(TestBlock), "Should have blocks indexed up to test block");
        TestContext.Out.WriteLine($"Max indexed block: {maxBlock}");
    }

    [Test]
    public async Task Views_AreQueryable()
    {
        Assert.That(_session, Is.Not.Null);

        // Test that materialized views work
        var views = new[]
        {
            "V_CrcV2_TrustRelations",
            "V_CrcV2_BalancesByAccountAndToken",
            "V_Crc_Avatars"
        };

        foreach (var view in views)
        {
            var result = await _session!.ExecuteQueryAsync($"SELECT COUNT(*) FROM \"{view}\"");
            var count = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);
            TestContext.Out.WriteLine($"{view}: {count} rows");
            Assert.That(count, Is.GreaterThanOrEqualTo(0), $"View {view} should be queryable");
        }
    }

    [Test]
    public async Task BlockFiltering_RespectsSessionBlock()
    {
        Assert.That(_session, Is.Not.Null);

        // Query without explicit block filter should respect session's max block
        var result = await _session!.ExecuteQueryAsync(@"
            SELECT MAX(""blockNumber"") FROM ""CrcV2_Trust""");

        var maxBlock = Convert.ToInt64(result.Rows.FirstOrDefault()?[0] ?? 0);

        // Should not exceed session block due to circles.max_block_number
        Assert.That(maxBlock, Is.LessThanOrEqualTo(TestBlock),
            $"Max block {maxBlock} should not exceed session block {TestBlock}");

        TestContext.Out.WriteLine($"Max trust block: {maxBlock} (session limit: {TestBlock})");
    }
}

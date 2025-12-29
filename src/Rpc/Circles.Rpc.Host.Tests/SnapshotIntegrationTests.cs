using Circles.Common.TestUtils;
using Circles.Index.Common;
using Circles.Index.Postgres;
using Npgsql;
using NUnit.Framework;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Snapshot-based integration tests for RPC database queries using the Circles Test Environment.
/// These tests validate SQL queries and data access patterns against historical blockchain state.
///
/// The tests automatically use the query proxy when direct database connection is not available
/// (e.g., when running against staging.circlesubi.network/test-env from external network).
///
/// To run these tests:
/// 1. Start the test environment locally:
///    docker compose -f docker/docker-compose.test-environment.yml up -d
/// 2. Run: dotnet test --filter "Category=Snapshot"
///
/// Or set TEST_ENV_URL to point to staging:
///    TEST_ENV_URL=https://staging.circlesubi.network/test-env dotnet test --filter "Category=Snapshot"
/// </summary>
[TestFixture]
[Category("Snapshot")]
public class RpcSnapshotTests
{
    [Test]
    public async Task HealthCheck_TestEnvironmentIsAvailable()
    {
        var health = await TestEnvironmentClient.GetHealthAsync();

        Assert.That(health, Is.Not.Null);
        Assert.That(health!.Status, Is.EqualTo("healthy"));
    }

    [Test]
    public async Task CurrentBlock_IsIndexed()
    {
        var currentBlock = await TestEnvironmentClient.GetCurrentBlockAsync();

        Assert.That(currentBlock, Is.GreaterThan(0), "Database should have indexed blocks");
    }
}

/// <summary>
/// Tests for RPC query functionality using historical database state.
/// These tests verify SQL queries work correctly with block-filtered data.
/// Uses proxy endpoint when direct DB connection is unavailable.
/// </summary>
[TestFixture]
[Category("Snapshot")]
public class RpcQuerySnapshotTests
{
    // Block 43193632 = state at the time of the mint-along-path bug
    private const long TestBlock = 43193632;

    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
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
            Assert.Ignore($"Block {TestBlock} not indexed. Run with production database snapshot.");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(
            TestBlock,
            features: ["db"],
            ttl: "15m");
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
    public async Task QueryEvents_AtBlock_ReturnsData()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterHuman""
            WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        TestContext.WriteLine($"CrcV2_RegisterHuman events up to block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have RegisterHuman events at this block");
    }

    [Test]
    public async Task QueryTrust_AtBlock_ReturnsTrustRelations()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""V_CrcV2_TrustRelations""");

        TestContext.WriteLine($"V_CrcV2_TrustRelations at block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have trust relations at this block");
    }

    [Test]
    public async Task QueryBalances_AtBlock_ReturnsTokenBalances()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(DISTINCT ""account"")
            FROM ""V_CrcV2_BalancesByAccountAndToken""");

        TestContext.WriteLine($"Unique accounts with V2 balances at block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have accounts with balances");
    }

    [Test]
    public async Task QueryGroups_AtBlock_ReturnsGroupData()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterGroup""
            WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        TestContext.WriteLine($"Registered groups at block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThanOrEqualTo(0));
    }

    private async Task<long> ExecuteScalarLongAsync(string sql, Dictionary<string, object?>? parameters = null)
    {
        if (_session!.IsDirectConnectionAvailable)
        {
            await using var conn = await _session.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (parameters != null)
            {
                foreach (var (name, value) in parameters)
                {
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
            }
            return (long)(await cmd.ExecuteScalarAsync() ?? 0);
        }
        else
        {
            var result = await _session.ExecuteScalarAsync(sql, parameters);
            return Convert.ToInt64(result ?? 0);
        }
    }
}

/// <summary>
/// Tests for database schema and view availability using test environment.
/// Verifies that all expected tables and views exist and are queryable.
/// </summary>
[TestFixture]
[Category("Snapshot")]
public class SchemaValidationSnapshotTests
{
    private const long TestBlock = 43193632;
    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
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
            Assert.Ignore($"Block {TestBlock} not indexed.");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(TestBlock);
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
    public async Task AllSchemaTables_AreQueryable()
    {
        Assert.That(_session, Is.Not.Null);

        var schema = new CompositeDatabaseSchema(SchemaProvider.AllSchemas.ToArray());
        var failures = new List<string>();

        foreach (var (ns, table) in schema.Tables.Keys)
        {
            var fullTableName = $"{ns}_{table}";
            var sql = $"SELECT 1 FROM \"{fullTableName}\" LIMIT 1";

            try
            {
                if (_session!.IsDirectConnectionAvailable)
                {
                    await using var conn = await _session.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    await cmd.ExecuteScalarAsync();
                }
                else
                {
                    await _session.ExecuteScalarAsync(sql);
                }
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                failures.Add($"{fullTableName}: does not exist");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("42P01"))
            {
                failures.Add($"{fullTableName}: does not exist");
            }
            catch (Exception ex)
            {
                failures.Add($"{fullTableName}: {ex.Message}");
            }
        }

        if (failures.Any())
        {
            TestContext.WriteLine($"Schema validation failures:\n{string.Join("\n", failures)}");
        }

        var criticalTables = new[]
        {
            "System_Block",
            "CrcV1_Signup", "CrcV1_Trust", "CrcV1_Transfer",
            "CrcV2_RegisterHuman", "CrcV2_Trust", "CrcV2_TransferSingle"
        };

        foreach (var table in criticalTables)
        {
            Assert.That(failures.Any(f => f.StartsWith(table)), Is.False,
                $"Critical table {table} should exist");
        }
    }

    [Test]
    public async Task CriticalViews_AreQueryable()
    {
        Assert.That(_session, Is.Not.Null);

        var criticalViews = new[]
        {
            "V_CrcV2_TrustRelations",
            "V_CrcV2_BalancesByAccountAndToken",
            "V_Crc_Avatars"
        };

        foreach (var view in criticalViews)
        {
            var sql = $"SELECT 1 FROM \"{view}\" LIMIT 1";

            try
            {
                if (_session!.IsDirectConnectionAvailable)
                {
                    await using var conn = await _session.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    await cmd.ExecuteScalarAsync();
                }
                else
                {
                    await _session.ExecuteScalarAsync(sql);
                }
                TestContext.WriteLine($"View {view}: OK");
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                Assert.Fail($"Critical view {view} does not exist");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("42P01"))
            {
                Assert.Fail($"Critical view {view} does not exist");
            }
        }
    }
}

/// <summary>
/// Tests for specific avatar queries at historical blocks.
/// These tests use known addresses from bug investigations.
/// </summary>
[TestFixture]
[Category("Snapshot")]
public class AvatarQuerySnapshotTests
{
    private const long BugReproBlock = 43193632;

    // Known addresses from mint-along-path bug investigation
    private const string KnownSource = "0xa571627c6ce9c7faebd2ae67cc9212787ad77f0c";
    private const string KnownSink = "0xb3129372e52b910b6994eaef77bbc1892ea48779";

    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
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

        var exists = await TestEnvironmentClient.BlockExistsAsync(BugReproBlock);
        if (!exists)
        {
            Assert.Ignore($"Block {BugReproBlock} not indexed.");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(BugReproBlock);
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
    public async Task KnownSource_ExistsInDatabase()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""V_Crc_Avatars""
            WHERE ""avatar"" = @address",
            new Dictionary<string, object?> { ["address"] = KnownSource });

        Assert.That(count, Is.EqualTo(1), $"Source address {KnownSource} should exist in avatars");
    }

    [Test]
    public async Task KnownSink_ExistsInDatabase()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""V_Crc_Avatars""
            WHERE ""avatar"" = @address",
            new Dictionary<string, object?> { ["address"] = KnownSink });

        Assert.That(count, Is.EqualTo(1), $"Sink address {KnownSink} should exist in avatars");
    }

    [Test]
    public async Task QueryTrustBetweenKnownAddresses_ReturnsTrustPath()
    {
        Assert.That(_session, Is.Not.Null);

        var sql = @"
            SELECT ""truster"", ""trustee""
            FROM ""V_CrcV2_TrustRelations""
            WHERE ""truster"" = @source OR ""trustee"" = @source
               OR ""truster"" = @sink OR ""trustee"" = @sink
            LIMIT 100";
        var parameters = new Dictionary<string, object?>
        {
            ["source"] = KnownSource,
            ["sink"] = KnownSink
        };

        var result = await _session!.ExecuteQueryAsync(sql, parameters, maxRows: 100);

        TestContext.WriteLine($"Trust relations involving source/sink: {result.RowCount}");
        foreach (var row in result.Rows.Take(10))
        {
            TestContext.WriteLine($"  {row[0]} trusts {row[1]}");
        }

        Assert.That(result.RowCount, Is.GreaterThan(0),
            "Should have trust relations for known addresses");
    }

    [Test]
    public async Task QueryBalances_ForKnownSource_ReturnsTokens()
    {
        Assert.That(_session, Is.Not.Null);

        var sql = @"
            SELECT ""tokenId"", ""balance""
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""account"" = @address
            LIMIT 50";
        var parameters = new Dictionary<string, object?> { ["address"] = KnownSource };

        var result = await _session!.ExecuteQueryAsync(sql, parameters, maxRows: 50);

        TestContext.WriteLine($"Balances for {KnownSource}: {result.RowCount} tokens");
        foreach (var row in result.Rows.Take(5))
        {
            TestContext.WriteLine($"  {row[0]}: {row[1]}");
        }

        Assert.That(result.RowCount, Is.GreaterThan(0),
            "Known source should have token balances at this block");
    }

    private async Task<long> ExecuteScalarLongAsync(string sql, Dictionary<string, object?>? parameters = null)
    {
        if (_session!.IsDirectConnectionAvailable)
        {
            await using var conn = await _session.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (parameters != null)
            {
                foreach (var (name, value) in parameters)
                {
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
            }
            return (long)(await cmd.ExecuteScalarAsync() ?? 0);
        }
        else
        {
            var result = await _session.ExecuteScalarAsync(sql, parameters);
            return Convert.ToInt64(result ?? 0);
        }
    }
}

/// <summary>
/// Tests for transaction history queries at historical blocks.
/// </summary>
[TestFixture]
[Category("Snapshot")]
public class TransactionHistorySnapshotTests
{
    private const long TestBlock = 43193632;
    private TestEnvironmentClient? _session;

    [OneTimeSetUp]
    public async Task Setup()
    {
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
            Assert.Ignore($"Block {TestBlock} not indexed.");
        }

        _session = await TestEnvironmentClient.CreateSessionAsync(TestBlock);
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
    public async Task QueryTransfers_AtBlock_ReturnsTransferHistory()
    {
        Assert.That(_session, Is.Not.Null);

        var sql = @"
            SELECT ""blockNumber"", ""transactionHash"", ""from"", ""to""
            FROM ""CrcV2_TransferSingle""
            WHERE ""blockNumber"" <= @block
            ORDER BY ""blockNumber"" DESC
            LIMIT 10";
        var parameters = new Dictionary<string, object?> { ["block"] = TestBlock };

        var result = await _session!.ExecuteQueryAsync(sql, parameters, maxRows: 10);

        TestContext.WriteLine($"Recent V2 transfers (up to block {TestBlock}):");
        foreach (var row in result.Rows)
        {
            var from = row[2]?.ToString() ?? "";
            var to = row[3]?.ToString() ?? "";
            TestContext.WriteLine($"  Block {row[0]}: {from[..Math.Min(10, from.Length)]}... -> {to[..Math.Min(10, to.Length)]}...");
        }

        Assert.That(result.RowCount, Is.GreaterThan(0), "Should have transfer history");
    }

    [Test]
    public async Task QueryV1Transfers_AtBlock_ReturnsLegacyHistory()
    {
        Assert.That(_session, Is.Not.Null);

        var count = await ExecuteScalarLongAsync(@"
            SELECT COUNT(*)
            FROM ""CrcV1_Transfer""
            WHERE ""blockNumber"" <= @block",
            new Dictionary<string, object?> { ["block"] = TestBlock });

        TestContext.WriteLine($"V1 transfers up to block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have V1 transfer history");
    }

    private async Task<long> ExecuteScalarLongAsync(string sql, Dictionary<string, object?>? parameters = null)
    {
        if (_session!.IsDirectConnectionAvailable)
        {
            await using var conn = await _session.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (parameters != null)
            {
                foreach (var (name, value) in parameters)
                {
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
            }
            return (long)(await cmd.ExecuteScalarAsync() ?? 0);
        }
        else
        {
            var result = await _session.ExecuteScalarAsync(sql, parameters);
            return Convert.ToInt64(result ?? 0);
        }
    }
}

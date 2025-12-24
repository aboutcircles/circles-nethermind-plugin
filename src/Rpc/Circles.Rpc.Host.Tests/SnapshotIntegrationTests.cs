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

        await using var conn = await _session!.OpenConnectionAsync();

        // Query CrcV2_RegisterHuman events - should have registered humans at this block
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterHuman""
            WHERE ""blockNumber"" <= @block", conn);
        cmd.Parameters.AddWithValue("block", TestBlock);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        TestContext.WriteLine($"CrcV2_RegisterHuman events up to block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have RegisterHuman events at this block");
    }

    [Test]
    public async Task QueryTrust_AtBlock_ReturnsTrustRelations()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        // Query V_CrcV2 trust view - should have trust relations
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""V_CrcV2_TrustRelations""", conn);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        TestContext.WriteLine($"V_CrcV2_TrustRelations at block {TestBlock}: {count}");
        Assert.That(count, Is.GreaterThan(0), "Should have trust relations at this block");
    }

    [Test]
    public async Task QueryBalances_AtBlock_ReturnsTokenBalances()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        // Query aggregated balances view
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(DISTINCT ""account"")
            FROM ""V_CrcV2_BalancesByAccountAndToken""", conn);

        var accountCount = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        TestContext.WriteLine($"Unique accounts with V2 balances at block {TestBlock}: {accountCount}");
        Assert.That(accountCount, Is.GreaterThan(0), "Should have accounts with balances");
    }

    [Test]
    public async Task QueryGroups_AtBlock_ReturnsGroupData()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        // Query registered groups
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""CrcV2_RegisterGroup""
            WHERE ""blockNumber"" <= @block", conn);
        cmd.Parameters.AddWithValue("block", TestBlock);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        TestContext.WriteLine($"Registered groups at block {TestBlock}: {count}");
        // Groups may or may not exist at this block
        Assert.That(count, Is.GreaterThanOrEqualTo(0));
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

        await using var conn = await _session!.OpenConnectionAsync();

        foreach (var (ns, table) in schema.Tables.Keys)
        {
            var fullTableName = $"{ns}_{table}";
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"SELECT 1 FROM \"{fullTableName}\" LIMIT 1", conn);
                await cmd.ExecuteScalarAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01") // table does not exist
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

        // Allow some missing tables (views may not exist in all environments)
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

        await using var conn = await _session!.OpenConnectionAsync();

        foreach (var view in criticalViews)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"SELECT 1 FROM \"{view}\" LIMIT 1", conn);
                await cmd.ExecuteScalarAsync();
                TestContext.WriteLine($"View {view}: OK");
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
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

        await using var conn = await _session!.OpenConnectionAsync();

        // Check if the source address exists as a registered human or organization
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""V_Crc_Avatars""
            WHERE ""avatar"" = @address", conn);
        cmd.Parameters.AddWithValue("address", KnownSource);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        Assert.That(count, Is.EqualTo(1), $"Source address {KnownSource} should exist in avatars");
    }

    [Test]
    public async Task KnownSink_ExistsInDatabase()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""V_Crc_Avatars""
            WHERE ""avatar"" = @address", conn);
        cmd.Parameters.AddWithValue("address", KnownSink);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        Assert.That(count, Is.EqualTo(1), $"Sink address {KnownSink} should exist in avatars");
    }

    [Test]
    public async Task QueryTrustBetweenKnownAddresses_ReturnsTrustPath()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        // Check for direct or indirect trust relations
        await using var cmd = new NpgsqlCommand(@"
            SELECT ""truster"", ""trustee"", ""expiryTime""
            FROM ""V_CrcV2_TrustRelations""
            WHERE ""truster"" = @source OR ""trustee"" = @source
               OR ""truster"" = @sink OR ""trustee"" = @sink
            LIMIT 100", conn);
        cmd.Parameters.AddWithValue("source", KnownSource);
        cmd.Parameters.AddWithValue("sink", KnownSink);

        var trustRelations = new List<(string truster, string trustee)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            trustRelations.Add((reader.GetString(0), reader.GetString(1)));
        }

        TestContext.WriteLine($"Trust relations involving source/sink: {trustRelations.Count}");
        foreach (var (truster, trustee) in trustRelations.Take(10))
        {
            TestContext.WriteLine($"  {truster} trusts {trustee}");
        }

        // At least one trust relation should exist for the path to be computable
        Assert.That(trustRelations.Count, Is.GreaterThan(0),
            "Should have trust relations for known addresses");
    }

    [Test]
    public async Task QueryBalances_ForKnownSource_ReturnsTokens()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT ""tokenId"", ""balance""
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""account"" = @address
            LIMIT 50", conn);
        cmd.Parameters.AddWithValue("address", KnownSource);

        var balances = new List<(string tokenId, decimal balance)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            balances.Add((reader.GetString(0), reader.GetDecimal(1)));
        }

        TestContext.WriteLine($"Balances for {KnownSource}: {balances.Count} tokens");
        foreach (var (tokenId, balance) in balances.Take(5))
        {
            TestContext.WriteLine($"  {tokenId}: {balance}");
        }

        // Source should have some tokens to transfer
        Assert.That(balances.Count, Is.GreaterThan(0),
            "Known source should have token balances at this block");
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

        await using var conn = await _session!.OpenConnectionAsync();

        // Query recent transfers
        await using var cmd = new NpgsqlCommand(@"
            SELECT ""blockNumber"", ""transactionHash"", ""from"", ""to"", ""value""
            FROM ""CrcV2_TransferSingle""
            WHERE ""blockNumber"" <= @block
            ORDER BY ""blockNumber"" DESC
            LIMIT 10", conn);
        cmd.Parameters.AddWithValue("block", TestBlock);

        var transfers = new List<(long block, string txHash, string from, string to)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transfers.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        TestContext.WriteLine($"Recent V2 transfers (up to block {TestBlock}):");
        foreach (var (block, txHash, from, to) in transfers)
        {
            TestContext.WriteLine($"  Block {block}: {from.Substring(0, 10)}... -> {to.Substring(0, 10)}...");
        }

        Assert.That(transfers.Count, Is.GreaterThan(0), "Should have transfer history");
    }

    [Test]
    public async Task QueryV1Transfers_AtBlock_ReturnsLegacyHistory()
    {
        Assert.That(_session, Is.Not.Null);

        await using var conn = await _session!.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)
            FROM ""CrcV1_Transfer""
            WHERE ""blockNumber"" <= @block", conn);
        cmd.Parameters.AddWithValue("block", TestBlock);

        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);

        TestContext.WriteLine($"V1 transfers up to block {TestBlock}: {count}");
        // V1 should have historical transfers
        Assert.That(count, Is.GreaterThan(0), "Should have V1 transfer history");
    }
}

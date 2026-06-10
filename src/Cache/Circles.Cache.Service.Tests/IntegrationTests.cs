using System.Numerics;
using Circles.Cache.Service;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Integration tests using Testcontainers to spin up a real PostgreSQL instance.
/// These tests verify the actual database queries work correctly with real data.
/// </summary>
public class IntegrationTests : IAsyncLifetime
{
    internal static readonly bool DockerTestsEnabled = ComputeDockerTestsEnabled();

    /// <summary>
    /// RUN_CACHE_INTEGRATION_TESTS=true/false forces the gate; when unset (or set to any
    /// other value), the tests run whenever a Docker endpoint is detectable (these tests
    /// need only Docker, not staging).
    /// </summary>
    private static bool ComputeDockerTestsEnabled()
    {
        var env = Environment.GetEnvironmentVariable("RUN_CACHE_INTEGRATION_TESTS");
        if (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;

        return File.Exists("/var/run/docker.sock")
               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))
               || File.Exists(Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                   ".docker", "run", "docker.sock"));
    }

    private PostgreSqlContainer? _postgres;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!DockerTestsEnabled)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("circles_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Create test schema
        await CreateTestSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!DockerTestsEnabled)
        {
            return;
        }

        if (_postgres != null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private async Task CreateTestSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Create tables matching the real schema
        var sql = @"
            CREATE TABLE ""System_Block"" (
                ""blockNumber"" BIGINT PRIMARY KEY,
                ""timestamp"" BIGINT,
                ""blockHash"" TEXT NOT NULL,
                ""eventCounts"" TEXT
            );

            CREATE TABLE ""CrcV1_Signup"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""user"" TEXT NOT NULL,
                ""token"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV1_Transfer"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""tokenAddress"" TEXT NOT NULL,
                ""from"" TEXT NOT NULL,
                ""to"" TEXT NOT NULL,
                amount NUMERIC NOT NULL
            );

            CREATE TABLE ""CrcV1_OrganizationSignup"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""organization"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV1_Trust"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""canSendTo"" TEXT NOT NULL,
                ""user"" TEXT NOT NULL,
                ""limit"" NUMERIC NOT NULL
            );

            CREATE TABLE ""CrcV1_UpdateMetadataDigest"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                avatar TEXT NOT NULL,
                ""metadataDigest"" BYTEA NOT NULL
            );

            CREATE TABLE ""CrcV2_RegisterHuman"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""avatar"" TEXT NOT NULL,
                ""inviter"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV2_RegisterOrganization"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""organization"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV2_RegisterGroup"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""group"" TEXT NOT NULL,
                name TEXT NOT NULL,
                mint TEXT NOT NULL,
                symbol TEXT NOT NULL
            );

            CREATE TABLE ""CrcV2_ERC20WrapperDeployed"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                avatar TEXT NOT NULL,
                ""erc20Wrapper"" TEXT NOT NULL,
                ""circlesType"" INTEGER NOT NULL
            );

            CREATE TABLE ""CrcV2_TransferSingle"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""operator"" TEXT NOT NULL,
                ""from"" TEXT NOT NULL,
                ""to"" TEXT NOT NULL,
                id NUMERIC NOT NULL,
                value NUMERIC NOT NULL,
                ""tokenAddress"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV2_TransferBatch"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""operator"" TEXT NOT NULL,
                ""from"" TEXT NOT NULL,
                ""to"" TEXT NOT NULL,
                value NUMERIC NOT NULL,
                ""tokenAddress"" TEXT NOT NULL
            );

            CREATE TABLE ""CrcV2_Erc20WrapperTransfer"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""tokenAddress"" TEXT NOT NULL,
                ""from"" TEXT NOT NULL,
                ""to"" TEXT NOT NULL,
                amount NUMERIC NOT NULL
            );

            CREATE TABLE ""CrcV2_UpdateMetadataDigest"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                avatar TEXT NOT NULL,
                ""metadataDigest"" BYTEA NOT NULL
            );

            CREATE TABLE ""CrcV2_RegisterShortName"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                avatar TEXT NOT NULL,
                ""shortName"" NUMERIC NOT NULL
            );

            CREATE TABLE ""CrcV2_SetAdvancedUsageFlag"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                avatar TEXT NOT NULL,
                flag BYTEA NOT NULL
            );

            CREATE TABLE ""CrcV2_Trust"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                truster TEXT NOT NULL,
                trustee TEXT NOT NULL,
                ""expiryTime"" NUMERIC NOT NULL
            );

            CREATE OR REPLACE VIEW ""V_CrcV2_TrustRelations"" AS
            SELECT truster, trustee, ""expiryTime""
            FROM ""CrcV2_Trust"";
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [RequiresDockerFact]
    public async Task PostgresNumeric_ShouldReadAsBigInteger()
    {
        // Arrange - Insert a very large NUMERIC value (larger than decimal.MaxValue)
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // This value divided by 10^18 must be larger than decimal.MaxValue (≈ 7.9 × 10^28)
        // So we need value > 7.9 × 10^46. Using 10^47 gives divided = 10^29 > decimal.MaxValue
        var largeValue = "100000000000000000000000000000000000000000000000"; // 10^47

        var insertSql = @"
            INSERT INTO ""CrcV1_Transfer""
            (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"",
             ""tokenAddress"", ""from"", ""to"", amount)
            VALUES (1000, 12345, 0, 0, '0xhash', '0xtoken', '0xfrom', '0xto', @amount)
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("amount", BigInteger.Parse(largeValue));
        await insertCmd.ExecuteNonQueryAsync();

        // Act - Read it back using GetFieldValue<BigInteger>
        var selectSql = @"SELECT amount FROM ""CrcV1_Transfer"" WHERE ""blockNumber"" = 1000";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();
        var amountBig = reader.GetFieldValue<BigInteger>(0);

        // Assert
        amountBig.Should().Be(BigInteger.Parse(largeValue));

        // This proves that GetDecimal would overflow
        var divisor = BigInteger.Parse("1000000000000000000");
        var divided = amountBig / divisor;
        (divided > (BigInteger)decimal.MaxValue).Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task PostgresNumeric_GetDecimal_ShouldThrow_ForLargeValues()
    {
        // Arrange - Insert a very large NUMERIC value
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var largeValue = "100000000000000000000000000000"; // 10^29

        var insertSql = @"
            INSERT INTO ""CrcV1_Transfer""
            (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"",
             ""tokenAddress"", ""from"", ""to"", amount)
            VALUES (1001, 12345, 0, 0, '0xhash2', '0xtoken', '0xfrom', '0xto', @amount)
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("amount", BigInteger.Parse(largeValue));
        await insertCmd.ExecuteNonQueryAsync();

        // Act & Assert - GetDecimal should throw for values > decimal.MaxValue
        var selectSql = @"SELECT amount FROM ""CrcV1_Transfer"" WHERE ""blockNumber"" = 1001";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();

        // This demonstrates why GetDecimal is dangerous for NUMERIC columns
        var act = () => reader.GetDecimal(0);
        act.Should().Throw<OverflowException>();
    }

    [RequiresDockerFact]
    public async Task PostgresBigint_ShouldReadAsInt64()
    {
        // Arrange
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var insertSql = @"
            INSERT INTO ""System_Block"" (""blockNumber"", ""timestamp"", ""blockHash"", ""eventCounts"")
            VALUES (@blockNumber, @timestamp, '0xhash', '{}')
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("blockNumber", 36789012L);
        insertCmd.Parameters.AddWithValue("timestamp", 1735689600L);
        await insertCmd.ExecuteNonQueryAsync();

        // Act
        var selectSql = @"SELECT ""blockNumber"", ""timestamp"" FROM ""System_Block"" WHERE ""blockNumber"" = 36789012";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();
        var blockNumber = reader.GetInt64(0);
        var timestamp = reader.GetInt64(1);

        // Assert
        blockNumber.Should().Be(36789012L);
        timestamp.Should().Be(1735689600L);
    }

    [RequiresDockerFact]
    public async Task PostgresText_ShouldReadAsString()
    {
        // Arrange
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";

        var insertSql = @"
            INSERT INTO ""CrcV1_Signup""
            (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", ""user"", ""token"")
            VALUES (1000, 12345, 0, 0, '0xhash', @user, @token)
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("user", address);
        insertCmd.Parameters.AddWithValue("token", token);
        await insertCmd.ExecuteNonQueryAsync();

        // Act
        var selectSql = @"SELECT ""user"", ""token"" FROM ""CrcV1_Signup"" WHERE ""blockNumber"" = 1000";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();
        var userResult = reader.GetString(0);
        var tokenResult = reader.GetString(1);

        // Assert
        userResult.Should().Be(address);
        tokenResult.Should().Be(token);
    }

    [RequiresDockerFact]
    public async Task V2TransferSingle_ShouldHandleLargeTokenId()
    {
        // Arrange - V2 token IDs are uint256 (very large numbers)
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Real V2 token ID (avatar address as uint256)
        var tokenId = BigInteger.Parse("1390849295786071768276380950238675083608645509734");
        var value = BigInteger.Parse("1000000000000000000"); // 1 token in wei

        var insertSql = @"
            INSERT INTO ""CrcV2_TransferSingle""
            (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"",
             ""operator"", ""from"", ""to"", id, value, ""tokenAddress"")
            VALUES (2000, 12345, 0, 0, '0xhash', '0xop', '0xfrom', '0xto', @id, @value, '0xtoken')
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("id", tokenId);
        insertCmd.Parameters.AddWithValue("value", value);
        await insertCmd.ExecuteNonQueryAsync();

        // Act
        var selectSql = @"SELECT id, value FROM ""CrcV2_TransferSingle"" WHERE ""blockNumber"" = 2000";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();
        var idResult = reader.GetFieldValue<BigInteger>(0);
        var valueResult = reader.GetFieldValue<BigInteger>(1);

        // Assert
        idResult.Should().Be(tokenId);
        valueResult.Should().Be(value);

        // Token ID should convert to string correctly
        idResult.ToString().Should().Be("1390849295786071768276380950238675083608645509734");
    }

    [RequiresDockerFact]
    public async Task V2Trust_ExpiryTime_ShouldHandleLargeValues()
    {
        // Arrange - expiryTime can be max uint256 for "infinite" trust
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Use a very large expiry time
        var expiryTime = BigInteger.Parse("9999999999999999999");

        var insertSql = @"
            INSERT INTO ""CrcV2_Trust""
            (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"",
             truster, trustee, ""expiryTime"")
            VALUES (2000, 12345, 0, 0, '0xhash', '0xtruster', '0xtrustee', @expiryTime)
        ";

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("expiryTime", expiryTime);
        await insertCmd.ExecuteNonQueryAsync();

        // Act
        var selectSql = @"SELECT ""expiryTime"" FROM ""CrcV2_Trust"" WHERE ""blockNumber"" = 2000";
        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        await using var reader = await selectCmd.ExecuteReaderAsync();

        await reader.ReadAsync();
        var expiryResult = reader.GetFieldValue<BigInteger>(0);

        // Assert
        expiryResult.Should().Be(expiryTime);

        // Should cap to long.MaxValue when converting
        long safeLong = expiryResult > long.MaxValue ? long.MaxValue : (long)expiryResult;
        safeLong.Should().Be(long.MaxValue);
    }

    [RequiresDockerFact]
    public async Task NotificationListener_V1Trust_UsesCanSendToAsTruster_AndStoresLimit()
    {
        const long block = 9100;
        var canSendTo = "0x00000000000000000000000000000000000000a1";
        var user = "0x00000000000000000000000000000000000000b1";

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            var insertSql = @"
                INSERT INTO ""CrcV1_Trust""
                (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", ""canSendTo"", ""user"", ""limit"")
                VALUES (@blockNumber, 12345, 0, 0, '0xv1trust', @canSendTo, @user, 70)";

            await using var insertCmd = new NpgsqlCommand(insertSql, conn);
            insertCmd.Parameters.AddWithValue("blockNumber", block);
            insertCmd.Parameters.AddWithValue("canSendTo", canSendTo);
            insertCmd.Parameters.AddWithValue("user", user);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 8);
        var caches = new CacheContainer(rollbackCapacity: 8);
        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var listener = new TestableNotificationListenerService(settings, state, caches, dataSource);

        await listener.ProcessRangeAsync(block, block, CancellationToken.None);

        caches.V1TrustRelations.TryGetValue($"{canSendTo}:{user}", out var storedLimit).Should().BeTrue();
        storedLimit.Should().Be(70);
        caches.V1TrustRelations.ContainsKey($"{user}:{canSendTo}").Should().BeFalse();
    }

    [RequiresDockerFact]
    public async Task NotificationListener_V2Trust_GroupMembership_UsesGroupAsTruster()
    {
        const long block = 9200;
        var group = "0x00000000000000000000000000000000000000c1";
        var includedMember = "0x00000000000000000000000000000000000000d1";
        var excludedMember = "0x00000000000000000000000000000000000000e1";

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            var insertSql = @"
                INSERT INTO ""CrcV2_Trust""
                (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", truster, trustee, ""expiryTime"")
                VALUES
                (@blockNumber, 12345, 0, 0, '0xv2trust1', @group, @includedMember, 999999),
                (@blockNumber, 12345, 0, 1, '0xv2trust2', @excludedMember, @group, 999999)";

            await using var insertCmd = new NpgsqlCommand(insertSql, conn);
            insertCmd.Parameters.AddWithValue("blockNumber", block);
            insertCmd.Parameters.AddWithValue("group", group);
            insertCmd.Parameters.AddWithValue("includedMember", includedMember);
            insertCmd.Parameters.AddWithValue("excludedMember", excludedMember);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 8);
        var caches = new CacheContainer(rollbackCapacity: 8);
        caches.Groups.Add(1, group, ("Test Group", "0xmint", "TG"));
        // The trustee must be a registered avatar for ProcessV2TrustAsync to record the membership
        // (the registration gate added to NotificationListenerService.V2Trust.cs:68). Register the
        // included member so the group→member membership is upserted; the excluded member stays
        // unregistered and is excluded by trust direction (its only edge is member→group).
        caches.V2Avatars.Add(block, includedMember, ("CrcV2_RegisterHuman", 12345));

        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var listener = new TestableNotificationListenerService(settings, state, caches, dataSource);

        await listener.ProcessRangeAsync(block, block, CancellationToken.None);

        caches.GroupMemberships.ContainsKey($"{group}:{includedMember}").Should().BeTrue();
        caches.GroupMemberships.ContainsKey($"{group}:{excludedMember}").Should().BeFalse();
    }

    [RequiresDockerFact]
    public async Task CacheWarmup_V1Trust_WarmupUsesCanSendToAsTruster_AndHonorsToBlock()
    {
        const long olderBlock = 9300;
        const long newerBlock = 9301;
        var canSendTo = "0x00000000000000000000000000000000000000f1";
        var user = "0x00000000000000000000000000000000000000aa";

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            var insertSql = @"
                INSERT INTO ""CrcV1_Trust""
                (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", ""canSendTo"", ""user"", ""limit"")
                VALUES
                (@olderBlock, 12345, 0, 0, '0xwarmup1', @canSendTo, @user, 70),
                (@newerBlock, 12346, 0, 0, '0xwarmup2', @canSendTo, @user, 90)";

            await using var insertCmd = new NpgsqlCommand(insertSql, conn);
            insertCmd.Parameters.AddWithValue("olderBlock", olderBlock);
            insertCmd.Parameters.AddWithValue("newerBlock", newerBlock);
            insertCmd.Parameters.AddWithValue("canSendTo", canSendTo);
            insertCmd.Parameters.AddWithValue("user", user);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 8) { WarmupTargetBlock = olderBlock };
        var caches = new CacheContainer(rollbackCapacity: 8);

        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var warmup = new TestableCacheWarmupService(settings, state, caches, dataSource);

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await warmup.LoadTrustRelationsForTestAsync(conn, olderBlock, CancellationToken.None);
        }

        caches.V1TrustRelations.TryGetValue($"{canSendTo}:{user}", out var storedLimit).Should().BeTrue();
        storedLimit.Should().Be(70);
    }

    [RequiresDockerFact]
    public async Task WarmupSnapshotHelpers_KeepReadsConsistentAcrossWorkers()
    {
        await using var setupConn = new NpgsqlConnection(_connectionString);
        await setupConn.OpenAsync();

        const string setupSql = @"
            DROP TABLE IF EXISTS ""SnapshotProbe"";
            CREATE TABLE ""SnapshotProbe"" (
                id INTEGER PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO ""SnapshotProbe"" (id, value) VALUES (1, 'before');";

        await using (var setupCmd = new NpgsqlCommand(setupSql, setupConn))
        {
            await setupCmd.ExecuteNonQueryAsync();
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 8);
        var caches = new CacheContainer(rollbackCapacity: 8);
        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var warmup = new TestableCacheWarmupService(settings, state, caches, dataSource);

        string? worker1Value = null;
        string? worker2Value = null;

        await warmup.WithExportedSnapshotForTestAsync(async snapshotId =>
        {
            await warmup.WithSnapshotConnectionForTestAsync(snapshotId, async (conn, ct) =>
            {
                await using var cmd = new NpgsqlCommand("SELECT value FROM \"SnapshotProbe\" WHERE id = 1", conn);
                worker1Value = (string?)await cmd.ExecuteScalarAsync(ct);
            }, CancellationToken.None);

            await using (var mutateConn = new NpgsqlConnection(_connectionString))
            {
                await mutateConn.OpenAsync();
                await using var mutateCmd = new NpgsqlCommand("UPDATE \"SnapshotProbe\" SET value = 'after' WHERE id = 1", mutateConn);
                await mutateCmd.ExecuteNonQueryAsync();
            }

            await warmup.WithSnapshotConnectionForTestAsync(snapshotId, async (conn, ct) =>
            {
                await using var cmd = new NpgsqlCommand("SELECT value FROM \"SnapshotProbe\" WHERE id = 1", conn);
                worker2Value = (string?)await cmd.ExecuteScalarAsync(ct);
            }, CancellationToken.None);
        }, CancellationToken.None);

        await using var verifyConn = new NpgsqlConnection(_connectionString);
        await verifyConn.OpenAsync();
        await using var verifyCmd = new NpgsqlCommand("SELECT value FROM \"SnapshotProbe\" WHERE id = 1", verifyConn);
        var latestValue = (string?)await verifyCmd.ExecuteScalarAsync();

        worker1Value.Should().Be("before");
        worker2Value.Should().Be("before");
        latestValue.Should().Be("after");
    }

    [RequiresDockerFact]
    public async Task NotificationListener_V2_WrapperBlockBeforeErc1155Block_InSameRange_DoesNotThrowAndBalancesCorrect()
    {
        // Repro for the cross-handler block-monotonicity crash.
        // ProcessV2EventsAsync runs the ERC1155 transfer handler then the ERC20-wrapper transfer
        // handler against the SAME V2BalancesByAccountAndToken cache. When a wrapper transfer sits
        // at a LOWER block than the highest ERC1155 transfer block in the same range, the wrapper
        // handler's per-block flush calls RollbackCache.Add with a block < the cache's last block,
        // which throws "Block number must be monotonically increasing". Wrap/unwrap-heavy avatars
        // (whose 1155 and wrapper transfers interleave across blocks in one batch) trigger this.
        var avatar = "0x00000000000000000000000000000000000000a7";  // 1155 token owner
        var wrapper = "0x00000000000000000000000000000000000000b7"; // erc20 wrapper contract
        var holder = "0x00000000000000000000000000000000000000c7";
        var zero = "0x0000000000000000000000000000000000000000";
        const long wrapperBlock = 9400;  // LOWER
        const long erc1155Block = 9401;   // HIGHER
        var wei = BigInteger.Parse("5000000000000000000"); // 5 * 10^18 = 5 tokens

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();

            // Register the avatar (validates its 1155 token) and deploy a wrapper (validates the
            // wrapper token) at the lower block — both are processed before transfers in ProcessV2EventsAsync.
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_RegisterHuman"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""avatar"",""inviter"")
                  VALUES (@b,1,0,0,'0xrh',@a,'0x0')",
                ("b", wrapperBlock), ("a", avatar));
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_ERC20WrapperDeployed"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",avatar,""erc20Wrapper"",""circlesType"")
                  VALUES (@b,1,0,1,'0xwd',@a,@w,0)",
                ("b", wrapperBlock), ("a", avatar), ("w", wrapper));

            // Wrapper transfer (mint to holder) at the LOWER block.
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_Erc20WrapperTransfer"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""tokenAddress"",""from"",""to"",amount)
                  VALUES (@b,1,0,2,'0xwt',@w,@z,@h,@amt)",
                ("b", wrapperBlock), ("w", wrapper), ("z", zero), ("h", holder), ("amt", wei));

            // ERC1155 transfer (mint to holder) at the HIGHER block.
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_TransferSingle"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""operator"",""from"",""to"",id,value,""tokenAddress"")
                  VALUES (@b,1,0,0,'0xts','0xop',@z,@h,1,@amt,@t)",
                ("b", erc1155Block), ("z", zero), ("h", holder), ("amt", wei), ("t", avatar));
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 12);
        var caches = new CacheContainer(rollbackCapacity: 12);
        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var listener = new TestableNotificationListenerService(settings, state, caches, dataSource);

        // Before the fix this throws ArgumentException (monotonic violation). After: succeeds.
        var act = async () => await listener.ProcessRangeAsync(wrapperBlock, erc1155Block, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Both balances must be present and correct (disjoint token namespaces).
        caches.V2BalancesByAccountAndToken.TryGetValue($"{holder}:{avatar}", out var erc1155Bal).Should().BeTrue();
        erc1155Bal.Should().Be(5m);
        caches.V2BalancesByAccountAndToken.TryGetValue($"{holder}:{wrapper}", out var wrapperBal).Should().BeTrue();
        wrapperBal.Should().Be(5m);
    }

    [RequiresDockerFact]
    public async Task NotificationListener_V2_SenderDebits_AcrossInterleavedBlocks_BalancesCorrect()
    {
        // Exercises the merged path's SENDER-DEBIT branch (from != 0x0) for BOTH token namespaces
        // across interleaved blocks, with wrapper transfers at a lower block than 1155 transfers
        // (the monotonicity-crash trigger). Verifies the debit arithmetic the #74 drift concerns.
        var avatar = "0x00000000000000000000000000000000000000a8";  // 1155 token owner
        var wrapper = "0x00000000000000000000000000000000000000b8"; // erc20 wrapper contract
        var holder = "0x00000000000000000000000000000000000000c8";
        var recipient = "0x00000000000000000000000000000000000000d8";
        var zero = "0x0000000000000000000000000000000000000000";
        var ten = BigInteger.Parse("10000000000000000000"); // 10
        var three = BigInteger.Parse("3000000000000000000"); // 3
        var four = BigInteger.Parse("4000000000000000000"); // 4

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            // Registration + wrapper deploy at block 9499 (before transfers).
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_RegisterHuman"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""avatar"",""inviter"")
                  VALUES (9499,1,0,0,'0xrh',@a,'0x0')", ("a", avatar));
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_ERC20WrapperDeployed"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",avatar,""erc20Wrapper"",""circlesType"")
                  VALUES (9499,1,0,1,'0xwd',@a,@w,0)", ("a", avatar), ("w", wrapper));

            // Block 9500 mints: 1155 to holder, wrapper to holder.
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_TransferSingle"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""operator"",""from"",""to"",id,value,""tokenAddress"")
                  VALUES (9500,1,0,0,'0xm1','0xop',@z,@h,1,@v,@t)", ("z", zero), ("h", holder), ("v", ten), ("t", avatar));
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_Erc20WrapperTransfer"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""tokenAddress"",""from"",""to"",amount)
                  VALUES (9500,1,0,1,'0xmw',@w,@z,@h,@v)", ("w", wrapper), ("z", zero), ("h", holder), ("v", ten));

            // Block 9501 sends (DEBITS from holder): 1155 H->R (3), wrapper H->R (4).
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_TransferSingle"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""operator"",""from"",""to"",id,value,""tokenAddress"")
                  VALUES (9501,1,0,0,'0xs1','0xop',@h,@r,1,@v,@t)", ("h", holder), ("r", recipient), ("v", three), ("t", avatar));
            await ExecAsync(conn,
                @"INSERT INTO ""CrcV2_Erc20WrapperTransfer"" (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",""tokenAddress"",""from"",""to"",amount)
                  VALUES (9501,1,0,1,'0xsw',@w,@h,@r,@v)", ("w", wrapper), ("h", holder), ("r", recipient), ("v", four));
        }

        var settings = new CacheServiceSettings { PostgresConnectionString = _connectionString! };
        var state = new CacheServiceState(rollbackCapacity: 12);
        var caches = new CacheContainer(rollbackCapacity: 12);
        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        var listener = new TestableNotificationListenerService(settings, state, caches, dataSource);

        var act = async () => await listener.ProcessRangeAsync(9499, 9501, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Holder debited on both tokens; recipient credited; arithmetic correct across the merged pass.
        caches.V2BalancesByAccountAndToken.TryGetValue($"{holder}:{avatar}", out var hA).Should().BeTrue();
        hA.Should().Be(7m); // 10 - 3
        caches.V2BalancesByAccountAndToken.TryGetValue($"{recipient}:{avatar}", out var rA).Should().BeTrue();
        rA.Should().Be(3m);
        caches.V2BalancesByAccountAndToken.TryGetValue($"{holder}:{wrapper}", out var hW).Should().BeTrue();
        hW.Should().Be(6m); // 10 - 4
        caches.V2BalancesByAccountAndToken.TryGetValue($"{recipient}:{wrapper}", out var rW).Should().BeTrue();
        rW.Should().Be(4m);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class TestableNotificationListenerService : NotificationListenerService
    {
        public TestableNotificationListenerService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            NpgsqlDataSource readonlyDataSource)
            : base(NullLogger<NotificationListenerService>.Instance, settings, state, caches, readonlyDataSource)
        {
        }

        public Task ProcessRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
            => ProcessBlockRangeAsync(fromBlock, toBlock, ct);
    }

    private sealed class TestableCacheWarmupService : CacheWarmupService
    {
        public TestableCacheWarmupService(
            CacheServiceSettings settings,
            CacheServiceState state,
            CacheContainer caches,
            NpgsqlDataSource readonlyDataSource)
            : base(NullLogger<CacheWarmupService>.Instance, settings, state, caches, readonlyDataSource)
        {
        }

        public Task LoadTrustRelationsForTestAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
            => LoadTrustRelationsAsync(conn, toBlock, ct);

        public async Task WithExportedSnapshotForTestAsync(Func<string, Task> action, CancellationToken ct)
        {
            await using var snapshot = await CreateWarmupSnapshotAsync(ct);
            await action(snapshot.SnapshotId);
        }

        public Task WithSnapshotConnectionForTestAsync(
            string snapshotId,
            Func<NpgsqlConnection, CancellationToken, Task> action,
            CancellationToken ct)
            => WithSnapshotReadonlyConnectionAsync(snapshotId, action, ct);
    }
}

internal sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!IntegrationTests.DockerTestsEnabled)
        {
            Skip = "Docker endpoint not detected or RUN_CACHE_INTEGRATION_TESTS=false — " +
                   "set RUN_CACHE_INTEGRATION_TESTS=true to force these tests";
        }
    }
}

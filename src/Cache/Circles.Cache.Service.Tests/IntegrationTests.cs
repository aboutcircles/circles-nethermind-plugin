using Xunit;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Npgsql;
using System.Numerics;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Integration tests using Testcontainers to spin up a real PostgreSQL instance.
/// These tests verify the actual database queries work correctly with real data.
/// </summary>
public class IntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:15-alpine")
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

            CREATE TABLE ""CrcV2_RegisterHuman"" (
                ""blockNumber"" BIGINT NOT NULL,
                ""timestamp"" BIGINT NOT NULL,
                ""transactionIndex"" BIGINT NOT NULL,
                ""logIndex"" BIGINT NOT NULL,
                ""transactionHash"" TEXT NOT NULL,
                ""avatar"" TEXT NOT NULL,
                ""inviter"" TEXT NOT NULL
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
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
}

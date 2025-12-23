using Npgsql;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Repository for liquidity-related database queries.
/// Queries the Circles indexer database for Balancer vault balances,
/// group treasury data, and whale transfer detection.
/// </summary>
public class LiquidityRepository
{
    private readonly string _connectionString;
    private readonly ILogger<LiquidityRepository> _logger;

    // Balancer vault addresses on Gnosis Chain
    // V2 was compromised but still tracked for historical data
    private const string BalancerV2VaultAddress = "0xba12222222228d8ba445958a75a0704d566bf2c8";
    // V3 is the current active vault
    private const string BalancerV3VaultAddress = "0xba1333333333a1ba1108e8412f11850a5c319ba9";

    // Combined list for queries
    private static readonly string[] BalancerVaultAddresses = { BalancerV2VaultAddress, BalancerV3VaultAddress };

    // Whale threshold: 100 tokens (1e20 wei for 18-decimal tokens)
    private const decimal WhaleThreshold = 100_000_000_000_000_000_000m; // 1e20

    public LiquidityRepository(
        string connectionString,
        ILogger<LiquidityRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    #region Balancer Vault Balances

    /// <summary>
    /// Get current Balancer vault balances for all tokens with liquidity.
    /// </summary>
    public async Task<List<BalancerVaultBalance>> GetBalancerVaultBalancesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                b."tokenAddress",
                COALESCE(g.name, 'Unknown') as "tokenName",
                b.value as "balance"
            FROM "V_CrcV2_Erc20BalancerVaultBalance_1h" b
            LEFT JOIN "V_CrcV2_Groups" g ON g."erc20WrapperDemurraged" = b."tokenAddress"
            WHERE b."timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
              AND b.value > 0
            ORDER BY b.value DESC
            """;

        var results = new List<BalancerVaultBalance>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new BalancerVaultBalance
            {
                TokenAddress = reader.GetString(0),
                TokenName = reader.GetString(1),
                Balance = reader.GetDecimal(2)
            });
        }

        return results;
    }

    /// <summary>
    /// Get hourly balance changes for all tokens in Balancer vault.
    /// </summary>
    public async Task<List<BalancerVaultChange>> GetBalancerVaultHourlyChangesAsync(CancellationToken ct)
    {
        const string sql = """
            WITH current_hour AS (
                SELECT "tokenAddress", value as current_value
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
            ),
            previous_hour AS (
                SELECT "tokenAddress", value as previous_value
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") - interval '1 hour' FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
            )
            SELECT
                c."tokenAddress",
                c.current_value,
                COALESCE(p.previous_value, c.current_value) as previous_value,
                (c.current_value - COALESCE(p.previous_value, c.current_value)) as change
            FROM current_hour c
            LEFT JOIN previous_hour p USING ("tokenAddress")
            """;

        var results = new List<BalancerVaultChange>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new BalancerVaultChange
            {
                TokenAddress = reader.GetString(0),
                CurrentValue = reader.GetDecimal(1),
                PreviousValue = reader.GetDecimal(2),
                Change = reader.GetDecimal(3)
            });
        }

        return results;
    }

    /// <summary>
    /// Get 24-hour balance changes for all tokens.
    /// </summary>
    public async Task<List<BalancerVaultChange>> GetBalancerVault24hChangesAsync(CancellationToken ct)
    {
        const string sql = """
            WITH current_hour AS (
                SELECT "tokenAddress", value as current_value
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
            ),
            previous_day AS (
                SELECT "tokenAddress", value as previous_value
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") - interval '24 hours' FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
            )
            SELECT
                c."tokenAddress",
                c.current_value,
                COALESCE(p.previous_value, c.current_value) as previous_value,
                (c.current_value - COALESCE(p.previous_value, c.current_value)) as change
            FROM current_hour c
            LEFT JOIN previous_day p USING ("tokenAddress")
            """;

        var results = new List<BalancerVaultChange>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new BalancerVaultChange
            {
                TokenAddress = reader.GetString(0),
                CurrentValue = reader.GetDecimal(1),
                PreviousValue = reader.GetDecimal(2),
                Change = reader.GetDecimal(3)
            });
        }

        return results;
    }

    #endregion

    #region Z-Score Anomaly Detection

    /// <summary>
    /// Calculate z-scores for each token based on 30-day historical changes.
    /// Z-score = (current_change - mean) / std_dev
    /// </summary>
    public async Task<List<TokenZScore>> GetBalancerVaultZScoresAsync(CancellationToken ct)
    {
        const string sql = """
            WITH hourly_changes AS (
                SELECT
                    "tokenAddress",
                    "timestamp",
                    value - LAG(value) OVER (PARTITION BY "tokenAddress" ORDER BY "timestamp") as change
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" > NOW() - interval '30 days'
            ),
            stats AS (
                SELECT
                    "tokenAddress",
                    AVG(change) as mean_change,
                    STDDEV(change) as stddev_change
                FROM hourly_changes
                WHERE change IS NOT NULL
                GROUP BY "tokenAddress"
            ),
            latest AS (
                SELECT DISTINCT ON ("tokenAddress")
                    "tokenAddress",
                    change as latest_change
                FROM hourly_changes
                WHERE change IS NOT NULL
                ORDER BY "tokenAddress", "timestamp" DESC
            )
            SELECT
                l."tokenAddress",
                l.latest_change,
                s.mean_change,
                s.stddev_change,
                CASE
                    WHEN s.stddev_change > 0
                    THEN (l.latest_change - s.mean_change) / s.stddev_change
                    ELSE 0
                END as z_score
            FROM latest l
            JOIN stats s USING ("tokenAddress")
            WHERE s.stddev_change > 0
            ORDER BY z_score ASC
            """;

        var results = new List<TokenZScore>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new TokenZScore
            {
                TokenAddress = reader.GetString(0),
                LatestChange = reader.GetDecimal(1),
                MeanChange = reader.GetDouble(2),
                StdDevChange = reader.GetDouble(3),
                ZScore = reader.GetDouble(4)
            });
        }

        return results;
    }

    #endregion

    #region Whale Transfer Detection

    /// <summary>
    /// Get recent whale transfers to/from Balancer vaults (V2 and V3).
    /// </summary>
    public async Task<List<WhaleTransfer>> GetRecentWhaleTransfersAsync(int limit, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                "timestamp",
                "from",
                "to",
                "tokenAddress",
                amount,
                CASE
                    WHEN "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN 'deposit'
                    WHEN "from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN 'withdrawal'
                    ELSE 'transfer'
                END as direction
            FROM "CrcV2_Erc20WrapperTransfer"
            WHERE amount > @threshold
              AND ("from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}')
                   OR "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}'))
            ORDER BY "timestamp" DESC
            LIMIT @limit
            """;

        var results = new List<WhaleTransfer>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("threshold", WhaleThreshold);
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new WhaleTransfer
            {
                Timestamp = reader.GetInt64(0),
                From = reader.GetString(1),
                To = reader.GetString(2),
                TokenAddress = reader.GetString(3),
                Amount = reader.GetDecimal(4),
                Direction = reader.GetString(5)
            });
        }

        return results;
    }

    /// <summary>
    /// Get whale transfer summary by token (counts and volumes).
    /// </summary>
    public async Task<List<WhaleTransferSummary>> GetWhaleTransferSummaryAsync(TimeSpan window, CancellationToken ct)
    {
        var cutoffTimestamp = DateTimeOffset.UtcNow.Add(-window).ToUnixTimeSeconds();

        var sql = $"""
            SELECT
                "tokenAddress",
                CASE
                    WHEN "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN 'deposit'
                    WHEN "from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN 'withdrawal'
                END as direction,
                COUNT(*) as transfer_count,
                SUM(amount) as total_volume
            FROM "CrcV2_Erc20WrapperTransfer"
            WHERE amount > @threshold
              AND "timestamp" > @cutoff
              AND ("from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}')
                   OR "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}'))
            GROUP BY "tokenAddress", direction
            ORDER BY total_volume DESC
            """;

        var results = new List<WhaleTransferSummary>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("threshold", WhaleThreshold);
        cmd.Parameters.AddWithValue("cutoff", cutoffTimestamp);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new WhaleTransferSummary
            {
                TokenAddress = reader.GetString(0),
                Direction = reader.GetString(1),
                TransferCount = reader.GetInt64(2),
                TotalVolume = reader.GetDecimal(3)
            });
        }

        return results;
    }

    #endregion

    #region Group Treasury

    /// <summary>
    /// Get group treasury totals with metadata.
    /// </summary>
    public async Task<List<GroupTreasury>> GetGroupTreasuriesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                g."group",
                g.name,
                g.symbol,
                g."memberCount",
                COALESCE(SUM(vb.balance), 0) as "treasuryTotal"
            FROM "V_CrcV2_Groups" g
            LEFT JOIN "CrcV2_CreateVault" cv ON cv."group" = g."group"
            LEFT JOIN "V_CrcV2_GroupVaultBalancesByToken" vb ON vb.vault = cv.vault
            GROUP BY g."group", g.name, g.symbol, g."memberCount"
            HAVING COALESCE(SUM(vb.balance), 0) > 0
            ORDER BY "treasuryTotal" DESC
            """;

        var results = new List<GroupTreasury>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new GroupTreasury
            {
                GroupAddress = reader.GetString(0),
                Name = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
                Symbol = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MemberCount = reader.GetInt64(3),
                TreasuryTotal = reader.GetDecimal(4)
            });
        }

        return results;
    }

    #endregion
}

#region DTOs

public record BalancerVaultBalance
{
    public required string TokenAddress { get; init; }
    public required string TokenName { get; init; }
    public decimal Balance { get; init; }
}

public record BalancerVaultChange
{
    public required string TokenAddress { get; init; }
    public decimal CurrentValue { get; init; }
    public decimal PreviousValue { get; init; }
    public decimal Change { get; init; }
}

public record TokenZScore
{
    public required string TokenAddress { get; init; }
    public decimal LatestChange { get; init; }
    public double MeanChange { get; init; }
    public double StdDevChange { get; init; }
    public double ZScore { get; init; }
}

public record WhaleTransfer
{
    public long Timestamp { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string TokenAddress { get; init; }
    public decimal Amount { get; init; }
    public required string Direction { get; init; }
}

public record WhaleTransferSummary
{
    public required string TokenAddress { get; init; }
    public required string Direction { get; init; }
    public long TransferCount { get; init; }
    public decimal TotalVolume { get; init; }
}

public record GroupTreasury
{
    public required string GroupAddress { get; init; }
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public long MemberCount { get; init; }
    public decimal TreasuryTotal { get; init; }
}

#endregion

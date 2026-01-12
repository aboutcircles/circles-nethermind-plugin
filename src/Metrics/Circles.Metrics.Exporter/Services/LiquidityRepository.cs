using System.Numerics;
using Circles.Common;
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

    // Whale threshold: 1000 tokens (1e21 wei for 18-decimal tokens)
    private const decimal WhaleThreshold = 1_000_000_000_000_000_000_000m; // 1e21

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
        // Join through ERC20WrapperDeployed to get the avatar, then get the name from V_CrcV2_Avatars
        // For humans (who have no name), fall back to their short name from CrcV2_RegisterShortName
        // Short name is stored as numeric, needs Base58 conversion in C#
        const string sql = """
            SELECT
                b."tokenAddress",
                a.name as "avatarName",
                sn."shortName" as "shortNameNumeric",
                b.value as "balance"
            FROM "V_CrcV2_Erc20BalancerVaultBalance_1h" b
            LEFT JOIN "CrcV2_ERC20WrapperDeployed" w ON w."erc20Wrapper" = b."tokenAddress"
            LEFT JOIN "V_CrcV2_Avatars" a ON a.avatar = w.avatar
            LEFT JOIN LATERAL (
                SELECT "shortName"
                FROM "CrcV2_RegisterShortName" r
                WHERE r.avatar = w.avatar
                ORDER BY r."blockNumber" DESC
                LIMIT 1
            ) sn ON true
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
            var tokenAddress = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortNameNumeric = reader.IsDBNull(2) ? (BigInteger?)null : reader.GetFieldValue<BigInteger>(2);
            var balance = reader.GetDecimal(3);

            // Resolve token name: avatar name > short name (Base58) > truncated address
            string tokenName;
            if (!string.IsNullOrEmpty(avatarName))
            {
                tokenName = avatarName;
            }
            else if (shortNameNumeric.HasValue)
            {
                tokenName = shortNameNumeric.Value.ToBase58Btc();
            }
            else
            {
                tokenName = tokenAddress.Length > 10 ? tokenAddress[..10] + "..." : tokenAddress;
            }

            results.Add(new BalancerVaultBalance
            {
                TokenAddress = tokenAddress,
                TokenName = tokenName,
                Balance = balance
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
    /// Calculate z-scores and multi-factor drain detection data for each token.
    /// Returns z-score, balance percentage, rate acceleration for comprehensive anomaly detection.
    /// Limited to top 100 tokens by balance to avoid cardinality explosion.
    /// </summary>
    public async Task<List<TokenZScore>> GetBalancerVaultZScoresAsync(CancellationToken ct)
    {
        // Join through ERC20WrapperDeployed to get token names
        // For humans (who have no name), fall back to their short name from CrcV2_RegisterShortName
        // Extended to include balance percentage and rate acceleration for multi-factor detection
        const string sql = """
            WITH top_tokens AS (
                -- Limit to top 100 tokens by current balance to avoid metric cardinality explosion
                SELECT "tokenAddress", value as current_balance
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
                ORDER BY value DESC
                LIMIT 100
            ),
            hourly_changes AS (
                SELECT
                    b."tokenAddress",
                    b."timestamp",
                    b.value,
                    b.value - LAG(b.value) OVER (PARTITION BY b."tokenAddress" ORDER BY b."timestamp") as change
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h" b
                WHERE b."timestamp" > NOW() - interval '30 days'
                  AND b."tokenAddress" IN (SELECT "tokenAddress" FROM top_tokens)
            ),
            stats AS (
                SELECT
                    "tokenAddress",
                    AVG(change) as mean_change,
                    STDDEV(change) as stddev_change,
                    -- Average daily withdrawal (negative changes only, summed per day, then averaged)
                    ABS(AVG(CASE WHEN change < 0 THEN change ELSE 0 END) * 24) as avg_daily_withdrawal
                FROM hourly_changes
                WHERE change IS NOT NULL
                GROUP BY "tokenAddress"
            ),
            latest AS (
                SELECT DISTINCT ON ("tokenAddress")
                    "tokenAddress",
                    value as current_balance,
                    change as latest_change
                FROM hourly_changes
                WHERE change IS NOT NULL
                ORDER BY "tokenAddress", "timestamp" DESC
            )
            SELECT
                l."tokenAddress",
                a.name as "avatarName",
                sn."shortName" as "shortNameNumeric",
                l.latest_change,
                l.current_balance,
                s.mean_change,
                s.stddev_change,
                s.avg_daily_withdrawal,
                CASE
                    WHEN s.stddev_change > 0
                    THEN (l.latest_change - s.mean_change) / s.stddev_change
                    ELSE 0
                END as z_score,
                -- Balance percentage: what % of current balance is this change?
                CASE
                    WHEN l.current_balance > 0
                    THEN ABS(l.latest_change / l.current_balance) * 100
                    ELSE 0
                END as balance_percentage,
                -- Rate acceleration: how many times larger is this withdrawal vs avg daily?
                CASE
                    WHEN s.avg_daily_withdrawal > 0 AND l.latest_change < 0
                    THEN ABS(l.latest_change) / s.avg_daily_withdrawal
                    ELSE 0
                END as rate_acceleration
            FROM latest l
            JOIN stats s USING ("tokenAddress")
            LEFT JOIN "CrcV2_ERC20WrapperDeployed" w ON w."erc20Wrapper" = l."tokenAddress"
            LEFT JOIN "V_CrcV2_Avatars" a ON a.avatar = w.avatar
            LEFT JOIN LATERAL (
                SELECT "shortName"
                FROM "CrcV2_RegisterShortName" r
                WHERE r.avatar = w.avatar
                ORDER BY r."blockNumber" DESC
                LIMIT 1
            ) sn ON true
            WHERE s.stddev_change > 0
            ORDER BY z_score ASC
            """;

        var results = new List<TokenZScore>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Column indices:
        // 0: tokenAddress, 1: avatarName, 2: shortNameNumeric, 3: latest_change,
        // 4: current_balance, 5: mean_change, 6: stddev_change, 7: avg_daily_withdrawal,
        // 8: z_score, 9: balance_percentage, 10: rate_acceleration
        while (await reader.ReadAsync(ct))
        {
            var tokenAddress = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortNameNumeric = reader.IsDBNull(2) ? (BigInteger?)null : reader.GetFieldValue<BigInteger>(2);

            // Resolve token name: avatar name > short name (Base58) > truncated address
            string tokenName;
            if (!string.IsNullOrEmpty(avatarName))
            {
                tokenName = avatarName;
            }
            else if (shortNameNumeric.HasValue)
            {
                tokenName = shortNameNumeric.Value.ToBase58Btc();
            }
            else
            {
                tokenName = tokenAddress.Length > 10 ? tokenAddress[..10] + "..." : tokenAddress;
            }

            var latestChange = reader.GetDecimal(3);
            var currentBalance = reader.GetDecimal(4);

            results.Add(new TokenZScore
            {
                TokenAddress = tokenAddress,
                TokenName = tokenName,
                LatestChange = latestChange,
                LatestChangeCrc = latestChange / 1_000_000_000_000_000_000m, // Convert from wei to CRC
                CurrentBalanceCrc = currentBalance / 1_000_000_000_000_000_000m,
                MeanChange = reader.GetDouble(5),
                StdDevChange = reader.GetDouble(6),
                // Skip index 7 (avg_daily_withdrawal) - used in SQL calculation only
                ZScore = reader.GetDouble(8),
                BalancePercentage = reader.GetDouble(9),
                RateAcceleration = reader.GetDouble(10)
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

    #region Sybil Detection

    /// <summary>
    /// Get hourly withdrawal patterns for sybil attack detection.
    /// Detects coordinated small withdrawals to many unique recipients.
    /// </summary>
    public async Task<List<SybilDetectionResult>> GetHourlySybilMetricsAsync(CancellationToken ct)
    {
        var sql = $"""
            WITH hourly_withdrawals AS (
                SELECT
                    "from" as vault,
                    "to" as recipient,
                    amount / 1e18 as amount_crc,
                    "timestamp"
                FROM "CrcV2_Erc20WrapperTransfer"
                WHERE "from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}')
                  AND "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '1 hour')
            ),
            vault_stats AS (
                SELECT
                    vault,
                    COUNT(*) as withdrawal_count,
                    COUNT(DISTINCT recipient) as unique_recipients,
                    SUM(amount_crc) as total_withdrawn_crc,
                    AVG(amount_crc) as avg_withdrawal_crc
                FROM hourly_withdrawals
                GROUP BY vault
            ),
            historical_stats AS (
                -- Get 7-day baseline for comparison
                SELECT
                    "from" as vault,
                    COUNT(*) / (7.0 * 24) as avg_hourly_withdrawals,
                    COUNT(DISTINCT "to") / (7.0 * 24) as avg_hourly_recipients,
                    SUM(amount / 1e18) / (7.0 * 24) as avg_hourly_volume
                FROM "CrcV2_Erc20WrapperTransfer"
                WHERE "from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}')
                  AND "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '7 days')
                  AND "timestamp" <= EXTRACT(EPOCH FROM NOW() - interval '1 hour')
                GROUP BY "from"
            )
            SELECT
                v.vault,
                v.withdrawal_count,
                v.unique_recipients,
                v.total_withdrawn_crc,
                v.avg_withdrawal_crc,
                COALESCE(h.avg_hourly_withdrawals, 0) as baseline_withdrawals,
                COALESCE(h.avg_hourly_recipients, 0) as baseline_recipients,
                COALESCE(h.avg_hourly_volume, 0) as baseline_volume
            FROM vault_stats v
            LEFT JOIN historical_stats h ON v.vault = h.vault
            """;

        var results = new List<SybilDetectionResult>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new SybilDetectionResult
            {
                VaultAddress = reader.GetString(0),
                WithdrawalCount = reader.GetInt64(1),
                UniqueRecipients = reader.GetInt64(2),
                TotalWithdrawnCrc = reader.GetDecimal(3),
                AvgWithdrawalCrc = reader.GetDecimal(4),
                BaselineHourlyWithdrawals = reader.GetDouble(5),
                BaselineHourlyRecipients = reader.GetDouble(6),
                BaselineHourlyVolume = reader.GetDouble(7)
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

    /// <summary>
    /// Get group treasury z-scores for anomaly detection.
    /// Monitors group vault balance changes for unusual outflows.
    /// </summary>
    public async Task<List<GroupTreasuryZScore>> GetGroupTreasuryZScoresAsync(CancellationToken ct)
    {
        const string sql = """
            WITH group_vaults AS (
                -- Get all group vaults with their group info
                SELECT
                    cv.vault,
                    cv."group" as group_address,
                    g.name as group_name
                FROM "CrcV2_CreateVault" cv
                JOIN "V_CrcV2_Groups" g ON g."group" = cv."group"
            ),
            vault_balances AS (
                -- Get hourly balance snapshots for group vaults
                SELECT
                    gv.group_address,
                    gv.group_name,
                    b."timestamp",
                    SUM(b.balance) as total_balance
                FROM group_vaults gv
                JOIN "V_CrcV2_GroupVaultBalancesByToken_1h" b ON b.vault = gv.vault
                WHERE b."timestamp" > NOW() - interval '30 days'
                GROUP BY gv.group_address, gv.group_name, b."timestamp"
            ),
            hourly_changes AS (
                SELECT
                    group_address,
                    group_name,
                    "timestamp",
                    total_balance,
                    total_balance - LAG(total_balance) OVER (PARTITION BY group_address ORDER BY "timestamp") as change
                FROM vault_balances
            ),
            stats AS (
                SELECT
                    group_address,
                    group_name,
                    AVG(change) as mean_change,
                    STDDEV(change) as stddev_change,
                    ABS(AVG(CASE WHEN change < 0 THEN change ELSE 0 END) * 24) as avg_daily_withdrawal
                FROM hourly_changes
                WHERE change IS NOT NULL
                GROUP BY group_address, group_name
                HAVING STDDEV(change) > 0
            ),
            latest AS (
                SELECT DISTINCT ON (group_address)
                    group_address,
                    group_name,
                    total_balance as current_balance,
                    change as latest_change
                FROM hourly_changes
                WHERE change IS NOT NULL
                ORDER BY group_address, "timestamp" DESC
            )
            SELECT
                l.group_address,
                l.group_name,
                l.latest_change,
                l.current_balance,
                s.mean_change,
                s.stddev_change,
                s.avg_daily_withdrawal,
                CASE
                    WHEN s.stddev_change > 0
                    THEN (l.latest_change - s.mean_change) / s.stddev_change
                    ELSE 0
                END as z_score,
                CASE
                    WHEN l.current_balance > 0
                    THEN ABS(l.latest_change / l.current_balance) * 100
                    ELSE 0
                END as balance_percentage,
                CASE
                    WHEN s.avg_daily_withdrawal > 0 AND l.latest_change < 0
                    THEN ABS(l.latest_change) / s.avg_daily_withdrawal
                    ELSE 0
                END as rate_acceleration
            FROM latest l
            JOIN stats s USING (group_address)
            ORDER BY z_score ASC
            """;

        var results = new List<GroupTreasuryZScore>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var latestChange = reader.GetDecimal(2);
            var currentBalance = reader.GetDecimal(3);

            results.Add(new GroupTreasuryZScore
            {
                GroupAddress = reader.GetString(0),
                GroupName = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
                LatestChange = latestChange,
                LatestChangeCrc = latestChange / 1_000_000_000_000_000_000m,
                CurrentBalanceCrc = currentBalance / 1_000_000_000_000_000_000m,
                MeanChange = reader.GetDouble(4),
                StdDevChange = reader.GetDouble(5),
                ZScore = reader.GetDouble(7),
                BalancePercentage = reader.GetDouble(8),
                RateAcceleration = reader.GetDouble(9)
            });
        }

        return results;
    }

    #endregion

    #region Aggregate TVL

    /// <summary>
    /// Get aggregate Balancer TVL with hourly and 15-minute changes.
    /// Returns total value locked, change from 1 hour ago, and net flow in last 15 minutes.
    /// </summary>
    public async Task<TvlSnapshot> GetBalancerTvlAsync(CancellationToken ct)
    {
        // Query combines hourly view for 1h change + raw events for 15m rapid detection
        var sql = $"""
            WITH current_tvl AS (
                SELECT
                    SUM(value) as total_balance,
                    MAX("timestamp") as "timestamp"
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_Erc20BalancerVaultBalance_1h")
            ),
            previous_tvl AS (
                SELECT SUM(value) as total_balance
                FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                WHERE "timestamp" = (
                    SELECT MAX("timestamp") - interval '1 hour'
                    FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
                )
            ),
            -- 15-minute net flow from raw transfer events
            recent_flow AS (
                SELECT COALESCE(SUM(
                    CASE
                        WHEN "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN amount
                        WHEN "from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}') THEN -amount
                        ELSE 0
                    END
                ), 0) as net_flow_15m
                FROM "CrcV2_Erc20WrapperTransfer"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '15 minutes')
                  AND ("from" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}')
                       OR "to" IN ('{BalancerV2VaultAddress}', '{BalancerV3VaultAddress}'))
            )
            SELECT
                COALESCE(c.total_balance, 0) as current_total,
                COALESCE(p.total_balance, 0) as previous_total,
                c."timestamp",
                r.net_flow_15m
            FROM current_tvl c
            CROSS JOIN previous_tvl p
            CROSS JOIN recent_flow r
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var currentTotal = reader.GetDecimal(0);
            var previousTotal = reader.GetDecimal(1);
            var timestamp = reader.IsDBNull(2) ? DateTimeOffset.UtcNow : reader.GetFieldValue<DateTimeOffset>(2);
            var netFlow15m = reader.GetDecimal(3);

            // Convert to CRC (divide by 1e18)
            var currentCrc = currentTotal / 1_000_000_000_000_000_000m;
            var previousCrc = previousTotal / 1_000_000_000_000_000_000m;
            var change1hCrc = currentCrc - previousCrc;
            var change1hPct = previousCrc > 0 ? (double)(change1hCrc / previousCrc * 100) : 0;

            var change15mCrc = netFlow15m / 1_000_000_000_000_000_000m;
            var change15mPct = currentCrc > 0 ? (double)(change15mCrc / currentCrc * 100) : 0;

            return new TvlSnapshot
            {
                CurrentTotalCrc = currentCrc,
                PreviousTotalCrc = previousCrc,
                Change1hCrc = change1hCrc,
                Change1hPct = change1hPct,
                Change15mCrc = change15mCrc,
                Change15mPct = change15mPct,
                Timestamp = timestamp
            };
        }

        return new TvlSnapshot();
    }

    /// <summary>
    /// Get aggregate Group Treasury TVL with hourly and 15-minute changes.
    /// Uses the hourly view for 1h change + raw events for 15m rapid detection.
    /// </summary>
    public async Task<TvlSnapshot> GetGroupTreasuryTvlAsync(CancellationToken ct)
    {
        const string sql = """
            WITH current_tvl AS (
                SELECT
                    SUM(balance) as total_balance,
                    MAX("timestamp") as "timestamp"
                FROM "V_CrcV2_GroupVaultBalancesByToken_1h"
                WHERE "timestamp" = (SELECT MAX("timestamp") FROM "V_CrcV2_GroupVaultBalancesByToken_1h")
            ),
            previous_tvl AS (
                SELECT SUM(balance) as total_balance
                FROM "V_CrcV2_GroupVaultBalancesByToken_1h"
                WHERE "timestamp" = (
                    SELECT MAX("timestamp") - 3600  -- 1 hour in unix seconds
                    FROM "V_CrcV2_GroupVaultBalancesByToken_1h"
                )
            ),
            -- 15-minute net flow from raw collateral events
            recent_inflows AS (
                SELECT COALESCE(SUM(value), 0) as total
                FROM (
                    SELECT value FROM "CrcV2_CollateralLockedSingle"
                    WHERE "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '15 minutes')
                    UNION ALL
                    SELECT value FROM "CrcV2_CollateralLockedBatch"
                    WHERE "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '15 minutes')
                ) inflows
            ),
            recent_outflows AS (
                SELECT COALESCE(SUM(value), 0) as total
                FROM (
                    SELECT value FROM "CrcV2_GroupRedeemCollateralReturn"
                    WHERE "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '15 minutes')
                    UNION ALL
                    SELECT value FROM "CrcV2_GroupRedeemCollateralBurn"
                    WHERE "timestamp" > EXTRACT(EPOCH FROM NOW() - interval '15 minutes')
                ) outflows
            )
            SELECT
                COALESCE(c.total_balance, 0) as current_total,
                COALESCE(p.total_balance, 0) as previous_total,
                c."timestamp",
                (SELECT total FROM recent_inflows) - (SELECT total FROM recent_outflows) as net_flow_15m
            FROM current_tvl c
            CROSS JOIN previous_tvl p
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var currentTotal = reader.GetDecimal(0);
            var previousTotal = reader.GetDecimal(1);
            var timestamp = reader.IsDBNull(2)
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2));
            var netFlow15m = reader.GetDecimal(3);

            // Convert to CRC (divide by 1e18)
            var currentCrc = currentTotal / 1_000_000_000_000_000_000m;
            var previousCrc = previousTotal / 1_000_000_000_000_000_000m;
            var change1hCrc = currentCrc - previousCrc;
            var change1hPct = previousCrc > 0 ? (double)(change1hCrc / previousCrc * 100) : 0;

            var change15mCrc = netFlow15m / 1_000_000_000_000_000_000m;
            var change15mPct = currentCrc > 0 ? (double)(change15mCrc / currentCrc * 100) : 0;

            return new TvlSnapshot
            {
                CurrentTotalCrc = currentCrc,
                PreviousTotalCrc = previousCrc,
                Change1hCrc = change1hCrc,
                Change1hPct = change1hPct,
                Change15mCrc = change15mCrc,
                Change15mPct = change15mPct,
                Timestamp = timestamp
            };
        }

        return new TvlSnapshot();
    }

    #endregion
}

#region DTOs

public record TvlSnapshot
{
    public decimal CurrentTotalCrc { get; init; }
    public decimal PreviousTotalCrc { get; init; }
    public decimal Change1hCrc { get; init; }
    public double Change1hPct { get; init; }
    public decimal Change15mCrc { get; init; }
    public double Change15mPct { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

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
    public required string TokenName { get; init; }
    /// <summary>Raw change in wei (18 decimals)</summary>
    public decimal LatestChange { get; init; }
    /// <summary>Change in CRC (human-readable)</summary>
    public decimal LatestChangeCrc { get; init; }
    /// <summary>Current balance in CRC</summary>
    public decimal CurrentBalanceCrc { get; init; }
    /// <summary>Percentage of balance that this change represents</summary>
    public double BalancePercentage { get; init; }
    /// <summary>How many times larger this withdrawal is vs average daily withdrawal</summary>
    public double RateAcceleration { get; init; }
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

public record SybilDetectionResult
{
    public required string VaultAddress { get; init; }
    public long WithdrawalCount { get; init; }
    public long UniqueRecipients { get; init; }
    public decimal TotalWithdrawnCrc { get; init; }
    public decimal AvgWithdrawalCrc { get; init; }
    public double BaselineHourlyWithdrawals { get; init; }
    public double BaselineHourlyRecipients { get; init; }
    public double BaselineHourlyVolume { get; init; }
}

public record GroupTreasuryZScore
{
    public required string GroupAddress { get; init; }
    public required string GroupName { get; init; }
    public decimal LatestChange { get; init; }
    public decimal LatestChangeCrc { get; init; }
    public decimal CurrentBalanceCrc { get; init; }
    public double MeanChange { get; init; }
    public double StdDevChange { get; init; }
    public double ZScore { get; init; }
    public double BalancePercentage { get; init; }
    public double RateAcceleration { get; init; }
}

#endregion

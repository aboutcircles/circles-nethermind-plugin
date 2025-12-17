using Npgsql;

namespace Circles.Metrics.Exporter.Services;

/// <summary>
/// Repository for querying Circles KPIs from PostgreSQL.
/// </summary>
public class KpiRepository
{
    private readonly string _connectionString;
    private readonly ILogger<KpiRepository> _logger;

    public KpiRepository(string connectionString, ILogger<KpiRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<long> GetTotalHumansV1Async(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV1_Signup"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetTotalHumansV2Async(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterHuman"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetTotalOrganizationsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterOrganization"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetTotalGroupsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterGroup"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetNewUsersAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_RegisterHuman"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetActiveTrustsAsync(CancellationToken ct = default)
    {
        // Count trusts where trustee is trusted by truster (excluding self-trusts)
        const string sql = """
            SELECT COUNT(*) FROM "V_CrcV2_TrustRelations"
            WHERE "trustee" != "truster"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetNewTrustsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_Trust"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND "expiryTime" > EXTRACT(EPOCH FROM NOW())
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<(long added, long removed)> GetTrustChangesAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Trusts with far future expiry are "added", trusts with past/zero expiry are "removed"
        var sqlAdded = $"""
            SELECT COUNT(*) FROM "CrcV2_Trust"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND "expiryTime" > EXTRACT(EPOCH FROM NOW()) + 86400
            """;

        var sqlRemoved = $"""
            SELECT COUNT(*) FROM "CrcV2_Trust"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND "expiryTime" <= EXTRACT(EPOCH FROM NOW())
            """;

        var added = await ExecuteScalarAsync<long>(sqlAdded, ct);
        var removed = await ExecuteScalarAsync<long>(sqlRemoved, ct);

        return (added, removed);
    }

    public async Task<long> GetTotalBackersAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(DISTINCT "backer") FROM "CrcV2_CirclesBackingInitiated"
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetActiveMintersAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<double> GetDailyMintVolumeAsync(CancellationToken ct = default)
    {
        // Sum of mint amounts in the last 24 hours, converted from wei to CRC
        // Use float8 to avoid decimal overflow on large sums
        const string sql = """
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_PersonalMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetDailyTransferVolumeAsync(CancellationToken ct = default)
    {
        // Sum of transfer amounts in the last 24 hours, converted from wei to CRC
        // Use float8 to avoid decimal overflow on large sums
        const string sql = """
            SELECT COALESCE(SUM(("value"::float8) / 1e18), 0)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<long> GetDailyTransferCountAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetUniqueTransactingAddressesAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Count unique senders and receivers in transfers
        var sql = $"""
            SELECT COUNT(DISTINCT addr) FROM (
                SELECT "from" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
                UNION
                SELECT "to" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            ) addresses
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetGroupMembersTotalAsync(CancellationToken ct = default)
    {
        // Members of standard treasury groups
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_GroupMembershipJoined"
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            // Table may not exist
            return 0;
        }
    }

    public async Task<double> GetGroupMintVolumeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_GroupMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetProfilesCreatedAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_UpdateMetadataDigest"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetProfilesTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(DISTINCT "avatar") FROM "CrcV2_UpdateMetadataDigest"
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    // ============================================================================
    // NEW KPIs - Dune parity + enhancements
    // ============================================================================

    public async Task<long> GetDailyMintCountAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_PersonalMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetNewBackersAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetMintingFraction14DayAsync(CancellationToken ct = default)
    {
        // Percentage of registered humans who minted in last 14 days
        const string sql = """
            SELECT
              COALESCE(
                (SELECT COUNT(DISTINCT "human")::float8 FROM "CrcV2_PersonalMint"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 1209600)
                /
                NULLIF((SELECT COUNT(*)::float8 FROM "CrcV2_RegisterHuman"), 0),
                0
              )
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<long> GetNewOrganizationsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_RegisterOrganization"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetNewGroupsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_RegisterGroup"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    // ============================================================================
    // Activity rates by time window (minters, spenders)
    // ============================================================================

    public async Task<double> GetMintingRateAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Percentage of registered humans who minted in the given window
        var sql = $"""
            SELECT
              COALESCE(
                (SELECT COUNT(DISTINCT "human")::float8 FROM "CrcV2_PersonalMint"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds})
                /
                NULLIF((SELECT COUNT(*)::float8 FROM "CrcV2_RegisterHuman"), 0),
                0
              )
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetSpendingRateAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Percentage of registered humans who sent transfers in the given window
        var sql = $"""
            SELECT
              COALESCE(
                (SELECT COUNT(DISTINCT "from")::float8 FROM "CrcV2_TransferSingle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
                 AND "from" != '0x0000000000000000000000000000000000000000')
                /
                NULLIF((SELECT COUNT(*)::float8 FROM "CrcV2_RegisterHuman"), 0),
                0
              )
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetTransferVolumeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(SUM(("value"::float8) / 1e18), 0)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetMintVolumeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_PersonalMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<long> GetTransferCountAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<double> GetAverageTransferAmountAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(AVG(("value"::float8) / 1e18), 0)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<double> GetMedianTransferAmountAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ("value"::float8) / 1e18), 0)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    // ============================================================================
    // Sybil detection metrics
    // ============================================================================

    public async Task<long> GetAccountsWithoutProfileAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterHuman" h
            LEFT JOIN "CrcV2_UpdateMetadataDigest" m ON h."avatar" = m."avatar"
            WHERE m."avatar" IS NULL
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetAccountsWithoutIncomingTrustAsync(CancellationToken ct = default)
    {
        // Accounts that no one else trusts (excluding self-trust)
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterHuman" h
            WHERE h."avatar" NOT IN (
                SELECT DISTINCT "trustee" FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
            )
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetBatchRegistrationsAsync(TimeSpan window, int threshold, CancellationToken ct = default)
    {
        // Count accounts that were registered in batches (same block with > threshold accounts)
        var sql = $"""
            SELECT COALESCE(SUM(cnt), 0) FROM (
                SELECT COUNT(*) as cnt
                FROM "CrcV2_RegisterHuman"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
                GROUP BY "blockNumber"
                HAVING COUNT(*) > {threshold}
            ) batches
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetMintAndDrainAccountsAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Accounts that minted in the window but have zero balance now
        var sql = $"""
            SELECT COUNT(DISTINCT pm."human")
            FROM "CrcV2_PersonalMint" pm
            WHERE pm."timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND pm."human" NOT IN (
                SELECT DISTINCT "account" FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "totalBalance" > 0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetHighVolumeInvitersCountAsync(TimeSpan window, int threshold, CancellationToken ct = default)
    {
        // Count of inviters who invited more than threshold accounts in the window
        var sql = $"""
            SELECT COUNT(*) FROM (
                SELECT "inviter"
                FROM "CrcV2_RegisterHuman"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
                AND "inviter" IS NOT NULL
                AND "inviter" != '0x0000000000000000000000000000000000000000'
                GROUP BY "inviter"
                HAVING COUNT(*) > {threshold}
            ) high_volume
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetSuspiciousAccountsAsync(CancellationToken ct = default)
    {
        // Accounts matching multiple sybil indicators:
        // - No profile AND no incoming trust AND minted
        const string sql = """
            SELECT COUNT(DISTINCT h."avatar")
            FROM "CrcV2_RegisterHuman" h
            LEFT JOIN "CrcV2_UpdateMetadataDigest" m ON h."avatar" = m."avatar"
            WHERE m."avatar" IS NULL
            AND h."avatar" NOT IN (
                SELECT DISTINCT "trustee" FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
            )
            AND h."avatar" IN (
                SELECT DISTINCT "human" FROM "CrcV2_PersonalMint"
            )
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetOrganicAccountsAsync(CancellationToken ct = default)
    {
        // Accounts with profile AND incoming trust (healthy accounts)
        const string sql = """
            SELECT COUNT(DISTINCT h."avatar")
            FROM "CrcV2_RegisterHuman" h
            INNER JOIN "CrcV2_UpdateMetadataDigest" m ON h."avatar" = m."avatar"
            WHERE h."avatar" IN (
                SELECT DISTINCT "trustee" FROM "V_CrcV2_TrustRelations"
                WHERE "truster" != "trustee"
            )
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    // ============================================================================
    // Advanced Monetary/Economic Metrics
    // ============================================================================

    public async Task<double> GetTotalCrcSupplyAsync(CancellationToken ct = default)
    {
        // Total CRC in circulation (sum of all balances)
        const string sql = """
            SELECT COALESCE(SUM(("demurragedTotalBalance"::float8) / 1e18), 0)
            FROM "V_CrcV2_BalancesByAccountAndToken"
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetTotalMintedAllTimeAsync(CancellationToken ct = default)
    {
        // Total CRC ever minted (before demurrage)
        const string sql = """
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_PersonalMint"
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public Task<double> GetDemurragePaidAsync(TimeSpan window, CancellationToken ct = default)
    {
        // CrcV2_Demurrage table does not exist - demurrage is applied on-the-fly
        // Return 0 as we cannot calculate this without a dedicated demurrage tracking table
        return Task.FromResult(0.0);
    }

    public async Task<double> GetMoneyVelocityAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Money velocity = Transfer volume / Average supply
        // Higher velocity = CRC is changing hands frequently
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(("value"::float8) / 1e18) FROM "CrcV2_TransferSingle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds})
                /
                NULLIF((SELECT SUM(("demurragedTotalBalance"::float8) / 1e18)
                        FROM "V_CrcV2_BalancesByAccountAndToken"), 0),
                0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetActiveBalanceHoldersAsync(CancellationToken ct = default)
    {
        // Number of accounts with non-zero CRC balance
        const string sql = """
            SELECT COUNT(DISTINCT "account")
            FROM "V_CrcV2_BalancesByAccountAndToken"
            WHERE "demurragedTotalBalance" > 0
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetAverageBalanceAsync(CancellationToken ct = default)
    {
        // Average CRC balance per account (among holders with balance > 0)
        const string sql = """
            SELECT COALESCE(AVG(total_balance), 0) FROM (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "demurragedTotalBalance" > 0
                GROUP BY "account"
            ) balances
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetMedianBalanceAsync(CancellationToken ct = default)
    {
        // Median CRC balance per account (among holders with balance > 0)
        const string sql = """
            SELECT COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY total_balance), 0) FROM (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "demurragedTotalBalance" > 0
                GROUP BY "account"
            ) balances
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetGiniCoefficientAsync(CancellationToken ct = default)
    {
        // Gini coefficient (0 = perfect equality, 1 = perfect inequality)
        // Using a simplified calculation that works in PostgreSQL
        const string sql = """
            WITH balances AS (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as balance
                FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "demurragedTotalBalance" > 0
                GROUP BY "account"
                ORDER BY balance
            ),
            indexed AS (
                SELECT balance, ROW_NUMBER() OVER (ORDER BY balance) as i,
                       COUNT(*) OVER () as n
                FROM balances
            )
            SELECT COALESCE(
                (2.0 * SUM(i * balance) / (n * SUM(balance))) - ((n + 1.0) / n),
                0
            )
            FROM indexed
            GROUP BY n
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetTopHolderConcentrationAsync(int topN, CancellationToken ct = default)
    {
        // Percentage of total supply held by top N holders
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(total_balance) FROM (
                    SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                    FROM "V_CrcV2_BalancesByAccountAndToken"
                    WHERE "demurragedTotalBalance" > 0
                    GROUP BY "account"
                    ORDER BY total_balance DESC
                    LIMIT {topN}
                ) top_holders)
                /
                NULLIF((SELECT SUM(("demurragedTotalBalance"::float8) / 1e18)
                        FROM "V_CrcV2_BalancesByAccountAndToken"), 0),
                0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetDailyActiveWalletsAsync(CancellationToken ct = default)
    {
        // Unique wallets that sent or received CRC in last 24h
        const string sql = """
            SELECT COUNT(DISTINCT addr) FROM (
                SELECT "from" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
                AND "from" != '0x0000000000000000000000000000000000000000'
                UNION
                SELECT "to" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
                AND "to" != '0x0000000000000000000000000000000000000000'
            ) addresses
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetWeeklyActiveWalletsAsync(CancellationToken ct = default)
    {
        // Unique wallets that sent or received CRC in last 7 days
        const string sql = """
            SELECT COUNT(DISTINCT addr) FROM (
                SELECT "from" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800
                AND "from" != '0x0000000000000000000000000000000000000000'
                UNION
                SELECT "to" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800
                AND "to" != '0x0000000000000000000000000000000000000000'
            ) addresses
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetMonthlyActiveWalletsAsync(CancellationToken ct = default)
    {
        // Unique wallets that sent or received CRC in last 30 days
        const string sql = """
            SELECT COUNT(DISTINCT addr) FROM (
                SELECT "from" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 2592000
                AND "from" != '0x0000000000000000000000000000000000000000'
                UNION
                SELECT "to" as addr FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 2592000
                AND "to" != '0x0000000000000000000000000000000000000000'
            ) addresses
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<double> GetUserRetentionRateAsync(TimeSpan window, CancellationToken ct = default)
    {
        // % of users who transacted in previous period who also transacted in current period
        // e.g., 7d retention = users active in last 7d who were also active 7-14d ago
        var periodSeconds = (int)window.TotalSeconds;
        var sql = $"""
            SELECT COALESCE(
                (SELECT COUNT(DISTINCT "from")::float8
                 FROM "CrcV2_TransferSingle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {periodSeconds}
                 AND "from" IN (
                     SELECT DISTINCT "from" FROM "CrcV2_TransferSingle"
                     WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {periodSeconds * 2}
                     AND "timestamp" <= EXTRACT(EPOCH FROM NOW()) - {periodSeconds}
                 ))
                /
                NULLIF((SELECT COUNT(DISTINCT "from")::float8
                        FROM "CrcV2_TransferSingle"
                        WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {periodSeconds * 2}
                        AND "timestamp" <= EXTRACT(EPOCH FROM NOW()) - {periodSeconds}), 0),
                0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> GetFirstTimeTransactorsAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Users who made their first transfer in the window
        var sql = $"""
            SELECT COUNT(*) FROM (
                SELECT "from"
                FROM "CrcV2_TransferSingle"
                GROUP BY "from"
                HAVING MIN("timestamp") > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            ) first_timers
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<double> GetTransferSizeDistributionPercentileAsync(double percentile, TimeSpan window, CancellationToken ct = default)
    {
        // Get a specific percentile of transfer sizes (e.g., P10, P25, P75, P90)
        var sql = $"""
            SELECT COALESCE(
                PERCENTILE_CONT({percentile}) WITHIN GROUP (ORDER BY ("value"::float8) / 1e18),
                0
            )
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<long> GetMicroTransactionCountAsync(TimeSpan window, double maxCrc, CancellationToken ct = default)
    {
        // Transfers below a threshold (e.g., < 1 CRC) - potential spam/test transactions
        var sql = $"""
            SELECT COUNT(*)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND ("value"::float8) / 1e18 < {maxCrc}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<long> GetLargeTransactionCountAsync(TimeSpan window, double minCrc, CancellationToken ct = default)
    {
        // Transfers above a threshold (e.g., > 100 CRC) - significant transactions
        var sql = $"""
            SELECT COUNT(*)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            AND ("value"::float8) / 1e18 > {minCrc}
            """;
        return await ExecuteScalarAsync<long>(sql, ct);
    }

    public async Task<double> GetNetInflowAsync(TimeSpan window, CancellationToken ct = default)
    {
        // Net new CRC = minted - demurraged (approximation)
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(("amount"::float8) / 1e18) FROM "CrcV2_PersonalMint"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}),
                0
            )
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    // ============================================================================
    // Account type breakdown metrics (humans, groups, organizations)
    // ============================================================================

    /// <summary>
    /// Gets Gini coefficient for a specific account type only.
    /// Valid types: "human", "group", "organization"
    /// </summary>
    public async Task<double> GetGiniCoefficientByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            WITH balances AS (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as balance
                FROM "V_CrcV2_BalancesByAccountAndToken" b
                INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                WHERE b."demurragedTotalBalance" > 0
                AND a."type" = '{typeFilter}'
                GROUP BY b."account"
                ORDER BY balance
            ),
            indexed AS (
                SELECT balance, ROW_NUMBER() OVER (ORDER BY balance) as i,
                       COUNT(*) OVER () as n
                FROM balances
            )
            SELECT COALESCE(
                (2.0 * SUM(i * balance) / (n * SUM(balance))) - ((n + 1.0) / n),
                0
            )
            FROM indexed
            GROUP BY n
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets total CRC balance held by a specific account type.
    /// </summary>
    public async Task<double> GetTotalBalanceByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COALESCE(SUM(("demurragedTotalBalance"::float8) / 1e18), 0)
            FROM "V_CrcV2_BalancesByAccountAndToken" b
            INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
            WHERE a."type" = '{typeFilter}'
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets count of accounts with non-zero balance by type.
    /// </summary>
    public async Task<long> GetBalanceHolderCountByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COUNT(DISTINCT b."account")
            FROM "V_CrcV2_BalancesByAccountAndToken" b
            INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
            WHERE b."demurragedTotalBalance" > 0
            AND a."type" = '{typeFilter}'
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets average CRC balance per account by type.
    /// </summary>
    public async Task<double> GetAverageBalanceByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COALESCE(AVG(total_balance), 0) FROM (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                FROM "V_CrcV2_BalancesByAccountAndToken" b
                INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                WHERE b."demurragedTotalBalance" > 0
                AND a."type" = '{typeFilter}'
                GROUP BY b."account"
            ) balances
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets median CRC balance per account by type.
    /// </summary>
    public async Task<double> GetMedianBalanceByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COALESCE(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY total_balance), 0) FROM (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                FROM "V_CrcV2_BalancesByAccountAndToken" b
                INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                WHERE b."demurragedTotalBalance" > 0
                AND a."type" = '{typeFilter}'
                GROUP BY b."account"
            ) balances
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets percentage of total supply held by top N holders of a specific type.
    /// </summary>
    public async Task<double> GetTopHolderConcentrationByTypeAsync(int topN, string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(total_balance) FROM (
                    SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as total_balance
                    FROM "V_CrcV2_BalancesByAccountAndToken" b
                    INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                    WHERE b."demurragedTotalBalance" > 0
                    AND a."type" = '{typeFilter}'
                    GROUP BY b."account"
                    ORDER BY total_balance DESC
                    LIMIT {topN}
                ) top_holders)
                /
                NULLIF((SELECT SUM(("demurragedTotalBalance"::float8) / 1e18)
                        FROM "V_CrcV2_BalancesByAccountAndToken" b
                        INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                        WHERE a."type" = '{typeFilter}'), 0),
                0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets supply share (percentage of total CRC) held by a specific account type.
    /// </summary>
    public async Task<double> GetSupplyShareByTypeAsync(string accountType, CancellationToken ct = default)
    {
        var typeFilter = GetAccountTypeFilter(accountType);
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(("demurragedTotalBalance"::float8) / 1e18)
                 FROM "V_CrcV2_BalancesByAccountAndToken" b
                 INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                 WHERE a."type" = '{typeFilter}')
                /
                NULLIF((SELECT SUM(("demurragedTotalBalance"::float8) / 1e18)
                        FROM "V_CrcV2_BalancesByAccountAndToken"), 0),
                0
            )
            """;

        try
        {
            return await ExecuteScalarAsync<double>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    private static string GetAccountTypeFilter(string accountType)
    {
        return accountType.ToLowerInvariant() switch
        {
            "human" => "CrcV2_RegisterHuman",
            "group" => "CrcV2_RegisterGroup",
            "organization" => "CrcV2_RegisterOrganization",
            _ => throw new ArgumentException($"Invalid account type: {accountType}. Valid types: human, group, organization")
        };
    }

    // ============================================================================
    // Network health metrics
    // ============================================================================

    public async Task<double> GetAverageTrustConnectionsAsync(CancellationToken ct = default)
    {
        // Average number of incoming trust connections per registered human
        const string sql = """
            SELECT COALESCE(
                (SELECT COUNT(*)::float8 FROM "V_CrcV2_TrustRelations" WHERE "truster" != "trustee")
                /
                NULLIF((SELECT COUNT(*)::float8 FROM "CrcV2_RegisterHuman"), 0),
                0
            )
            """;
        return await ExecuteScalarAsync<double>(sql, ct);
    }

    public async Task<long> GetIsolatedAccountsAsync(CancellationToken ct = default)
    {
        // Accounts with zero trust connections (neither trusting nor trusted)
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_RegisterHuman" h
            WHERE h."avatar" NOT IN (
                SELECT DISTINCT "truster" FROM "V_CrcV2_TrustRelations"
                UNION
                SELECT DISTINCT "trustee" FROM "V_CrcV2_TrustRelations"
            )
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken ct) where T : struct
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 30;

        var result = await cmd.ExecuteScalarAsync(ct);

        if (result == null || result == DBNull.Value)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }
}

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
        // Count distinct group memberships from the view
        // (trust relations where truster is a group)
        const string sql = """
            SELECT COUNT(DISTINCT ("group", member)) FROM "V_CrcV2_GroupMemberships"
            """;

        try
        {
            return await ExecuteScalarAsync<long>(sql, ct);
        }
        catch
        {
            // View may not exist during initial sync
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
    /// Gets Gini coefficient for humans excluding custodians/aggregators.
    /// Excludes accounts holding more than maxUniqueTokens different token types,
    /// as these are likely exchanges, custodians, or protocol treasuries.
    /// </summary>
    public async Task<double> GetGiniCoefficientNonCustodialAsync(int maxUniqueTokens = 50, CancellationToken ct = default)
    {
        var sql = $"""
            WITH account_token_counts AS (
                SELECT
                    b."account",
                    COUNT(DISTINCT b."tokenId") as unique_tokens,
                    SUM(("demurragedTotalBalance"::float8) / 1e18) as balance
                FROM "V_CrcV2_BalancesByAccountAndToken" b
                INNER JOIN "V_CrcV2_Avatars" a ON b."account" = a."avatar"
                WHERE b."demurragedTotalBalance" > 0
                AND a."type" = 'CrcV2_RegisterHuman'
                GROUP BY b."account"
                HAVING COUNT(DISTINCT b."tokenId") <= {maxUniqueTokens}
            ),
            balances AS (
                SELECT balance FROM account_token_counts ORDER BY balance
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
    // BATCHED QUERIES - Reduce database round-trips
    // ============================================================================

    /// <summary>
    /// Result of batched entity counts query
    /// </summary>
    public record EntityCounts(
        long HumansV1,
        long HumansV2,
        long Organizations,
        long Groups,
        long Backers,
        long ProfilesTotal,
        long ActiveTrusts
    );

    /// <summary>
    /// Gets all basic entity counts in a single query.
    /// Replaces 7 individual queries.
    /// </summary>
    public async Task<EntityCounts> GetEntityCountsBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM "CrcV1_Signup") as humans_v1,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman") as humans_v2,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization") as organizations,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup") as groups,
                (SELECT COUNT(DISTINCT "backer") FROM "CrcV2_CirclesBackingInitiated") as backers,
                (SELECT COUNT(DISTINCT "avatar") FROM "CrcV2_UpdateMetadataDigest") as profiles_total,
                (SELECT COUNT(*) FROM "V_CrcV2_TrustRelations" WHERE "trustee" != "truster") as active_trusts
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 60;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new EntityCounts(
                HumansV1: reader.GetInt64(0),
                HumansV2: reader.GetInt64(1),
                Organizations: reader.GetInt64(2),
                Groups: reader.GetInt64(3),
                Backers: reader.GetInt64(4),
                ProfilesTotal: reader.GetInt64(5),
                ActiveTrusts: reader.GetInt64(6)
            );
        }

        return new EntityCounts(0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Result of batched time-windowed counts query
    /// </summary>
    public record TimeWindowedCounts(
        Dictionary<string, long> NewUsers,
        Dictionary<string, long> NewOrganizations,
        Dictionary<string, long> NewGroups,
        Dictionary<string, long> NewBackers,
        Dictionary<string, long> ActiveMinters,
        Dictionary<string, long> TransferCounts
    );

    /// <summary>
    /// Gets counts for multiple time windows in a single query.
    /// Replaces ~36 individual queries.
    /// </summary>
    public async Task<TimeWindowedCounts> GetTimeWindowedCountsBatchedAsync(CancellationToken ct = default)
    {
        // Time window boundaries in seconds
        const int h24 = 86400;
        const int d7 = 604800;
        const int d30 = 2592000;
        const int d90 = 7776000;
        const int d180 = 15552000;
        const int d365 = 31536000;

        var sql = $"""
            WITH time_bounds AS (
                SELECT EXTRACT(EPOCH FROM NOW()) as now
            )
            SELECT
                -- New users by window
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {h24}) as new_users_24h,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {d7}) as new_users_7d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {d30}) as new_users_30d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {d90}) as new_users_90d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {d180}) as new_users_180d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman", time_bounds WHERE "timestamp" > now - {d365}) as new_users_1y,
                -- New organizations by window
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {h24}) as new_orgs_24h,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {d7}) as new_orgs_7d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {d30}) as new_orgs_30d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {d90}) as new_orgs_90d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {d180}) as new_orgs_180d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterOrganization", time_bounds WHERE "timestamp" > now - {d365}) as new_orgs_1y,
                -- New groups by window
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {h24}) as new_groups_24h,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {d7}) as new_groups_7d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {d30}) as new_groups_30d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {d90}) as new_groups_90d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {d180}) as new_groups_180d,
                (SELECT COUNT(*) FROM "CrcV2_RegisterGroup", time_bounds WHERE "timestamp" > now - {d365}) as new_groups_1y,
                -- New backers by window
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {h24}) as new_backers_24h,
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {d7}) as new_backers_7d,
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {d30}) as new_backers_30d,
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {d90}) as new_backers_90d,
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {d180}) as new_backers_180d,
                (SELECT COUNT(*) FROM "CrcV2_CirclesBackingInitiated", time_bounds WHERE "timestamp" > now - {d365}) as new_backers_1y,
                -- Active minters by window
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {h24}) as minters_24h,
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {d7}) as minters_7d,
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {d30}) as minters_30d,
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {d90}) as minters_90d,
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {d180}) as minters_180d,
                (SELECT COUNT(DISTINCT "human") FROM "CrcV2_PersonalMint", time_bounds WHERE "timestamp" > now - {d365}) as minters_1y,
                -- Transfer counts by window
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {h24}) as transfers_24h,
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {d7}) as transfers_7d,
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {d30}) as transfers_30d,
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {d90}) as transfers_90d,
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {d180}) as transfers_180d,
                (SELECT COUNT(*) FROM "CrcV2_TransferSingle", time_bounds WHERE "timestamp" > now - {d365}) as transfers_1y
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new TimeWindowedCounts(
                NewUsers: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(0),
                    ["7d"] = reader.GetInt64(1),
                    ["30d"] = reader.GetInt64(2),
                    ["90d"] = reader.GetInt64(3),
                    ["180d"] = reader.GetInt64(4),
                    ["1y"] = reader.GetInt64(5)
                },
                NewOrganizations: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(6),
                    ["7d"] = reader.GetInt64(7),
                    ["30d"] = reader.GetInt64(8),
                    ["90d"] = reader.GetInt64(9),
                    ["180d"] = reader.GetInt64(10),
                    ["1y"] = reader.GetInt64(11)
                },
                NewGroups: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(12),
                    ["7d"] = reader.GetInt64(13),
                    ["30d"] = reader.GetInt64(14),
                    ["90d"] = reader.GetInt64(15),
                    ["180d"] = reader.GetInt64(16),
                    ["1y"] = reader.GetInt64(17)
                },
                NewBackers: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(18),
                    ["7d"] = reader.GetInt64(19),
                    ["30d"] = reader.GetInt64(20),
                    ["90d"] = reader.GetInt64(21),
                    ["180d"] = reader.GetInt64(22),
                    ["1y"] = reader.GetInt64(23)
                },
                ActiveMinters: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(24),
                    ["7d"] = reader.GetInt64(25),
                    ["30d"] = reader.GetInt64(26),
                    ["90d"] = reader.GetInt64(27),
                    ["180d"] = reader.GetInt64(28),
                    ["1y"] = reader.GetInt64(29)
                },
                TransferCounts: new Dictionary<string, long>
                {
                    ["24h"] = reader.GetInt64(30),
                    ["7d"] = reader.GetInt64(31),
                    ["30d"] = reader.GetInt64(32),
                    ["90d"] = reader.GetInt64(33),
                    ["180d"] = reader.GetInt64(34),
                    ["1y"] = reader.GetInt64(35)
                }
            );
        }

        return new TimeWindowedCounts(
            new Dictionary<string, long>(), new Dictionary<string, long>(),
            new Dictionary<string, long>(), new Dictionary<string, long>(),
            new Dictionary<string, long>(), new Dictionary<string, long>()
        );
    }

    /// <summary>
    /// Result of batched economic metrics query
    /// </summary>
    public record EconomicMetrics(
        double TotalSupply,
        double TotalMintedAllTime,
        double DailyMintVolume,
        double DailyTransferVolume,
        long DailyMintCount,
        long ActiveBalanceHolders,
        double AverageBalance,
        double MedianBalance,
        double GiniCoefficient,
        long DailyActiveWallets,
        long WeeklyActiveWallets,
        long MonthlyActiveWallets
    );

    /// <summary>
    /// Gets economic/monetary metrics in a single query.
    /// Replaces ~12 individual queries.
    /// </summary>
    public async Task<EconomicMetrics> GetEconomicMetricsBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH balance_stats AS (
                SELECT
                    SUM(("demurragedTotalBalance"::float8) / 1e18) as total_supply,
                    COUNT(DISTINCT "account") as holder_count
                FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "demurragedTotalBalance" > 0
            ),
            balance_per_account AS (
                SELECT SUM(("demurragedTotalBalance"::float8) / 1e18) as balance
                FROM "V_CrcV2_BalancesByAccountAndToken"
                WHERE "demurragedTotalBalance" > 0
                GROUP BY "account"
            ),
            balance_distribution AS (
                SELECT
                    AVG(balance) as avg_balance,
                    PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY balance) as median_balance
                FROM balance_per_account
            ),
            gini_calc AS (
                SELECT COALESCE(
                    (2.0 * SUM(i * balance) / (n * SUM(balance))) - ((n + 1.0) / n),
                    0
                ) as gini
                FROM (
                    SELECT balance, ROW_NUMBER() OVER (ORDER BY balance) as i, COUNT(*) OVER () as n
                    FROM balance_per_account
                ) indexed
                GROUP BY n
            ),
            daily_activity AS (
                SELECT
                    COALESCE(SUM(("amount"::float8) / 1e18), 0) as mint_volume,
                    COUNT(*) as mint_count
                FROM "CrcV2_PersonalMint"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            ),
            daily_transfers AS (
                SELECT COALESCE(SUM(("value"::float8) / 1e18), 0) as transfer_volume
                FROM "CrcV2_TransferSingle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            ),
            active_wallets AS (
                SELECT
                    (SELECT COUNT(DISTINCT addr) FROM (
                        SELECT "from" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
                        UNION SELECT "to" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
                    ) d) as daw,
                    (SELECT COUNT(DISTINCT addr) FROM (
                        SELECT "from" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800
                        UNION SELECT "to" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 604800
                    ) w) as waw,
                    (SELECT COUNT(DISTINCT addr) FROM (
                        SELECT "from" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 2592000
                        UNION SELECT "to" as addr FROM "CrcV2_TransferSingle" WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 2592000
                    ) m) as maw
            )
            SELECT
                COALESCE((SELECT total_supply FROM balance_stats), 0),
                COALESCE((SELECT SUM(("amount"::float8) / 1e18) FROM "CrcV2_PersonalMint"), 0),
                COALESCE((SELECT mint_volume FROM daily_activity), 0),
                COALESCE((SELECT transfer_volume FROM daily_transfers), 0),
                COALESCE((SELECT mint_count FROM daily_activity), 0),
                COALESCE((SELECT holder_count FROM balance_stats), 0),
                COALESCE((SELECT avg_balance FROM balance_distribution), 0),
                COALESCE((SELECT median_balance FROM balance_distribution), 0),
                COALESCE((SELECT gini FROM gini_calc), 0),
                COALESCE((SELECT daw FROM active_wallets), 0),
                COALESCE((SELECT waw FROM active_wallets), 0),
                COALESCE((SELECT maw FROM active_wallets), 0)
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new EconomicMetrics(
                    TotalSupply: reader.GetDouble(0),
                    TotalMintedAllTime: reader.GetDouble(1),
                    DailyMintVolume: reader.GetDouble(2),
                    DailyTransferVolume: reader.GetDouble(3),
                    DailyMintCount: reader.GetInt64(4),
                    ActiveBalanceHolders: reader.GetInt64(5),
                    AverageBalance: reader.GetDouble(6),
                    MedianBalance: reader.GetDouble(7),
                    GiniCoefficient: reader.GetDouble(8),
                    DailyActiveWallets: reader.GetInt64(9),
                    WeeklyActiveWallets: reader.GetInt64(10),
                    MonthlyActiveWallets: reader.GetInt64(11)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute batched economic metrics query");
        }

        return new EconomicMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Result of batched sybil detection metrics query
    /// </summary>
    public record SybilMetrics(
        long AccountsWithoutProfile,
        long AccountsWithoutIncomingTrust,
        long SuspiciousAccounts,
        long OrganicAccounts,
        long IsolatedAccounts
    );

    /// <summary>
    /// Gets sybil detection metrics in a single query.
    /// Replaces ~5 individual queries.
    /// </summary>
    public async Task<SybilMetrics> GetSybilMetricsBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            WITH profile_status AS (
                SELECT h."avatar",
                       CASE WHEN m."avatar" IS NOT NULL THEN true ELSE false END as has_profile
                FROM "CrcV2_RegisterHuman" h
                LEFT JOIN "CrcV2_UpdateMetadataDigest" m ON h."avatar" = m."avatar"
            ),
            trust_status AS (
                SELECT DISTINCT "trustee" as avatar FROM "V_CrcV2_TrustRelations" WHERE "truster" != "trustee"
            ),
            mint_status AS (
                SELECT DISTINCT "human" as avatar FROM "CrcV2_PersonalMint"
            ),
            all_trust_participants AS (
                SELECT DISTINCT "truster" as avatar FROM "V_CrcV2_TrustRelations"
                UNION
                SELECT DISTINCT "trustee" as avatar FROM "V_CrcV2_TrustRelations"
            )
            SELECT
                (SELECT COUNT(*) FROM profile_status WHERE NOT has_profile) as no_profile,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman" h WHERE h."avatar" NOT IN (SELECT avatar FROM trust_status)) as no_incoming_trust,
                (SELECT COUNT(DISTINCT p."avatar")
                 FROM profile_status p
                 WHERE NOT p.has_profile
                 AND p."avatar" NOT IN (SELECT avatar FROM trust_status)
                 AND p."avatar" IN (SELECT avatar FROM mint_status)) as suspicious,
                (SELECT COUNT(DISTINCT p."avatar")
                 FROM profile_status p
                 WHERE p.has_profile
                 AND p."avatar" IN (SELECT avatar FROM trust_status)) as organic,
                (SELECT COUNT(*) FROM "CrcV2_RegisterHuman" h
                 WHERE h."avatar" NOT IN (SELECT avatar FROM all_trust_participants)) as isolated
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new SybilMetrics(
                    AccountsWithoutProfile: reader.GetInt64(0),
                    AccountsWithoutIncomingTrust: reader.GetInt64(1),
                    SuspiciousAccounts: reader.GetInt64(2),
                    OrganicAccounts: reader.GetInt64(3),
                    IsolatedAccounts: reader.GetInt64(4)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute batched sybil metrics query");
        }

        return new SybilMetrics(0, 0, 0, 0, 0);
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

    // ============================================================================
    // Token Offers Metrics (GNO Bonus, Marketplace)
    // ============================================================================

    public async Task<long> GetTokenOfferCyclesTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_TokenOffers_ERC20TokenOfferCycleCreated"
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

    public async Task<long> GetTokenOfferClaimsTotalAsync(CancellationToken ct = default)
    {
        // Claims can come from both OfferClaimed and OfferClaimedFromCycle
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimed") +
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle")
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

    public async Task<long> GetTokenOfferClaimsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimed"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}) +
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds})
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

    public async Task<long> GetTokenOfferUniqueClaimersAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT account) FROM (
                SELECT "account" FROM "CrcV2_TokenOffers_OfferClaimed"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
                UNION
                SELECT "account" FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"
                WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            ) claimers
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

    public async Task<double> GetTokenOfferCrcSpentAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}) +
                (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}),
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

    public async Task<double> GetTokenOfferCrcSpentTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(
                (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed") +
                (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"),
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

    public async Task<double> GetTokenOfferTokensReceivedAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(
                (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}) +
                (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"
                 WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}),
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

    public async Task<double> GetTokenOfferTokensReceivedTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(
                (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed") +
                (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"),
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
    /// Gets the latest offer configuration (price, limit, accepted CRCs).
    /// Returns (tokenPriceInCrc, offerLimitInCrc, acceptedCrcCount).
    /// </summary>
    public async Task<(double priceInCrc, double limitInCrc, int acceptedCrcCount)> GetLatestOfferConfigAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                ("tokenPriceInCRC"::float8) / 1e18 as price,
                ("offerLimitInCRC"::float8) / 1e18 as limit_crc,
                COALESCE(array_length("acceptedCRC", 1), 0) as accepted_count
            FROM "CrcV2_TokenOffers_NextOfferCreated"
            ORDER BY "blockNumber" DESC, "logIndex" DESC
            LIMIT 1
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 30;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return (
                    reader.GetDouble(0),
                    reader.GetDouble(1),
                    reader.GetInt32(2)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get latest offer config");
        }

        return (0, 0, 0);
    }

    // ============================================================================
    // Payment Gateway Metrics
    // ============================================================================

    public async Task<long> GetPaymentGatewaysTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_PaymentGateway_GatewayCreated"
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

    public async Task<long> GetPaymentGatewaysCreatedAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_PaymentGateway_GatewayCreated"
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

    public async Task<long> GetPaymentGatewayPaymentsTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    public async Task<long> GetPaymentGatewayPaymentsAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(*) FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    public async Task<double> GetPaymentGatewayVolumeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    public async Task<double> GetPaymentGatewayVolumeTotalAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(SUM(("amount"::float8) / 1e18), 0)
            FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    public async Task<long> GetPaymentGatewayUniquePayersAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT "payer")
            FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    public async Task<long> GetPaymentGatewayUniquePayeesAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT "payee")
            FROM "CrcV2_PaymentGateway_PaymentReceived"
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

    /// <summary>
    /// Batched query for token offers metrics.
    /// </summary>
    public record TokenOffersMetrics(
        long CyclesTotal,
        long ClaimsTotal,
        double CrcSpentTotal,
        double TokensReceivedTotal,
        double CurrentPriceInCrc,
        double CurrentLimitInCrc,
        int AcceptedCrcCount
    );

    public async Task<TokenOffersMetrics> GetTokenOffersMetricsBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_ERC20TokenOfferCycleCreated") as cycles_total,
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimed") +
                (SELECT COUNT(*) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle") as claims_total,
                COALESCE(
                    (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed") +
                    (SELECT SUM(("spent"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"),
                    0
                ) as crc_spent_total,
                COALESCE(
                    (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimed") +
                    (SELECT SUM(("received"::float8) / 1e18) FROM "CrcV2_TokenOffers_OfferClaimedFromCycle"),
                    0
                ) as tokens_received_total,
                COALESCE((
                    SELECT ("tokenPriceInCRC"::float8) / 1e18
                    FROM "CrcV2_TokenOffers_NextOfferCreated"
                    ORDER BY "blockNumber" DESC, "logIndex" DESC
                    LIMIT 1
                ), 0) as current_price,
                COALESCE((
                    SELECT ("offerLimitInCRC"::float8) / 1e18
                    FROM "CrcV2_TokenOffers_NextOfferCreated"
                    ORDER BY "blockNumber" DESC, "logIndex" DESC
                    LIMIT 1
                ), 0) as current_limit,
                COALESCE((
                    SELECT array_length("acceptedCRC", 1)
                    FROM "CrcV2_TokenOffers_NextOfferCreated"
                    ORDER BY "blockNumber" DESC, "logIndex" DESC
                    LIMIT 1
                ), 0) as accepted_count
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 60;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new TokenOffersMetrics(
                    CyclesTotal: reader.GetInt64(0),
                    ClaimsTotal: reader.GetInt64(1),
                    CrcSpentTotal: reader.GetDouble(2),
                    TokensReceivedTotal: reader.GetDouble(3),
                    CurrentPriceInCrc: reader.GetDouble(4),
                    CurrentLimitInCrc: reader.GetDouble(5),
                    AcceptedCrcCount: reader.GetInt32(6)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute batched token offers metrics query");
        }

        return new TokenOffersMetrics(0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Batched query for payment gateway metrics.
    /// </summary>
    public record PaymentGatewayMetrics(
        long GatewaysTotal,
        long PaymentsTotal,
        double VolumeTotal
    );

    public async Task<PaymentGatewayMetrics> GetPaymentGatewayMetricsBatchedAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM "CrcV2_PaymentGateway_GatewayCreated") as gateways_total,
                (SELECT COUNT(*) FROM "CrcV2_PaymentGateway_PaymentReceived") as payments_total,
                COALESCE((SELECT SUM(("amount"::float8) / 1e18) FROM "CrcV2_PaymentGateway_PaymentReceived"), 0) as volume_total
            """;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 60;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new PaymentGatewayMetrics(
                    GatewaysTotal: reader.GetInt64(0),
                    PaymentsTotal: reader.GetInt64(1),
                    VolumeTotal: reader.GetDouble(2)
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute batched payment gateway metrics query");
        }

        return new PaymentGatewayMetrics(0, 0, 0);
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

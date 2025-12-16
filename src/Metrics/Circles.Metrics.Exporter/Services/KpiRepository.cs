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
            SELECT COALESCE(SUM(("mintAmount"::float8) / 1e18), 0)
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

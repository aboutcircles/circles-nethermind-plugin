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

    public async Task<decimal> GetDailyMintVolumeAsync(CancellationToken ct = default)
    {
        // Sum of mint amounts in the last 24 hours, converted from wei to CRC (18 decimals)
        const string sql = """
            SELECT COALESCE(SUM(("amount"::numeric) / 1e18), 0)
            FROM "CrcV2_PersonalMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<decimal>(sql, ct);
    }

    public async Task<decimal> GetDailyTransferVolumeAsync(CancellationToken ct = default)
    {
        // Sum of transfer amounts in the last 24 hours
        const string sql = """
            SELECT COALESCE(SUM(("value"::numeric) / 1e18), 0)
            FROM "CrcV2_TransferSingle"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - 86400
            """;
        return await ExecuteScalarAsync<decimal>(sql, ct);
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

    public async Task<decimal> GetGroupMintVolumeAsync(TimeSpan window, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT COALESCE(SUM(("mintAmount"::numeric) / 1e18), 0)
            FROM "CrcV2_GroupMint"
            WHERE "timestamp" > EXTRACT(EPOCH FROM NOW()) - {(int)window.TotalSeconds}
            """;

        try
        {
            return await ExecuteScalarAsync<decimal>(sql, ct);
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

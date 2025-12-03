using Circles.Cache.Service.Caches;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that performs initial cache warmup by replaying all events from PostgreSQL.
/// Runs once on startup to build the cache state from historical blockchain data.
/// </summary>
public class CacheWarmupService : BackgroundService
{
    private readonly ILogger<CacheWarmupService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;

    public CacheWarmupService(
        ILogger<CacheWarmupService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting cache warmup...");

            await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
            await conn.OpenAsync(stoppingToken);

            // Get the current database head block
            var dbHead = await GetDatabaseHeadAsync(conn, stoppingToken);
            _logger.LogInformation("Database head block: {Block}", dbHead);

            // Replay V1 events
            await ReplayV1EventsAsync(conn, dbHead, stoppingToken);

            // Replay V2 events
            await ReplayV2EventsAsync(conn, dbHead, stoppingToken);

            // Load group memberships
            await LoadGroupMembershipsAsync(conn, stoppingToken);

            // Load balances (this may take longer)
            await LoadBalancesAsync(conn, stoppingToken);

            // Rebuild secondary indexes for fast balance lookups
            _logger.LogInformation("Rebuilding secondary indexes...");
            _caches.RebuildSecondaryIndexes();
            _logger.LogInformation("Secondary indexes rebuilt");

            // Mark warmup as complete
            _state.WarmupComplete = true;
            _state.LastProcessedBlock = dbHead;

            _logger.LogInformation("Cache warmup completed successfully. Processed up to block {Block}", dbHead);
            _logger.LogInformation("Cache statistics: {Stats}",
                System.Text.Json.JsonSerializer.Serialize(_caches.GetStatistics()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache warmup failed");
            throw;
        }
    }

    private async Task<long> GetDatabaseHeadAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(""blockNumber""), 0)
            FROM ""System_Block""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long blockNumber ? blockNumber : 0L;
    }

    private async Task ReplayV1EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Replaying V1 events...");

        // Replay V1 Signups (both human and organization)
        await ReplayV1SignupsAsync(conn, toBlock, ct);

        _logger.LogInformation("V1 event replay completed");
    }

    private async Task ReplayV1SignupsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        // Replay both human and organization signups in block order
        const string sql = @"
            SELECT
                s.""blockNumber"",
                s.""transactionIndex"",
                s.""logIndex"",
                s.""user"" as address,
                s.""token"",
                'Human' as type
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                o.""blockNumber"",
                o.""transactionIndex"",
                o.""logIndex"",
                o.""organization"" as address,
                NULL as token,
                'Organization' as type
            FROM ""CrcV1_OrganizationSignup"" o
            WHERE o.""blockNumber"" <= @toBlock

            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var humanCount = 0;
        var orgCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 10000; // Log every 10000 entries

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var address = reader.GetString(3);
            var token = reader.IsDBNull(4) ? null : reader.GetString(4);
            var type = reader.GetString(5);

            if (type == "Human")
            {
                // Add to V1Avatars cache
                _caches.V1Avatars.Add(blockNumber, address, ("Human", token!));

                // Add to V1TokenOwnerByToken cache (reverse mapping)
                _caches.V1TokenOwnerByToken.Add(blockNumber, token!, address);

                humanCount++;
            }
            else
            {
                // Add to V1Avatars cache (organizations don't have tokens)
                _caches.V1Avatars.Add(blockNumber, address, ("Organization", null));

                orgCount++;
            }

            // Log progress every 10000 entries
            var totalCount = humanCount + orgCount;
            if (totalCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V1 signup progress: {HumanCount} humans, {OrgCount} orgs ({Rate:F0} entries/sec)",
                    humanCount, orgCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Replayed {HumanCount} V1 human signups and {OrgCount} organization signups",
            humanCount, orgCount);
    }

    private async Task ReplayV2EventsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Replaying V2 events...");

        // Replay V2 avatar registrations (all types in block order)
        await ReplayV2AvatarRegistrationsAsync(conn, toBlock, ct);

        // Replay V2 ERC20WrapperDeployed
        await ReplayV2Erc20WrapperDeployedAsync(conn, toBlock, ct);

        _logger.LogInformation("V2 event replay completed");
    }

    private async Task ReplayV2AvatarRegistrationsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        // Combine all V2 avatar registration types in block order
        const string sql = @"
            SELECT
                r.""blockNumber"",
                r.""transactionIndex"",
                r.""logIndex"",
                r.""avatar"" as address,
                r.""timestamp"",
                'Human' as type,
                NULL as name,
                NULL as mint
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                r.""blockNumber"",
                r.""transactionIndex"",
                r.""logIndex"",
                r.""organization"" as address,
                r.""timestamp"",
                'Organization' as type,
                NULL as name,
                NULL as mint
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" <= @toBlock

            UNION ALL

            SELECT
                r.""blockNumber"",
                r.""transactionIndex"",
                r.""logIndex"",
                r.""group"" as address,
                0 as timestamp,
                'Group' as type,
                r.""name"",
                r.""mint""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" <= @toBlock

            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var humanCount = 0;
        var orgCount = 0;
        var groupCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 1000; // Log every 1000 entries (V2 has fewer than V1)

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var address = reader.GetString(3);
            var timestamp = reader.GetInt64(4);
            var type = reader.GetString(5);
            var name = reader.IsDBNull(6) ? null : reader.GetString(6);
            var mint = reader.IsDBNull(7) ? null : reader.GetString(7);

            if (type == "Human")
            {
                _caches.V2Avatars.Add(blockNumber, address, ("Human", timestamp));
                humanCount++;
            }
            else if (type == "Organization")
            {
                _caches.V2Avatars.Add(blockNumber, address, ("Organization", timestamp));
                orgCount++;
            }
            else if (type == "Group")
            {
                _caches.Groups.Add(blockNumber, address, (name!, mint!));
                groupCount++;
            }

            // Log progress every 1000 entries
            var totalCount = humanCount + orgCount + groupCount;
            if (totalCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V2 registration progress: {HumanCount} humans, {OrgCount} orgs, {GroupCount} groups ({Rate:F0} entries/sec)",
                    humanCount, orgCount, groupCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Replayed {HumanCount} V2 humans, {OrgCount} organizations, {GroupCount} groups",
            humanCount, orgCount, groupCount);
    }

    private async Task ReplayV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                e.""blockNumber"",
                e.""avatar"",
                e.""erc20Wrapper""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var avatar = reader.GetString(1);
            var erc20Wrapper = reader.GetString(2);

            // Add to Erc20WrapperAddresses cache
            _caches.Erc20WrapperAddresses.Add(blockNumber, avatar, erc20Wrapper);

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 ERC20 wrapper deployments", count);
    }

    private async Task LoadBalancesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading balances...");

        // Load V1 balances
        await LoadV1BalancesAsync(conn, ct);

        // Load V2 balances
        await LoadV2BalancesAsync(conn, ct);

        _logger.LogInformation("Balance loading completed");
    }

    private async Task LoadV1BalancesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                ""account"",
                ""tokenAddress"",
                ""totalBalance""
            FROM ""V_CrcV1_BalancesByAccountAndToken""
            WHERE ""totalBalance"" > 0
            ORDER BY ""account"", ""tokenAddress""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300; // 5 minutes for large balance queries
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 5000; // Log every 5000 entries

        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0);
            var tokenAddress = reader.GetString(1);
            var totalBalance = reader.GetDecimal(2);

            // Convert attoCircles to Circles (divide by 10^18)
            var balance = totalBalance / 1_000_000_000_000_000_000m;

            // Composite key: account:tokenAddress
            var key = $"{account}:{tokenAddress}";

            // Add to cache with block 1 (snapshot data doesn't have specific block)
            _caches.V1BalancesByAccountAndToken.Add(1, key, balance);

            count++;

            // Log progress every 5000 entries
            if (count % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V1 balances progress: {Count} loaded ({Rate:F0} entries/sec)",
                    count, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Loaded {Count} V1 balances", count);
    }

    private async Task LoadV2BalancesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                ""account"",
                ""tokenId"",
                ""demurragedTotalBalance""
            FROM ""V_CrcV2_BalancesByAccountAndToken""
            WHERE ""demurragedTotalBalance"" > 0
            ORDER BY ""account"", ""tokenId""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300; // 5 minutes for large balance queries
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 5000; // Log every 5000 entries

        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0);
            var tokenId = reader.GetString(1);
            var demurragedBalance = reader.GetDecimal(2);

            // Convert to Circles (divide by 10^18)
            var balance = demurragedBalance / 1_000_000_000_000_000_000m;

            // Composite key: account:tokenId
            var key = $"{account}:{tokenId}";

            // Add to cache with block 1 (snapshot data doesn't have specific block)
            _caches.V2BalancesByAccountAndToken.Add(1, key, balance);

            count++;

            // Log progress every 5000 entries
            if (count % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V2 balances progress: {Count} loaded ({Rate:F0} entries/sec)",
                    count, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Loaded {Count} V2 balances", count);
    }

    private async Task LoadGroupMembershipsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Loading group memberships...");

        const string sql = @"
            SELECT
                ""group"",
                ""member"",
                ""expiryTime"",
                ""blockNumber""
            FROM ""V_CrcV2_GroupMemberships""
            ORDER BY ""blockNumber"", ""group"", ""member""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var group = reader.GetString(0);
            var member = reader.GetString(1);

            // expiryTime is stored as NUMERIC and can be max decimal value (infinity)
            // Read as decimal first, then convert safely
            var expiryTimeDecimal = reader.GetDecimal(2);
            long expiryTime;

            // If expiry time is max value (infinity), use Int64.MaxValue as sentinel
            if (expiryTimeDecimal >= long.MaxValue)
            {
                expiryTime = long.MaxValue;
            }
            else
            {
                expiryTime = (long)expiryTimeDecimal;
            }

            var blockNumber = reader.GetInt64(3);

            // Composite key: group:member
            var key = $"{group}:{member}";

            // Add to cache
            _caches.GroupMemberships.Add(blockNumber, key, (member, expiryTime));

            count++;
        }

        _logger.LogInformation("Loaded {Count} group memberships", count);
    }
}

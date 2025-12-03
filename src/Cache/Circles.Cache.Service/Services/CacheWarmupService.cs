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

            // STEP 1: Get the current database head block and set it as warmup target
            var warmupTarget = await GetDatabaseHeadAsync(conn, stoppingToken);
            _state.WarmupTargetBlock = warmupTarget;
            _logger.LogInformation("Warmup target block set to: {Block}", warmupTarget);

            // STEP 2: Replay all events up to the warmup target
            _logger.LogInformation("Starting warmup replay up to block {Block}...", warmupTarget);

            // Replay V1 events
            await ReplayV1EventsAsync(conn, warmupTarget, stoppingToken);

            // Replay V2 events
            await ReplayV2EventsAsync(conn, warmupTarget, stoppingToken);

            // Load group memberships
            await LoadGroupMembershipsAsync(conn, stoppingToken);

            // Load balances (this may take longer)
            await LoadBalancesAsync(conn, stoppingToken);

            // Rebuild secondary indexes for fast balance lookups
            _logger.LogInformation("Rebuilding secondary indexes...");
            _caches.RebuildSecondaryIndexes();
            _logger.LogInformation("Secondary indexes rebuilt");

            // Update state
            _state.LastProcessedBlock = warmupTarget;
            _logger.LogInformation("Warmup replay completed at block {Block}", warmupTarget);

            // STEP 3: Initialize block ring buffer with recent blocks
            await InitializeBlockRingBufferAsync(conn, warmupTarget, stoppingToken);

            // STEP 4: Check if new blocks arrived during warmup and process them
            var currentHead = await GetDatabaseHeadAsync(conn, stoppingToken);
            if (currentHead > warmupTarget)
            {
                _logger.LogInformation(
                    "New blocks arrived during warmup ({WarmupTarget} -> {CurrentHead}). Processing gap...",
                    warmupTarget, currentHead);

                // Process the gap using the same notification processing logic
                await ProcessBlockGapAsync(conn, warmupTarget + 1, currentHead, stoppingToken);

                _state.LastProcessedBlock = currentHead;
                _logger.LogInformation("Processed {Count} blocks that arrived during warmup",
                    currentHead - warmupTarget);
            }
            else
            {
                _logger.LogInformation("No new blocks arrived during warmup");
            }

            // STEP 5: Mark warmup as complete
            _state.WarmupComplete = true;

            _logger.LogInformation("Cache warmup completed successfully. Current block: {Block}", _state.LastProcessedBlock);
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
        // blockNumber is BIGINT, can be returned as int or long depending on value
        return result switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };
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

            var expiryTime = reader.GetFieldValue<decimal>(2);
            var blockNumber = reader.GetInt64(3);

            // Composite key: group:member
            var key = $"{group}:{member}";

            // Add to cache
            _caches.GroupMemberships.Add(blockNumber, key, (member, (long)expiryTime));

            count++;
        }

        _logger.LogInformation("Loaded {Count} group memberships", count);
    }

    /// <summary>
    /// Initializes the block ring buffer with the most recent blocks from the database.
    /// </summary>
    private async Task InitializeBlockRingBufferAsync(NpgsqlConnection conn, long fromBlock, CancellationToken ct)
    {
        var capacity = _settings.RollbackCapacity;
        var startBlock = Math.Max(0, fromBlock - capacity + 1);

        _logger.LogInformation("Initializing block ring buffer with blocks {StartBlock} to {EndBlock}...",
            startBlock, fromBlock);

        const string sql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @startBlock AND ""blockNumber"" <= @endBlock
            ORDER BY ""blockNumber"" ASC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("startBlock", startBlock);
        cmd.Parameters.AddWithValue("endBlock", fromBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var blockHashStr = reader.GetString(1);

            _state.BlockRingBuffer.Add(blockNumber, blockHashStr);
            count++;
        }

        _logger.LogInformation("Block ring buffer initialized with {Count} blocks", count);
    }

    /// <summary>
    /// Processes blocks that arrived during the warmup phase.
    /// Reuses the same logic as the notification listener.
    /// </summary>
    private async Task ProcessBlockGapAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Processing block gap: {FromBlock} to {ToBlock}", fromBlock, toBlock);

        // Update block ring buffer with new blocks
        const string blocksSql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"" ASC";

        var newBlocks = new List<(long BlockNumber, string BlockHash)>();

        await using (var blocksCmd = new NpgsqlCommand(blocksSql, conn))
        {
            blocksCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            blocksCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var blocksReader = await blocksCmd.ExecuteReaderAsync(ct);
            while (await blocksReader.ReadAsync(ct))
            {
                var blockNumber = blocksReader.GetInt64(0);
                var blockHashStr = blocksReader.GetString(1);
                newBlocks.Add((blockNumber, blockHashStr));
            }
        }

        // Check for reorgs when updating the ring buffer
        var reorgPoint = _state.BlockRingBuffer.UpdateFromBlocks(newBlocks);

        if (reorgPoint.HasValue)
        {
            _logger.LogWarning("Reorg detected at block {ReorgBlock} during gap processing! Rolling back caches...",
                reorgPoint.Value);

            // Rollback all caches
            _caches.RollbackAll(reorgPoint.Value);

            // Rebuild secondary indexes after rollback
            _logger.LogInformation("Rebuilding secondary indexes after rollback...");
            _caches.RebuildSecondaryIndexes();

            // Adjust the fromBlock to start processing from the reorg point
            fromBlock = reorgPoint.Value;
        }

        // Process V1 events in this range
        await ProcessV1EventsInRangeAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsInRangeAsync(conn, fromBlock, toBlock, ct);

        _logger.LogInformation("Successfully processed block gap {FromBlock} to {ToBlock}", fromBlock, toBlock);
    }

    /// <summary>
    /// Process V1 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV1EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups (humans)
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var user = reader.GetString(1);
                var token = reader.GetString(2);

                _caches.V1Avatars.Add(blockNumber, user, ("Human", token));
                _caches.V1TokenOwnerByToken.Add(blockNumber, token, user);
            }
        }

        // Process V1 Organization Signups
        const string orgSignupSql = @"
            SELECT o.""blockNumber"", o.""organization""
            FROM ""CrcV1_OrganizationSignup"" o
            WHERE o.""blockNumber"" >= @fromBlock AND o.""blockNumber"" <= @toBlock
            ORDER BY o.""blockNumber"", o.""transactionIndex"", o.""logIndex""";

        await using var orgCmd = new NpgsqlCommand(orgSignupSql, conn);
        orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        orgCmd.Parameters.AddWithValue("toBlock", toBlock);

        await using (var orgReader = await orgCmd.ExecuteReaderAsync(ct))
        {
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var organization = orgReader.GetString(1);

                _caches.V1Avatars.Add(blockNumber, organization, ("Organization", null));
            }
        }
    }

    /// <summary>
    /// Process V2 events in a specific block range (used for gap processing).
    /// </summary>
    private async Task ProcessV2EventsInRangeAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        const string humanSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var cmd = new NpgsqlCommand(humanSql, conn))
        {
            cmd.Parameters.AddWithValue("fromBlock", fromBlock);
            cmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var avatar = reader.GetString(2);

                _caches.V2Avatars.Add(blockNumber, avatar, ("Human", timestamp));
            }
        }

        // Process V2 RegisterOrganization
        const string orgSql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""organization""
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var orgCmd = new NpgsqlCommand(orgSql, conn))
        {
            orgCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            orgCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var orgReader = await orgCmd.ExecuteReaderAsync(ct);
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var timestamp = orgReader.GetInt64(1);
                var organization = orgReader.GetString(2);

                _caches.V2Avatars.Add(blockNumber, organization, ("Organization", timestamp));
            }
        }

        // Process V2 RegisterGroup
        const string groupSql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using (var groupCmd = new NpgsqlCommand(groupSql, conn))
        {
            groupCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            groupCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
            while (await groupReader.ReadAsync(ct))
            {
                var blockNumber = groupReader.GetInt64(0);
                var group = groupReader.GetString(1);
                var name = groupReader.GetString(2);
                var mint = groupReader.GetString(3);

                _caches.Groups.Add(blockNumber, group, (name, mint));
            }
        }

        // Process V2 ERC20WrapperDeployed
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using (var wrapperCmd = new NpgsqlCommand(wrapperSql, conn))
        {
            wrapperCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            wrapperCmd.Parameters.AddWithValue("toBlock", toBlock);

            await using var wrapperReader = await wrapperCmd.ExecuteReaderAsync(ct);
            while (await wrapperReader.ReadAsync(ct))
            {
                var blockNumber = wrapperReader.GetInt64(0);
                var avatar = wrapperReader.GetString(1);
                var erc20Wrapper = wrapperReader.GetString(2);

                _caches.Erc20WrapperAddresses.Add(blockNumber, avatar, erc20Wrapper);
            }
        }
    }
}

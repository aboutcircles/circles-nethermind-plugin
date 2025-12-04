using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Index.Common;
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

    // Fields for periodic reminder logging
    private DateTime _lastReminderLogTime = DateTime.MinValue;
    private readonly TimeSpan _reminderInterval = TimeSpan.FromMinutes(5);

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
        _logger.LogInformation("Starting cache warmup service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting cache warmup...");

                // Wait for PostgreSQL to be ready
                await WaitForDatabaseAsync(stoppingToken);

                await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
                await conn.OpenAsync(stoppingToken);

                // Wait for initial sync to complete before starting warmup
                await WaitForInitialSyncAsync(conn, stoppingToken);

                // Clear caches before replay to ensure monotonic block order
                ClearCaches();

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
                await LoadGroupMembershipsAsync(conn, warmupTarget, stoppingToken);

                // Load trust relations
                await LoadTrustRelationsAsync(conn, warmupTarget, stoppingToken);

                // Load avatar metadata (CID maps)
                await LoadAvatarMetadataAsync(conn, warmupTarget, stoppingToken);

                // Load V2 short names
                await LoadV2ShortNamesAsync(conn, warmupTarget, stoppingToken);

                // Load balances (this may take longer)
                await LoadBalancesAsync(conn, stoppingToken);

                // Rebuild secondary indexes for fast balance lookups
                _logger.LogInformation("Rebuilding secondary indexes...");
                _caches.RebuildSecondaryIndexes();
                _logger.LogInformation("Secondary indexes rebuilt");

                // Update state
                _state.LastProcessedBlock = warmupTarget;
                _logger.LogInformation("========================================");
                _logger.LogInformation("✓ Warmup replay completed at block {Block}", warmupTarget);
                _logger.LogInformation("========================================");

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

                // Reset reminder time on success
                _lastReminderLogTime = DateTime.MinValue;

                _logger.LogInformation("Cache warmup completed successfully");
                break; // Exit loop on success
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache warmup failed");

                // Log periodic reminder if enough time has passed since last reminder
                var now = DateTime.UtcNow;
                if (now - _lastReminderLogTime >= _reminderInterval)
                {
                    _logger.LogWarning("Cache warmup failed. Service will remain unhealthy until manual restart or DB issue is resolved.");
                    _lastReminderLogTime = now;
                }

                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Cache warmup service stopped");
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for PostgreSQL to be ready...");

        const int maxRetries = 60;
        const int delayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                _logger.LogInformation("PostgreSQL is ready");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PostgreSQL not ready yet (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...", i + 1, maxRetries, delayMs / 1000);
                await Task.Delay(delayMs, ct);
            }
        }

        throw new Exception($"PostgreSQL did not become ready after {maxRetries} attempts");
    }

    /// <summary>
    /// Waits for the Nethermind plugin to complete initial sync by listening for the first NOTIFY event.
    /// The plugin only sends NOTIFY events once initial sync is complete and blocks are being streamed live.
    /// </summary>
    private async Task WaitForInitialSyncAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Waiting for Nethermind plugin to complete initial sync...");
        _logger.LogInformation("Listening for first pg_notify event on channel '{Channel}' to detect sync completion...",
            _settings.PgNotifyChannel);

        // Set up NOTIFY listener
        var notificationReceived = new TaskCompletionSource<bool>();

        conn.Notification += (sender, args) =>
        {
            _logger.LogInformation("Received first NOTIFY event - initial sync is complete!");
            notificationReceived.TrySetResult(true);
        };

        await using var cmd = new NpgsqlCommand($"LISTEN {_settings.PgNotifyChannel}", conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Subscribed to NOTIFY channel. Waiting for first event...");

        // Wait for first notification or cancellation
        using var ctRegistration = ct.Register(() => notificationReceived.TrySetCanceled(ct));

        var lastWaitLogTime = DateTime.UtcNow;
        const int waitLogIntervalSeconds = 300;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for notification with periodic logging
                var waitTask = conn.WaitAsync(ct);
                var delayTask = Task.Delay(TimeSpan.FromSeconds(waitLogIntervalSeconds), ct);
                var completedTask = await Task.WhenAny(notificationReceived.Task, waitTask, delayTask);

                if (completedTask == notificationReceived.Task)
                {
                    // Notification received, sync is complete
                    _logger.LogInformation("Initial sync confirmed complete via NOTIFY event");

                    // Wait for the WaitAsync to complete if it hasn't already, to exit the waiting state
                    if (!waitTask.IsCompleted)
                    {
                        await waitTask;
                    }

                    // Unlisten to free the connection for subsequent operations
                    await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                    await unlistenCmd.ExecuteNonQueryAsync(ct);

                    return;
                }
                else if (completedTask == delayTask)
                {
                    // Periodic log to show we're still waiting
                    _logger.LogInformation("Still waiting for first NOTIFY event on channel '{Channel}'...",
                        _settings.PgNotifyChannel);
                }
                // If waitTask completed, it means there's data but not our notification, continue loop
            }
        }
        finally
        {
            // Ensure we always unlisten, even if cancelled or exception occurs
            try
            {
                await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                await unlistenCmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to UNLISTEN from channel '{Channel}' during cleanup", _settings.PgNotifyChannel);
            }
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
            SELECT * FROM (
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
            ) AS combined
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var humanCount = 0;
        var orgCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 10000; // Log every 10000 entries

        long lastBlockNumber = -1;
        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var address = reader.GetString(3);
            var token = reader.IsDBNull(4) ? null : reader.GetString(4);
            var type = reader.GetString(5);

            // Validation: Ensure data is actually in order
            if (blockNumber < lastBlockNumber)
            {
                _logger.LogError(
                    "Database contains out-of-order data! Block {Current} came after block {Previous}. " +
                    "This indicates the database was not fully synced or contains corrupted data.",
                    blockNumber, lastBlockNumber);
                throw new InvalidOperationException(
                    $"Database integrity error: Block numbers are not in ascending order ({blockNumber} < {lastBlockNumber})");
            }
            lastBlockNumber = blockNumber;

            var addressKey = address.ToLowerInvariant();

            if (type == "Human")
            {
                var tokenKey = token!.ToLowerInvariant();

                // Add to V1Avatars cache
                _caches.V1Avatars.Add(blockNumber, addressKey, ("Human", token!));

                // Add to V1TokenOwnerByToken cache (reverse mapping)
                _caches.V1TokenOwnerByToken.Add(blockNumber, tokenKey, address);

                humanCount++;
            }
            else
            {
                // Add to V1Avatars cache (organizations don't have tokens)
                _caches.V1Avatars.Add(blockNumber, addressKey, ("Organization", null));

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
            SELECT * FROM (
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
                    r.""mint"",
                    r.""symbol""
                FROM ""CrcV2_RegisterGroup"" r
                WHERE r.""blockNumber"" <= @toBlock
            ) AS combined
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
            var symbol = reader.IsDBNull(8) ? null : reader.GetString(8);

            var addressKey = address.ToLowerInvariant();

            if (type == "Human")
            {
                _caches.V2Avatars.Add(blockNumber, addressKey, ("Human", timestamp));
                humanCount++;
            }
            else if (type == "Organization")
            {
                _caches.V2Avatars.Add(blockNumber, addressKey, ("Organization", timestamp));
                orgCount++;
            }
            else if (type == "Group")
            {
                _caches.Groups.Add(blockNumber, addressKey, (name!, mint!, symbol!));
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

            var avatarKey = avatar.ToLowerInvariant();

            // Add to Erc20WrapperAddresses cache
            _caches.Erc20WrapperAddresses.Add(blockNumber, avatarKey, erc20Wrapper);

            count++;
        }

        _logger.LogInformation("Replayed {Count} V2 ERC20 wrapper deployments", count);
    }

    private async Task LoadBalancesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        _logger.LogInformation("Building balances from transfer events...");

        // Build V1 balances incrementally from transfers
        await BuildV1BalancesFromTransfersAsync(conn, ct);

        // Build V2 balances incrementally from transfers
        await BuildV2BalancesFromTransfersAsync(conn, ct);

        _logger.LogInformation("Balance building completed");
    }

    private async Task BuildV1BalancesFromTransfersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Process all V1 transfers and build balances incrementally
        // This is much faster than the aggregate view because it processes each transfer once
        const string sql = @"
            SELECT
                ""from"",
                ""to"",
                ""tokenAddress"",
                amount,
                ""blockNumber""
            FROM ""CrcV1_Transfer""
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 600; // 10 minutes for processing all transfers
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var transferCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 100000; // Log every 100k transfers

        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            var tokenAddress = reader.GetString(2);
            var amount = reader.GetDecimal(3);
            var blockNumber = reader.GetInt64(4);

            var tokenKey = tokenAddress.ToLowerInvariant();

            // Convert attoCircles to Circles
            var value = amount / 1_000_000_000_000_000_000m;

            // Skip zero address
            if (from != "0x0000000000000000000000000000000000000000")
            {
                // Subtract from sender
                var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                var currentBalance = _caches.V1BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                _caches.V1BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - value);
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                // Add to receiver
                var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                var currentBalance = _caches.V1BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                _caches.V1BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + value);
            }

            transferCount++;

            if (transferCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V1 balance building progress: {Count} transfers processed ({Rate:F0} transfers/sec)",
                    transferCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Built V1 balances from {Count} transfers. Total balance entries: {BalanceCount}",
            transferCount, _caches.V1BalancesByAccountAndToken.Count);
    }

    private async Task BuildV2BalancesFromTransfersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Process V2 TransferSingle events
        const string singleSql = @"
            SELECT
                ""from"",
                ""to"",
                id,
                value,
                ""blockNumber""
            FROM ""CrcV2_TransferSingle""
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var singleCmd = new NpgsqlCommand(singleSql, conn);
        singleCmd.CommandTimeout = 600;
        await using var singleReader = await singleCmd.ExecuteReaderAsync(ct);

        var transferCount = 0;
        var lastLogTime = DateTime.UtcNow;
        const int logInterval = 100000;

        while (await singleReader.ReadAsync(ct))
        {
            var from = singleReader.GetString(0);
            var to = singleReader.GetString(1);
            var tokenId = singleReader.GetFieldValue<decimal>(2).ToString();
            var value = singleReader.GetFieldValue<decimal>(3);
            var blockNumber = singleReader.GetInt64(4);

            // Convert to Circles
            var amount = value / 1_000_000_000_000_000_000m;

            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                _caches.V2BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - amount);
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
                var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                _caches.V2BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + amount);
            }

            transferCount++;

            if (transferCount % logInterval == 0)
            {
                var elapsed = DateTime.UtcNow - lastLogTime;
                _logger.LogInformation("V2 TransferSingle progress: {Count} transfers ({Rate:F0} transfers/sec)",
                    transferCount, logInterval / elapsed.TotalSeconds);
                lastLogTime = DateTime.UtcNow;
            }
        }

        // Process V2 TransferBatch events
        const string batchSql = @"
            SELECT
                ""from"",
                ""to"",
                id,
                value,
                ""blockNumber""
            FROM ""CrcV2_TransferBatch""
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var batchCmd = new NpgsqlCommand(batchSql, conn);
        batchCmd.CommandTimeout = 600;
        await using var batchReader = await batchCmd.ExecuteReaderAsync(ct);

        var batchCount = 0;

        while (await batchReader.ReadAsync(ct))
        {
            var from = batchReader.GetString(0);
            var to = batchReader.GetString(1);
            var tokenId = batchReader.GetFieldValue<decimal>(2).ToString();
            var value = batchReader.GetFieldValue<decimal>(3);
            var blockNumber = batchReader.GetInt64(4);

            var amount = value / 1_000_000_000_000_000_000m;

            if (from != "0x0000000000000000000000000000000000000000")
            {
                var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                _caches.V2BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - amount);
            }

            if (to != "0x0000000000000000000000000000000000000000")
            {
                var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
                var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                _caches.V2BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + amount);
            }

            batchCount++;
        }

        _logger.LogInformation("Built V2 balances from {SingleCount} TransferSingle + {BatchCount} TransferBatch. Total balance entries: {BalanceCount}",
            transferCount, batchCount, _caches.V2BalancesByAccountAndToken.Count);
    }

    private async Task LoadGroupMembershipsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading group memberships...");

        // Load group memberships at the current state
        // Use toBlock as block number since we're loading the final state
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

            // Safely cast expiryTime to long, capping at long.MaxValue for overflow
            long expiryLong = expiryTime > long.MaxValue ? long.MaxValue : (long)expiryTime;

            // Composite key: group:member
            var key = $"{group.ToLowerInvariant()}:{member.ToLowerInvariant()}";

            // Add to cache - use toBlock as we're loading deduplicated state after event replay
            _caches.GroupMemberships.Add(toBlock, key, (member, expiryLong));

            count++;
        }

        _logger.LogInformation("Loaded {Count} group memberships", count);
    }

    private async Task LoadTrustRelationsAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading trust relations...");

        // Load V1 trust relations - get latest trust status for each pair
        // Must order by blockNumber for RollbackCache
        const string v1Sql = @"
            SELECT t.""blockNumber"", t.truster, t.trustee, t.""limit""
            FROM (
                SELECT ""blockNumber"", ""transactionIndex"", ""logIndex"", ""canSendTo"" as truster, ""user"" as trustee, ""limit"",
                    ROW_NUMBER() OVER (PARTITION BY ""canSendTo"", ""user"" ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_Trust""
                WHERE ""blockNumber"" <= @toBlock
            ) t
            WHERE t.rn = 1 AND t.""limit"" > 0
            ORDER BY t.""blockNumber""";

        await using var v1Cmd = new NpgsqlCommand(v1Sql, conn);
        v1Cmd.Parameters.AddWithValue("toBlock", toBlock);
        await using var v1Reader = await v1Cmd.ExecuteReaderAsync(ct);
        var v1Count = 0;

        while (await v1Reader.ReadAsync(ct))
        {
            var blockNumber = v1Reader.GetInt64(0);
            var truster = v1Reader.GetString(1);
            var trustee = v1Reader.GetString(2);
            var limit = v1Reader.GetFieldValue<decimal>(3);

            // For V1, we use limit as the indicator (no expiry time)
            // Store as 0 for active trust (V1 doesn't have expiry)
            // Use toBlock as we're loading deduplicated state after event replay
            var key = $"{truster.ToLowerInvariant()}:{trustee.ToLowerInvariant()}";
            _caches.V1TrustRelations.Add(toBlock, key, 0L);

            v1Count++;
        }

        _logger.LogInformation("Loaded {Count} V1 trust relations", v1Count);

        // Load V2 trust relations - filter for non-expired
        const string v2Sql = @"
            SELECT ""blockNumber"", truster, trustee, ""expiryTime""
            FROM ""V_CrcV2_TrustRelations""
            ORDER BY ""blockNumber""";

        await using var v2Cmd = new NpgsqlCommand(v2Sql, conn);
        await using var v2Reader = await v2Cmd.ExecuteReaderAsync(ct);
        var v2Count = 0;

        while (await v2Reader.ReadAsync(ct))
        {
            var blockNumber = v2Reader.GetInt64(0);
            var truster = v2Reader.GetString(1);
            var trustee = v2Reader.GetString(2);
            var expiryTime = v2Reader.GetFieldValue<decimal>(3);

            long expiryLong = expiryTime > long.MaxValue ? long.MaxValue : (long)expiryTime;

            // Use toBlock as we're loading deduplicated state after event replay
            var key = $"{truster.ToLowerInvariant()}:{trustee.ToLowerInvariant()}";
            _caches.V2TrustRelations.Add(toBlock, key, expiryLong);

            v2Count++;
        }

        _logger.LogInformation("Loaded {Count} V2 trust relations", v2Count);
    }

    private async Task LoadAvatarMetadataAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading avatar metadata (CID maps)...");

        // Load V1 avatar CIDs (latest metadata digest for each avatar)
        const string v1CidSql = @"
            SELECT m.avatar, m.""metadataDigest""
            FROM (
                SELECT avatar, ""metadataDigest"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV1_UpdateMetadataDigest""
                WHERE ""blockNumber"" <= @toBlock
            ) m
            WHERE m.rn = 1";

        await using var v1CidCmd = new NpgsqlCommand(v1CidSql, conn);
        v1CidCmd.Parameters.AddWithValue("toBlock", toBlock);
        await using var v1CidReader = await v1CidCmd.ExecuteReaderAsync(ct);
        var v1CidCount = 0;

        while (await v1CidReader.ReadAsync(ct))
        {
            var avatar = v1CidReader.GetString(0);
            var cid = v1CidReader.GetString(1);

            var key = avatar.ToLowerInvariant();
            // Use toBlock as we're loading deduplicated state after event replay
            _caches.V1AvatarToCidMap.Add(toBlock, key, cid);

            v1CidCount++;
        }

        _logger.LogInformation("Loaded {Count} V1 avatar CIDs", v1CidCount);

        // Load V2 avatar CIDs
        const string v2CidSql = @"
            SELECT m.avatar, m.""metadataDigest""
            FROM (
                SELECT avatar, ""metadataDigest"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV2_UpdateMetadataDigest""
                WHERE ""blockNumber"" <= @toBlock
            ) m
            WHERE m.rn = 1";

        await using var v2CidCmd = new NpgsqlCommand(v2CidSql, conn);
        v2CidCmd.Parameters.AddWithValue("toBlock", toBlock);
        await using var v2CidReader = await v2CidCmd.ExecuteReaderAsync(ct);
        var v2CidCount = 0;

        while (await v2CidReader.ReadAsync(ct))
        {
            var avatar = v2CidReader.GetString(0);
            var cid = v2CidReader.GetString(1);

            var key = avatar.ToLowerInvariant();
            // Use toBlock as we're loading deduplicated state after event replay
            _caches.V2AvatarToCidMap.Add(toBlock, key, cid);

            v2CidCount++;
        }

        _logger.LogInformation("Loaded {Count} V2 avatar CIDs", v2CidCount);
    }

    private async Task LoadV2ShortNamesAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        _logger.LogInformation("Loading V2 short names...");

        // Load V2 short names (latest registration for each avatar)
        const string shortNameSql = @"
            SELECT s.avatar, s.""shortName""
            FROM (
                SELECT avatar, ""shortName"",
                    ROW_NUMBER() OVER (PARTITION BY avatar ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) as rn
                FROM ""CrcV2_RegisterShortName""
                WHERE ""blockNumber"" <= @toBlock
            ) s
            WHERE s.rn = 1";

        await using var cmd = new NpgsqlCommand(shortNameSql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0);
            // shortName is stored as numeric (BigInteger) in database
            var shortNameNumeric = BigInteger.Parse(reader.GetString(1));
            // Convert to Base58Btc format (like "zAlice")
            var shortNameBase58 = shortNameNumeric.ToBase58Btc();

            var key = avatar.ToLowerInvariant();
            // Use toBlock as we're loading deduplicated state after event replay
            _caches.V2AvatarToShortNameMap.Add(toBlock, key, shortNameBase58);

            count++;
        }

        _logger.LogInformation("Loaded {Count} V2 short names", count);
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

                var userKey = user.ToLowerInvariant();
                var tokenKey = token.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, userKey, ("Human", token));
                _caches.V1TokenOwnerByToken.Add(blockNumber, tokenKey, user);
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

                var orgKey = organization.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, orgKey, ("Organization", null));
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

                var avatarKey = avatar.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("Human", timestamp));
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

                var orgKey = organization.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, orgKey, ("Organization", timestamp));
            }
        }

        // Process V2 RegisterGroup
        const string groupSql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint"", r.""symbol""
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
                var symbol = groupReader.GetString(4);

                var groupKey = group.ToLowerInvariant();

                _caches.Groups.Add(blockNumber, groupKey, (name, mint, symbol));
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

                var avatarKey = avatar.ToLowerInvariant();

                _caches.Erc20WrapperAddresses.Add(blockNumber, avatarKey, erc20Wrapper);
            }
        }
    }

    /// <summary>
    /// Clears all caches to ensure a clean state for warmup replay.
    /// </summary>
    private void ClearCaches()
    {
        _caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        _caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        _caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        _caches.Erc20WrapperAddresses.Seed(new Dictionary<string, string>());
        _caches.Groups.Seed(new Dictionary<string, (string, string, string)>());
        _caches.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        _caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        _caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.LastTokenMovement.Seed(new Dictionary<string, long>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());
    }
}

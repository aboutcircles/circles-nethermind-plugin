using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Metrics;
using Circles.Common;
using Npgsql;
using System.Numerics;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that listens to PostgreSQL NOTIFY events from the Indexer.
/// Treats notifications as "pings" and queries the database directly for block information.
/// Uses BlockRingBuffer to detect chain reorganizations.
/// </summary>
public class NotificationListenerService : BackgroundService
{
    private readonly ILogger<NotificationListenerService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;

    protected CacheServiceSettings Settings => _settings;
    protected CacheServiceState State => _state;
    protected CacheContainer Caches => _caches;

    public NotificationListenerService(
        ILogger<NotificationListenerService> logger,
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
        // Wait for warmup to complete before starting listener
        while (!_state.WarmupComplete && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Waiting for cache warmup to complete before starting listener...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("Starting PostgreSQL LISTEN/NOTIFY listener on channel: {Channel}",
            _settings.PgNotifyChannel);

        while (!stoppingToken.IsCancellationRequested)
        {
            // If warmup was reset (e.g., due to recovery failure), wait for it to complete again
            while (!_state.WarmupComplete && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Warmup required. Pausing notification listener...");
                _state.ListenerConnected = false;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await ListenForNotificationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Notification listener shutting down...");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notification listener. Reconnecting in 5 seconds...");
                _state.ListenerConnected = false;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenForNotificationsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.PostgresConnectionString);
        await conn.OpenAsync(ct);

        conn.Notification += async (sender, args) =>
        {
            try
            {
                CacheMetrics.NotificationsReceived.Inc();
                await HandleNotificationAsync(args.Payload, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling notification: {Payload}", args.Payload);
            }
        };

        await using var cmd = new NpgsqlCommand($"LISTEN {_settings.PgNotifyChannel}", conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _state.ListenerConnected = true;
        _logger.LogInformation("Successfully connected to PostgreSQL LISTEN channel: {Channel}",
            _settings.PgNotifyChannel);

        // Keep connection alive and wait for notifications
        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);
        }
    }

    protected internal virtual async Task HandleNotificationAsync(string payload, CancellationToken ct)
    {
        _logger.LogDebug("Received notification ping");

        // Treat the notification as a ping - don't trust the payload content
        // Instead, query the database for the actual latest blocks
        List<(long BlockNumber, string BlockHash)> recentBlocks = new();

        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            recentBlocks = await GetRecentBlocksAsync(conn, _settings.RollbackCapacity, token);
        }, ct);

        if (recentBlocks.Count == 0)
        {
            _logger.LogWarning("No blocks found in System_Block table");
            return;
        }

        // Update the block ring buffer and detect any reorgs
        var reorgPoint = _state.BlockRingBuffer.UpdateFromBlocks(recentBlocks);

        if (reorgPoint.HasValue)
        {
            _logger.LogWarning(
                "Detected reorg at block {ReorgBlock}! Rolling back caches from block {RollbackBlock}...",
                reorgPoint.Value, reorgPoint.Value);

            // Track reorg in metrics
            CacheMetrics.ReorgsDetected.Inc();

            // Rollback all caches to the reorg point
            _caches.RollbackAll(reorgPoint.Value);

            // Rebuild secondary indexes after rollback
            _logger.LogInformation("Rebuilding secondary indexes after rollback...");
            _caches.RebuildSecondaryIndexes();

            // Update state
            _state.LastProcessedBlock = Math.Min(_state.LastProcessedBlock, reorgPoint.Value - 1);

            _logger.LogInformation("Rollback completed. Will reprocess from block {FromBlock}",
                reorgPoint.Value);
        }

        // Process any new blocks that we haven't processed yet
        var latestBlock = recentBlocks.Max(b => b.BlockNumber);
        var fromBlock = _state.LastProcessedBlock + 1;

        if (fromBlock <= latestBlock)
        {
            var blocksProcessed = latestBlock - fromBlock + 1;
            _logger.LogInformation("Processing block range {FromBlock} → {ToBlock} ({Count} blocks)",
                fromBlock, latestBlock, blocksProcessed);

            try
            {
                // Process new blocks
                await ProcessBlockRangeAsync(fromBlock, latestBlock, ct);

                _state.LastProcessedBlock = latestBlock;

                // Track blocks processed
                CacheMetrics.BlocksProcessed.Inc(blocksProcessed);

                _logger.LogInformation("Completed processing blocks {FromBlock} → {ToBlock}. Cache now at block {CurrentBlock}",
                    fromBlock, latestBlock, _state.LastProcessedBlock);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process block range {FromBlock} → {ToBlock}", fromBlock, latestBlock);

                // Attempt to rollback caches to restore consistency with _state.LastProcessedBlock
                // The rollback target is fromBlock (first unprocessed block), which restores state to
                // what it was after processing (LastProcessedBlock = fromBlock - 1)
                await AttemptRecoveryRollbackAsync(fromBlock, ct);

                throw;
            }
        }
        else
        {
            _logger.LogDebug("No new blocks to process (last processed: {LastProcessed}, latest: {Latest})",
                _state.LastProcessedBlock, latestBlock);
        }
    }

    /// <summary>
    /// Attempts to rollback all caches to restore consistency after a processing failure.
    /// If rollback succeeds, the caches will be back in sync with _state.LastProcessedBlock.
    /// If rollback fails (e.g., beyond rollback capacity), triggers a full re-warmup.
    /// </summary>
    private Task AttemptRecoveryRollbackAsync(long rollbackToBlock, CancellationToken ct)
    {
        try
        {
            _logger.LogWarning("Attempting recovery rollback to block {Block}...", rollbackToBlock);

            _caches.RollbackAll(rollbackToBlock);
            _caches.RebuildSecondaryIndexes();

            _logger.LogInformation("Recovery rollback to block {Block} succeeded. Caches are now consistent with LastProcessedBlock={LastProcessed}",
                rollbackToBlock, _state.LastProcessedBlock);
        }
        catch (InvalidOperationException rollbackEx)
        {
            // Cannot rollback beyond stored history (rollback capacity exceeded)
            // The only safe recovery is a full re-warmup
            _logger.LogError(rollbackEx,
                "Cannot rollback to block {Block} - beyond rollback capacity. Triggering full re-warmup...",
                rollbackToBlock);

            // Signal that warmup needs to be redone
            // This will cause the service to be unhealthy and the warmup service to restart
            _state.WarmupComplete = false;
            _state.LastProcessedBlock = 0;

            // Clear all caches to force clean re-warmup
            ClearAllCaches();

            _logger.LogWarning("Caches cleared. Service will re-warmup on next iteration.");
        }
        catch (Exception ex)
        {
            // Unexpected error during rollback - still try to trigger re-warmup
            _logger.LogError(ex, "Unexpected error during recovery rollback. Triggering full re-warmup...");

            _state.WarmupComplete = false;
            _state.LastProcessedBlock = 0;
            ClearAllCaches();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all cache data by re-seeding with empty dictionaries.
    /// Used when recovery rollback fails and a full re-warmup is required.
    /// </summary>
    private void ClearAllCaches()
    {
        _caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        _caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        _caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        _caches.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, int)>());
        _caches.Groups.Seed(new Dictionary<string, (string, string, string)>());
        _caches.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        _caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        _caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());

        _caches.RebuildSecondaryIndexes();
    }

    protected virtual async Task WithReadonlyConnectionAsync(
        Func<NpgsqlConnection, CancellationToken, Task> action,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
        await conn.OpenAsync(ct);
        await action(conn, ct);
    }

    /// <summary>
    /// Queries the most recent N blocks from the System_Block table.
    /// </summary>
    protected virtual async Task<List<(long BlockNumber, string BlockHash)>> GetRecentBlocksAsync(
        NpgsqlConnection conn, int count, CancellationToken ct)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            ORDER BY ""blockNumber"" DESC
            LIMIT @count";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("count", count);

        var blocks = new List<(long BlockNumber, string BlockHash)>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blockNumber = reader.GetInt64(0);
            var blockHashStr = reader.GetString(1);

            blocks.Add((blockNumber, blockHashStr));
        }

        // Reverse to get oldest-to-newest order
        blocks.Reverse();

        return blocks;
    }

    protected virtual async Task ProcessBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
        await conn.OpenAsync(ct);

        // Process V1 events in this range
        await ProcessV1EventsAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV1EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Signups
        const string humanSignupSql = @"
            SELECT s.""blockNumber"", s.""user"", s.""token""
            FROM ""CrcV1_Signup"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(humanSignupSql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;

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
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 human signups", count);
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

        var orgCount = 0;

        await using (var orgReader = await orgCmd.ExecuteReaderAsync(ct))
        {
            while (await orgReader.ReadAsync(ct))
            {
                var blockNumber = orgReader.GetInt64(0);
                var organization = orgReader.GetString(1);

                var orgKey = organization.ToLowerInvariant();

                _caches.V1Avatars.Add(blockNumber, orgKey, ("Organization", null));
                orgCount++;
            }
        }

        if (orgCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 organization signups", orgCount);
        }

        // Process V1 Transfers (for balance updates)
        await ProcessV1TransfersAsync(conn, fromBlock, toBlock, ct);

        // Process V1 Trust events
        await ProcessV1TrustAsync(conn, fromBlock, toBlock, ct);

        // Process V1 UpdateMetadataDigest (for CID maps)
        await ProcessV1UpdateMetadataDigestAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV2EventsAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterHuman
        await ProcessV2RegisterHumanAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterOrganization
        await ProcessV2RegisterOrganizationAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterGroup
        await ProcessV2RegisterGroupAsync(conn, fromBlock, toBlock, ct);

        // Process V2 Trust (for group memberships)
        await ProcessV2TrustAsync(conn, fromBlock, toBlock, ct);

        // Process V2 ERC20WrapperDeployed
        await ProcessV2Erc20WrapperDeployedAsync(conn, fromBlock, toBlock, ct);

        // Process V2 Transfers (for balance updates)
        await ProcessV2TransfersAsync(conn, fromBlock, toBlock, ct);

        // Process V2 ERC20 Wrapper Transfers (for balance updates)
        await ProcessV2Erc20WrapperTransfersAsync(conn, fromBlock, toBlock, ct);

        // Process V2 UpdateMetadataDigest (for CID maps)
        await ProcessV2UpdateMetadataDigestAsync(conn, fromBlock, toBlock, ct);

        // Process V2 RegisterShortName (for short name mappings)
        await ProcessV2RegisterShortNameAsync(conn, fromBlock, toBlock, ct);
    }

    private async Task ProcessV2RegisterHumanAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""avatar""
            FROM ""CrcV2_RegisterHuman"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var avatar = reader.GetString(2);

                var avatarKey = avatar.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, avatarKey, ("Human", timestamp));
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 human registrations", count);
        }
    }

    private async Task ProcessV2RegisterOrganizationAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""timestamp"", r.""organization""
            FROM ""CrcV2_RegisterOrganization"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var timestamp = reader.GetInt64(1);
                var organization = reader.GetString(2);

                var orgKey = organization.ToLowerInvariant();

                _caches.V2Avatars.Add(blockNumber, orgKey, ("Organization", timestamp));
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 organization registrations", count);
        }
    }

    private async Task ProcessV2RegisterGroupAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT r.""blockNumber"", r.""group"", r.""name"", r.""mint"", r.""symbol""
            FROM ""CrcV2_RegisterGroup"" r
            WHERE r.""blockNumber"" >= @fromBlock AND r.""blockNumber"" <= @toBlock
            ORDER BY r.""blockNumber"", r.""transactionIndex"", r.""logIndex""";

        var count = 0;
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("fromBlock", fromBlock);
            cmd.Parameters.AddWithValue("toBlock", toBlock);

            await using (var groupReader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await groupReader.ReadAsync(ct))
                {
                    var blockNumber = groupReader.GetInt64(0);
                    var group = groupReader.GetString(1);
                    var name = groupReader.GetString(2);
                    var mint = groupReader.GetString(3);
                    var symbol = groupReader.GetString(4);

                    var groupKey = group.ToLowerInvariant();

                    _caches.Groups.Add(blockNumber, groupKey, (name, mint, symbol));
                    count++;
                }
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 group registrations", count);
        }
    }

    private async Task ProcessV2TrustAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process all V2 Trust events - update both V2TrustRelations cache and GroupMemberships (when trustee is a group)
        const string sql = @"
            SELECT 
                t.""blockNumber"",
                t.""truster"",
                t.""trustee"",
                t.""expiryTime""
            FROM ""CrcV2_Trust"" t
            WHERE t.""blockNumber"" >= @fromBlock AND t.""blockNumber"" <= @toBlock
            ORDER BY t.""blockNumber"", t.""transactionIndex"", t.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var trustCount = 0;
        var membershipCount = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var truster = reader.GetString(1);
                var trustee = reader.GetString(2);
                var expiryTimeBig = reader.GetFieldValue<BigInteger>(3);

                var trusterKey = truster.ToLowerInvariant();
                var trusteeKey = trustee.ToLowerInvariant();
                var trustKey = $"{trusterKey}:{trusteeKey}";

                // Safely cast expiryTime to long
                long expiryLong = expiryTimeBig > long.MaxValue ? long.MaxValue : (long)expiryTimeBig;

                // Always update V2TrustRelations cache
                if (expiryTimeBig == 0)
                {
                    _caches.V2TrustRelations.Remove(trustKey);
                }
                else
                {
                    _caches.V2TrustRelations.Add(blockNumber, trustKey, expiryLong);
                }
                trustCount++;

                // Also update GroupMemberships if trustee is a group
                if (_caches.Groups.ContainsKey(trusteeKey))
                {
                    // Composite key: group:member
                    var membershipKey = $"{trusteeKey}:{trusterKey}";

                    if (expiryTimeBig == 0)
                    {
                        _caches.GroupMemberships.Remove(membershipKey);
                    }
                    else
                    {
                        _caches.GroupMemberships.Add(blockNumber, membershipKey, (truster, expiryLong));
                    }
                    membershipCount++;
                }
            }
        }

        if (trustCount > 0)
        {
            _logger.LogDebug("Processed {TrustCount} V2 trust events ({MembershipCount} group memberships)",
                trustCount, membershipCount);
        }
    }

    private async Task ProcessV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper"", e.""circlesType""
            FROM ""CrcV2_ERC20WrapperDeployed"" e
            WHERE e.""blockNumber"" >= @fromBlock AND e.""blockNumber"" <= @toBlock
            ORDER BY e.""blockNumber"", e.""transactionIndex"", e.""logIndex""";

        await using (var wrapperCmd = new NpgsqlCommand(wrapperSql, conn))
        {
            wrapperCmd.Parameters.AddWithValue("fromBlock", fromBlock);
            wrapperCmd.Parameters.AddWithValue("toBlock", toBlock);

            var count = 0;
            await using (var wrapperReader = await wrapperCmd.ExecuteReaderAsync(ct))
            {
                while (await wrapperReader.ReadAsync(ct))
                {
                    var blockNumber = wrapperReader.GetInt64(0);
                    var avatar = wrapperReader.GetString(1);
                    var erc20Wrapper = wrapperReader.GetString(2);
                    var circlesType = wrapperReader.GetInt32(3);

                    // Key by wrapper address (not avatar) to support avatars with multiple wrappers
                    var wrapperKey = erc20Wrapper.ToLowerInvariant();

                    _caches.Erc20WrapperAddresses.Add(blockNumber, wrapperKey, (avatar.ToLowerInvariant(), circlesType));
                    count++;
                }
            }

            if (count > 0)
            {
                _logger.LogDebug("Processed {Count} V2 ERC20 wrapper deployments", count);
            }
        }
    }

    private async Task ProcessV1TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 transfers incrementally
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount, ""blockNumber""
            FROM ""CrcV1_Transfer""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var transferCount = 0;
        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var amountBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                var tokenKey = tokenAddress.ToLowerInvariant();
                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal value;
                try
                {
                    value = CirclesConverter.AttoCirclesToCircles(amountBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping V1 transfer with amount {Amount} that would overflow decimal", amountBig);
                    continue;
                }

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Add balances for the previous block
                        foreach (var kvp in currentBalances)
                        {
                            _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                            // Update secondary index for fast lookups
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: true, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V1BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= value;
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V1BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += value;
                }

                transferCount++;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V1BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                // Update secondary index for fast lookups
                _caches.UpdateBalanceIndex(kvp.Key, isV1: true, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 transfer events", transferCount);
        }
    }

    private async Task ProcessV2TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 TransferSingle and TransferBatch events together in block order
        const string sql = @"
            SELECT * FROM (
                SELECT
                    ""from"",
                    ""to"",
                    id,
                    value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    'Single' as type
                FROM ""CrcV2_TransferSingle""
                WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock

                UNION ALL

                SELECT
                    ""from"",
                    ""to"",
                    id,
                    value,
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    'Batch' as type
                FROM ""CrcV2_TransferBatch""
                WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ) AS combined
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var transferCount = 0;
        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenId = reader.GetFieldValue<BigInteger>(2).ToString();
                var valueBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal amount;
                try
                {
                    amount = CirclesConverter.AttoCirclesToCircles(valueBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping V2 transfer with value {Value} that would overflow decimal", valueBig);
                    continue;
                }

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Add balances for the previous block
                        foreach (var kvp in currentBalances)
                        {
                            _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                            // Update secondary index for fast lookups
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= amount;
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += amount;
                }

                transferCount++;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                // Update secondary index for fast lookups
                _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V2 transfer events", transferCount);
        }
    }

    private async Task ProcessV2Erc20WrapperTransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 ERC20 Wrapper Transfer events incrementally
        const string sql = @"
            SELECT ""from"", ""to"", ""tokenAddress"", amount, ""blockNumber""
            FROM ""CrcV2_Erc20WrapperTransfer""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var transferCount = 0;
        var currentBalances = new Dictionary<string, decimal>();
        long currentBlock = -1;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var amountBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                // Convert from wei (18 decimals) to token units using CirclesConverter for proper precision
                decimal amount;
                try
                {
                    amount = CirclesConverter.AttoCirclesToCircles(amountBig);
                }
                catch (OverflowException)
                {
                    _logger.LogWarning("Skipping ERC20 wrapper transfer with amount {Amount} that would overflow decimal", amountBig);
                    continue;
                }

                var tokenKey = tokenAddress.ToLowerInvariant();

                if (blockNumber != currentBlock)
                {
                    if (currentBlock != -1)
                    {
                        // Add balances for the previous block
                        foreach (var kvp in currentBalances)
                        {
                            _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                            // Update secondary index for fast lookups
                            _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
                        }
                    }
                    currentBlock = blockNumber;
                }

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(fromKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var existingBalance);
                        currentBalances[fromKey] = existingBalance;
                    }
                    currentBalances[fromKey] -= amount;
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                    // Initialize from cache if we haven't seen this key yet in this block range
                    if (!currentBalances.ContainsKey(toKey))
                    {
                        _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var existingBalance);
                        currentBalances[toKey] = existingBalance;
                    }
                    currentBalances[toKey] += amount;
                }

                transferCount++;
            }
        }

        // Add balances for the last block
        if (currentBlock != -1)
        {
            foreach (var kvp in currentBalances)
            {
                _caches.V2BalancesByAccountAndToken.Add(currentBlock, kvp.Key, kvp.Value);
                // Update secondary index for fast lookups
                _caches.UpdateBalanceIndex(kvp.Key, isV1: false, kvp.Value);
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} ERC20 wrapper transfer events", transferCount);
        }
    }

    private async Task ProcessV1TrustAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 Trust events - update V1TrustRelations cache
        const string sql = @"
            SELECT t.""blockNumber"", t.""canSendTo"" as truster, t.""user"" as trustee, t.""limit""
            FROM ""CrcV1_Trust"" t
            WHERE t.""blockNumber"" >= @fromBlock AND t.""blockNumber"" <= @toBlock
            ORDER BY t.""blockNumber"", t.""transactionIndex"", t.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var truster = reader.GetString(1);
                var trustee = reader.GetString(2);
                var limitBig = reader.GetFieldValue<BigInteger>(3);

                var key = $"{truster.ToLowerInvariant()}:{trustee.ToLowerInvariant()}";

                // If limit is 0, remove the trust relation; otherwise add/update it
                if (limitBig == 0)
                {
                    _caches.V1TrustRelations.Remove(key);
                }
                else
                {
                    // V1 trust doesn't have expiry, store 0 as indicator of active trust
                    _caches.V1TrustRelations.Add(blockNumber, key, 0L);
                }

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 trust events", count);
        }
    }

    private async Task ProcessV1UpdateMetadataDigestAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V1 UpdateMetadataDigest events - update V1AvatarToCidMap cache
        const string sql = @"
            SELECT m.""blockNumber"", m.avatar, m.""metadataDigest""
            FROM ""CrcV1_UpdateMetadataDigest"" m
            WHERE m.""blockNumber"" >= @fromBlock AND m.""blockNumber"" <= @toBlock
            ORDER BY m.""blockNumber"", m.""transactionIndex"", m.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1);
                var metadataDigest = (byte[])reader.GetValue(2);

                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                var key = avatar.ToLowerInvariant();

                _caches.V1AvatarToCidMap.Add(blockNumber, key, cid);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V1 metadata digest updates", count);
        }
    }

    private async Task ProcessV2UpdateMetadataDigestAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 UpdateMetadataDigest events - update V2AvatarToCidMap cache
        const string sql = @"
            SELECT m.""blockNumber"", m.avatar, m.""metadataDigest""
            FROM ""CrcV2_UpdateMetadataDigest"" m
            WHERE m.""blockNumber"" >= @fromBlock AND m.""blockNumber"" <= @toBlock
            ORDER BY m.""blockNumber"", m.""transactionIndex"", m.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1);
                var metadataDigest = (byte[])reader.GetValue(2);

                var cid = CidHelper.MetadataDigestToCidV0(metadataDigest);
                var key = avatar.ToLowerInvariant();

                _caches.V2AvatarToCidMap.Add(blockNumber, key, cid);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 metadata digest updates", count);
        }
    }

    private async Task ProcessV2RegisterShortNameAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 RegisterShortName events - update V2AvatarToShortNameMap cache
        const string sql = @"
            SELECT s.""blockNumber"", s.avatar, s.""shortName""
            FROM ""CrcV2_RegisterShortName"" s
            WHERE s.""blockNumber"" >= @fromBlock AND s.""blockNumber"" <= @toBlock
            ORDER BY s.""blockNumber"", s.""transactionIndex"", s.""logIndex""";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);

        var count = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var blockNumber = reader.GetInt64(0);
                var avatar = reader.GetString(1);
                // shortName is stored as numeric (BigInteger) in database
                var shortNameNumeric = reader.GetFieldValue<BigInteger>(2);
                // Convert to Base58Btc format (like "zAlice")
                var shortNameBase58 = shortNameNumeric.ToBase58Btc();

                var key = avatar.ToLowerInvariant();

                _caches.V2AvatarToShortNameMap.Add(blockNumber, key, shortNameBase58);

                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 short name registrations", count);
        }
    }
}

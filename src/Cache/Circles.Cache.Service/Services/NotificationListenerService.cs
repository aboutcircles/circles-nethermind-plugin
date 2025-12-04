using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Metrics;
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

    private async Task HandleNotificationAsync(string payload, CancellationToken ct)
    {
        _logger.LogDebug("Received notification ping");

        // Treat the notification as a ping - don't trust the payload content
        // Instead, query the database for the actual latest blocks

        await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
        await conn.OpenAsync(ct);

        // Query the last N blocks from System_Block (where N = rollback capacity)
        var recentBlocks = await GetRecentBlocksAsync(conn, _settings.RollbackCapacity, ct);

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

            // Process new blocks
            await ProcessBlockRangeAsync(fromBlock, latestBlock, ct);

            _state.LastProcessedBlock = latestBlock;

            // Track blocks processed
            CacheMetrics.BlocksProcessed.Inc(blocksProcessed);

            _logger.LogInformation("✓ Completed processing blocks {FromBlock} → {ToBlock}. Cache now at block {CurrentBlock}",
                fromBlock, latestBlock, _state.LastProcessedBlock);
        }
        else
        {
            _logger.LogDebug("No new blocks to process (last processed: {LastProcessed}, latest: {Latest})",
                _state.LastProcessedBlock, latestBlock);
        }
    }

    /// <summary>
    /// Queries the most recent N blocks from the System_Block table.
    /// </summary>
    private async Task<List<(long BlockNumber, string BlockHash)>> GetRecentBlocksAsync(
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

    private async Task ProcessBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
        await conn.OpenAsync(ct);

        // Process V1 events in this range
        await ProcessV1EventsAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsAsync(conn, fromBlock, toBlock, ct);

        _logger.LogInformation("Successfully processed blocks {FromBlock} to {ToBlock}", fromBlock, toBlock);
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
        // Query V_CrcV2_GroupMemberships to get updated trust relationships for group memberships
        // We only care about trusts where the trustee is a group (creates membership)
        const string sql = @"
            SELECT 
                t.""blockNumber"",
                t.""truster"" as member,
                t.""trustee"" as ""group"",
                t.""expiryTime""
            FROM ""CrcV2_Trust"" t
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
                var member = reader.GetString(1);
                var group = reader.GetString(2);

                var expiryTime = reader.GetDecimal(3);


                // Check if trustee is a group by looking it up in the Groups cache
                var groupKey = group.ToLowerInvariant();

                if (_caches.Groups.ContainsKey(groupKey))
                {
                    // Composite key: group:member
                    var key = $"{groupKey}:{member.ToLowerInvariant()}";

                    // If expiryTime is 0, this is an untrust - use Remove to delete the membership
                    // Otherwise, add/update the membership
                    if (expiryTime == 0)
                    {
                        _caches.GroupMemberships.Remove(key);
                    }
                    else
                    {
                        // Safely cast expiryTime to long, capping at long.MaxValue for overflow
                        long expiryLong = expiryTime > long.MaxValue ? long.MaxValue : (long)expiryTime;
                        _caches.GroupMemberships.Add(blockNumber, key, (member, expiryLong));
                    }

                    count++;
                }
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Processed {Count} V2 trust events for group memberships", count);
        }
    }

    private async Task ProcessV2Erc20WrapperDeployedAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string wrapperSql = @"
            SELECT e.""blockNumber"", e.""avatar"", e.""erc20Wrapper""
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

                    var avatarKey = avatar.ToLowerInvariant();

                    _caches.Erc20WrapperAddresses.Add(blockNumber, avatarKey, erc20Wrapper);
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
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenAddress = reader.GetString(2);
                var amount = reader.GetDecimal(3);
                var blockNumber = reader.GetInt64(4);

                var tokenKey = tokenAddress.ToLowerInvariant();
                var value = amount / 1_000_000_000_000_000_000m;

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenKey}";
                    var currentBalance = _caches.V1BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                    _caches.V1BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - value);
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenKey}";
                    var currentBalance = _caches.V1BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                    _caches.V1BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + value);
                }

                transferCount++;
            }
        }

        if (transferCount > 0)
        {
            _logger.LogDebug("Processed {Count} V1 transfer events", transferCount);
        }
    }

    private async Task ProcessV2TransfersAsync(NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        // Process V2 TransferSingle events incrementally
        const string singleSql = @"
            SELECT ""from"", ""to"", id, value, ""blockNumber""
            FROM ""CrcV2_TransferSingle""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var singleCmd = new NpgsqlCommand(singleSql, conn);
        singleCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        singleCmd.Parameters.AddWithValue("toBlock", toBlock);

        var transferCount = 0;
        await using (var reader = await singleCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenId = reader.GetFieldValue<BigInteger>(2).ToString();
                var valueBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                var divisor = BigInteger.Parse("1000000000000000000");
                var amountBig = valueBig / divisor;

                if (amountBig > (BigInteger)decimal.MaxValue || amountBig < (BigInteger)decimal.MinValue)
                {
                    _logger.LogWarning("Skipping V2 transfer with value {Value} that would overflow decimal", valueBig);
                    continue;
                }

                var amount = (decimal)amountBig;

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                    var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                    _caches.V2BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - amount);
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
                    var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                    _caches.V2BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + amount);
                }

                transferCount++;
            }
        }

        // Process V2 TransferBatch events
        const string batchSql = @"
            SELECT ""from"", ""to"", id, value, ""blockNumber""
            FROM ""CrcV2_TransferBatch""
            WHERE ""blockNumber"" >= @fromBlock AND ""blockNumber"" <= @toBlock
            ORDER BY ""blockNumber"", ""transactionIndex"", ""logIndex""";

        await using var batchCmd = new NpgsqlCommand(batchSql, conn);
        batchCmd.Parameters.AddWithValue("fromBlock", fromBlock);
        batchCmd.Parameters.AddWithValue("toBlock", toBlock);

        var batchCount = 0;
        await using (var reader = await batchCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var tokenId = reader.GetFieldValue<BigInteger>(2).ToString();
                var valueBig = reader.GetFieldValue<BigInteger>(3);
                var blockNumber = reader.GetInt64(4);

                var divisor = BigInteger.Parse("1000000000000000000");
                var amountBig = valueBig / divisor;

                if (amountBig > (BigInteger)decimal.MaxValue || amountBig < (BigInteger)decimal.MinValue)
                {
                    _logger.LogWarning("Skipping V2 batch transfer with value {Value} that would overflow decimal", valueBig);
                    continue;
                }

                var amount = (decimal)amountBig;

                // Update sender balance
                if (from != "0x0000000000000000000000000000000000000000")
                {
                    var fromKey = $"{from.ToLowerInvariant()}:{tokenId}";
                    var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(fromKey, out var bal) ? bal : 0m;
                    _caches.V2BalancesByAccountAndToken.Add(blockNumber, fromKey, currentBalance - amount);
                }

                // Update receiver balance
                if (to != "0x0000000000000000000000000000000000000000")
                {
                    var toKey = $"{to.ToLowerInvariant()}:{tokenId}";
                    var currentBalance = _caches.V2BalancesByAccountAndToken.TryGetValue(toKey, out var bal) ? bal : 0m;
                    _caches.V2BalancesByAccountAndToken.Add(blockNumber, toKey, currentBalance + amount);
                }

                batchCount++;
            }
        }

        if (transferCount > 0 || batchCount > 0)
        {
            _logger.LogDebug("Processed {SingleCount} TransferSingle + {BatchCount} TransferBatch events",
                transferCount, batchCount);
        }
    }
}

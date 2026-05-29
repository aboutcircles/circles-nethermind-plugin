using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Metrics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Background service that listens to PostgreSQL NOTIFY events from the Indexer.
/// Treats notifications as "pings" and queries the database directly for block information.
/// Uses BlockRingBuffer to detect chain reorganizations.
///
/// The implementation is split across multiple partial-class files for maintainability:
/// - NotificationListenerService.cs                       — fields, ctor, lifecycle (ExecuteAsync, ListenForNotificationsAsync, HandleNotificationAsync), recovery (AttemptRecoveryRollbackAsync, ClearAllCaches, TriggerFullRewarmup), shared helpers (GetBlockTimestampAsync, WithReadonlyConnectionAsync, GetRecentBlocksAsync), top-level dispatch (ProcessBlockRangeAsync)
/// - Listeners/NotificationListenerService.V1Events.cs    — ProcessV1EventsAsync (incl. inline signups), ProcessV1TransfersAsync, ProcessV1TrustAsync, ProcessV1UpdateMetadataDigestAsync
/// - Listeners/NotificationListenerService.V2Registry.cs  — ProcessV2EventsAsync, ProcessV2RegisterHumanAsync, ProcessV2RegisterOrganizationAsync, ProcessV2RegisterGroupAsync
/// - Listeners/NotificationListenerService.V2Trust.cs     — ProcessV2TrustAsync
/// - Listeners/NotificationListenerService.V2Wrappers.cs  — ProcessV2Erc20WrapperDeployedAsync
/// - Listeners/NotificationListenerService.V2Transfers.cs — ProcessV2TransfersAsync, ProcessV2Erc20WrapperTransfersAsync
/// - Listeners/NotificationListenerService.V2Metadata.cs  — ProcessV2UpdateMetadataDigestAsync, ProcessV2RegisterShortNameAsync, ProcessV2SetAdvancedUsageFlagAsync
/// </summary>
public partial class NotificationListenerService : BackgroundService
{
    private readonly ILogger<NotificationListenerService> _logger;
    private readonly CacheServiceSettings _settings;
    private readonly CacheServiceState _state;
    private readonly CacheContainer _caches;
    private readonly NpgsqlDataSource _readonlyDataSource;
    private readonly IRegistrationSet _registrations;
    private readonly IWrapperLookup _wrapperLookup;

    // Serializes notification handling — Npgsql's conn.Notification callback is async void,
    // so overlapping notifications can fire concurrent HandleNotificationAsync calls. Without
    // serialization, two handlers can read the same LastProcessedBlock, compute overlapping
    // block ranges, and double-apply cache mutations (or trigger spurious re-warmups from
    // RollbackCache.Add throwing on non-monotonic block numbers).
    private readonly SemaphoreSlim _notificationGate = new(1, 1);

    protected CacheServiceSettings Settings => _settings;
    protected CacheServiceState State => _state;
    protected CacheContainer Caches => _caches;

    public NotificationListenerService(
        ILogger<NotificationListenerService> logger,
        CacheServiceSettings settings,
        CacheServiceState state,
        CacheContainer caches,
        NpgsqlDataSource readonlyDataSource)
    {
        _logger = logger;
        _settings = settings;
        _state = state;
        _caches = caches;
        _readonlyDataSource = readonlyDataSource;
        _registrations = new CacheRegistrationSet(caches);
        _wrapperLookup = new CacheWrapperLookup(caches);
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
        // Use 5-minute timeout cycle to send keepalive queries, preventing idle_session_timeout (10min)
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                await conn.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout reached, not shutdown - send keepalive query to reset PostgreSQL's idle timer
                await using var keepalive = new NpgsqlCommand("SELECT 1", conn);
                await keepalive.ExecuteNonQueryAsync(ct);
                _logger.LogDebug("Sent keepalive query to prevent idle_session_timeout");
            }
        }
    }

    protected internal virtual async Task HandleNotificationAsync(string payload, CancellationToken ct)
    {
        // Skip processing if warmup is in progress. The async notification callback
        // can fire even after TriggerFullRewarmup() sets WarmupComplete=false, because
        // ListenForNotificationsAsync is still waiting on conn.WaitAsync(). Processing
        // blocks now would race with InitializeBlockRingBufferAsync (adding newer blocks
        // to the buffer that warmup will then fail to initialize with older blocks).
        if (!_state.WarmupComplete)
        {
            _logger.LogDebug("Skipping notification - warmup in progress");
            return;
        }

        // Serialize notification handling. Npgsql's conn.Notification callback is async void,
        // so a second notification can fire while we're awaiting DB queries in the first handler.
        // Without this gate, concurrent handlers double-apply balance deltas and corrupt state.
        await _notificationGate.WaitAsync(ct);
        try
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
                if (reorgPoint.Value <= _state.WarmupTargetBlock)
                {
                    _logger.LogWarning(
                        "Detected reorg at block {ReorgBlock} crossing warmup seed boundary {WarmupTarget}. Triggering full re-warmup.",
                        reorgPoint.Value,
                        _state.WarmupTargetBlock);

                    CacheMetrics.ReorgsDetected.Inc();
                    TriggerFullRewarmup();
                    return;
                }

                _logger.LogWarning(
                    "Detected reorg at block {ReorgBlock}! Rolling back caches from block {RollbackBlock}...",
                    reorgPoint.Value, reorgPoint.Value);

                // Track reorg in metrics
                CacheMetrics.ReorgsDetected.Inc();

                // Rollback all caches to the reorg point. If the reorg is deeper than
                // RollbackCapacity, RollbackAll throws InvalidOperationException — in that
                // case the only safe recovery is a full re-warmup.
                try
                {
                    _caches.RollbackAll(reorgPoint.Value);

                    // Rebuild secondary indexes after rollback
                    _logger.LogInformation("Rebuilding secondary indexes after rollback...");
                    _caches.RebuildSecondaryIndexes();

                    // Update state
                    _state.LastProcessedBlock = Math.Min(_state.LastProcessedBlock, reorgPoint.Value - 1);

                    _logger.LogInformation("Rollback completed. Will reprocess from block {FromBlock}",
                        reorgPoint.Value);
                }
                catch (InvalidOperationException rollbackEx)
                {
                    _logger.LogError(rollbackEx,
                        "Reorg at block {ReorgBlock} exceeds rollback capacity. Triggering full re-warmup.",
                        reorgPoint.Value);

                    TriggerFullRewarmup();
                    return;
                }
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
                    _state.CurrentBlockTimestamp = await GetBlockTimestampAsync(latestBlock, ct);

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
        finally
        {
            _notificationGate.Release();
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

            TriggerFullRewarmup();

            _logger.LogWarning("Caches cleared. Service will re-warmup on next iteration.");
        }
        catch (Exception ex)
        {
            // Unexpected error during rollback - still try to trigger re-warmup
            _logger.LogError(ex, "Unexpected error during recovery rollback. Triggering full re-warmup...");

            TriggerFullRewarmup();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all cache data by re-seeding with empty dictionaries.
    /// Used when recovery rollback fails and a full re-warmup is required.
    /// </summary>
    private void ClearAllCaches()
    {
        // Note: BlockRingBuffer is cleared by RewarmupReset.Trigger before this callback.
        // Do not clear it here to avoid confusing double-clear.

        _caches.V1Avatars.Seed(new Dictionary<string, (string, string?)>());
        _caches.V1TokenOwnerByToken.Seed(new Dictionary<string, string>());
        _caches.V1AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2Avatars.Seed(new Dictionary<string, (string, long)>());
        _caches.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, CirclesType)>());
        _caches.Groups.Seed(new Dictionary<string, (string, string, string)>());
        _caches.GroupMemberships.Seed(new Dictionary<string, (string, long)>());
        _caches.V2AvatarToCidMap.Seed(new Dictionary<string, string>());
        _caches.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>());
        _caches.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>());
        _caches.V2LastActivity.Seed(new Dictionary<string, long>());
        _caches.V1TrustRelations.Seed(new Dictionary<string, long>());
        _caches.V2TrustRelations.Seed(new Dictionary<string, long>());
        _caches.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>());

        _caches.RebuildSecondaryIndexes();
    }

    private void TriggerFullRewarmup()
    {
        RewarmupReset.Trigger(_state, ClearAllCaches);
    }

    protected virtual async Task<long> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
    {
        long timestamp = 0;

        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            const string sql = @"
                SELECT COALESCE(MAX(""timestamp""), 0)
                FROM ""System_Block""
                WHERE ""blockNumber"" <= @blockNumber";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("blockNumber", blockNumber);
            var result = await cmd.ExecuteScalarAsync(token);
            timestamp = result switch
            {
                long l => l,
                int i => i,
                _ => 0L
            };
        }, ct);

        return timestamp;
    }

    protected virtual async Task WithReadonlyConnectionAsync(
        Func<NpgsqlConnection, CancellationToken, Task> action,
        CancellationToken ct)
    {
        await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
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
        await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);

        // Process V1 events in this range
        await ProcessV1EventsAsync(conn, fromBlock, toBlock, ct);

        // Process V2 events in this range
        await ProcessV2EventsAsync(conn, fromBlock, toBlock, ct);
    }
}

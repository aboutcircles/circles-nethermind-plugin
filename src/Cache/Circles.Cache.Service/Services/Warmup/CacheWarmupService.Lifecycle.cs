using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Lifecycle and phase orchestration for CacheWarmupService.
/// Hosts the BackgroundService loop, the three-phase warmup iteration, database readiness
/// probing, NOTIFY-driven initial-sync detection, and small timing helpers shared by the
/// parallel fan-out tasks.
/// </summary>
public partial class CacheWarmupService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting cache warmup service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for warmup to be needed (either initially or after a recovery reset)
                while (_state.WarmupComplete && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    await RunWarmupIterationAsync(stoppingToken);
                    // Don't break - keep monitoring for re-warmup requests
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cache warmup failed");

                    var now = DateTime.UtcNow;
                    if (now - _lastReminderLogTime >= _reminderInterval)
                    {
                        _logger.LogWarning("Cache warmup failed. Service will remain unhealthy until manual restart or DB issue is resolved.");
                        _lastReminderLogTime = now;
                    }

                    try
                    {
                        await DelayAfterFailureAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Cache warmup service stopped");
    }

    protected virtual async Task RunWarmupIterationAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting cache warmup...");

        await WaitForDatabaseAsync(stoppingToken);

        long warmupTarget = 0;

        // Phase 1: Wait for database and initial sync on a shared connection
        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            await WaitForInitialSyncAsync(conn, token);
            ClearCaches();
            warmupTarget = await GetDatabaseHeadAsync(conn, token);
        }, stoppingToken);

        _state.WarmupTargetBlock = warmupTarget;
        _logger.LogInformation("Starting warmup replay up to block {Block}...", warmupTarget);

        // Phase 2: Load all data in parallel — each task opens its own pooled connection
        var warmupSw = System.Diagnostics.Stopwatch.StartNew();

        long warmupTargetTimestamp = 0;

        await using (var snapshot = await CreateWarmupSnapshotAsync(stoppingToken))
        {
            await Task.WhenAll(
                TimedLoadAsync("V1 events", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => ReplayV1EventsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("V2 events", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => ReplayV2EventsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("group memberships", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadGroupMembershipsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("trust relations", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadTrustRelationsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("consented flow flags", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadConsentedFlowFlagsAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("avatar metadata", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadAvatarMetadataAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("short names", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadV2ShortNamesAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("balances", ct => WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId,
                    (c, t) => LoadBalancesAsync(c, warmupTarget, t), ct), stoppingToken),
                TimedLoadAsync("warmup target timestamp", async ct =>
                {
                    await WithSnapshotReadonlyConnectionAsync(snapshot.SnapshotId, async (c, t) =>
                    {
                        warmupTargetTimestamp = await GetMaxTimestampUpToBlockAsync(c, warmupTarget, t);
                    }, ct);
                }, stoppingToken)
            );
        }

        warmupSw.Stop();
        _logger.LogInformation("All data loaded in {Elapsed:n1}s (parallel)", warmupSw.Elapsed.TotalSeconds);

        _logger.LogInformation("Rebuilding secondary indexes...");
        _caches.RebuildSecondaryIndexes();
        _logger.LogInformation("Secondary indexes rebuilt");

        _state.LastProcessedBlock = warmupTarget;
        _logger.LogInformation("========================================");
        _logger.LogInformation("✓ Warmup replay completed at block {Block}", warmupTarget);
        _logger.LogInformation("========================================");

        // Phase 3: Initialize ring buffer and catch up on a shared connection
        var finalHeadTimestamp = warmupTargetTimestamp;
        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            await InitializeBlockRingBufferAsync(conn, warmupTarget, token);

            var currentHead = await GetDatabaseHeadAsync(conn, token);
            if (currentHead > warmupTarget)
            {
                _logger.LogInformation(
                    "New blocks arrived during warmup ({WarmupTarget} -> {CurrentHead}). Processing gap...",
                    warmupTarget, currentHead);

                await ProcessBlockGapAsync(conn, warmupTarget + 1, currentHead, token);

                _state.LastProcessedBlock = currentHead;
                finalHeadTimestamp = await GetMaxTimestampUpToBlockAsync(conn, currentHead, token);
                _logger.LogInformation("Processed {Count} blocks that arrived during warmup",
                    currentHead - warmupTarget);
            }
            else
            {
                _logger.LogInformation("No new blocks arrived during warmup");
            }
        }, stoppingToken);

        _state.CurrentBlockTimestamp = finalHeadTimestamp;

        _state.WarmupComplete = true;
        _lastReminderLogTime = DateTime.MinValue;

        _logger.LogInformation("Cache warmup completed successfully");
    }

    private async Task TimedLoadAsync(string name, Func<CancellationToken, Task> load, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Loading {Name}...", name);
        await load(ct);
        sw.Stop();
        _logger.LogInformation("Loaded {Name} in {Elapsed:n1}s", name, sw.Elapsed.TotalSeconds);
    }

    protected virtual Task DelayAfterFailureAsync(CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(30), ct);

    protected virtual async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for PostgreSQL to be ready...");

        const int maxRetries = 60;
        const int delayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using var conn = await _readonlyDataSource.OpenConnectionAsync(ct);
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
    protected virtual async Task WaitForInitialSyncAsync(NpgsqlConnection conn, CancellationToken ct)
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

        // Create a cancellation source that we can use to cancel the WaitAsync when needed
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        const int waitLogIntervalSeconds = 300;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for notification with periodic logging
                // Use a fresh cancellation token for each wait iteration
                using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                iterationCts.CancelAfter(TimeSpan.FromSeconds(waitLogIntervalSeconds));

                try
                {
                    // WaitAsync will return when a notification is received or when cancelled
                    await conn.WaitAsync(iterationCts.Token);

                    // If we get here without cancellation, check if notification was received
                    if (notificationReceived.Task.IsCompleted)
                    {
                        _logger.LogInformation("Initial sync confirmed complete via NOTIFY event");

                        // Unlisten to free the connection for subsequent operations
                        await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN {_settings.PgNotifyChannel}", conn);
                        await unlistenCmd.ExecuteNonQueryAsync(ct);

                        return;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This is expected - periodic timeout for logging
                    // The iteration CTS timed out, but the main CT is still valid
                    _logger.LogInformation("Still waiting for first NOTIFY event on channel '{Channel}'...",
                        _settings.PgNotifyChannel);
                }
            }
        }
        finally
        {
            // Cancel any in-progress WaitAsync before trying to UNLISTEN
            // This is important because UNLISTEN cannot execute while connection is in 'Waiting' state
            await waitCts.CancelAsync();

            // Give a small delay for the connection to exit the waiting state
            await Task.Delay(100, CancellationToken.None);

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

    protected virtual async Task<long> GetDatabaseHeadAsync(NpgsqlConnection conn, CancellationToken ct)
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

    private static async Task<long> GetMaxTimestampUpToBlockAsync(NpgsqlConnection conn, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(""timestamp""), 0)
            FROM ""System_Block""
            WHERE ""blockNumber"" <= @toBlock";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        var result = await cmd.ExecuteScalarAsync(ct);

        return result switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };
    }
}

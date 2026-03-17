using System.Text.Json;
using Circles.Common;
// using Circles.Rpc;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Npgsql;

namespace Circles.Index;

public class StateMachine(
    Context context,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    CancellationToken cancellationToken)
{
    public interface IEvent;

    public record NewHead(long Head) : IEvent;

    public enum State
    {
        New,
        Initial,
        Syncing,
        NotifySubscribers,
        Reorg,
        WaitForNewBlock,
        Error,
        End
    }

    private record EnterState : IEvent;

    private record EnterState<TArg>(TArg Arg) : EnterState;

    private record LeaveState : IEvent;

    private List<Exception> Errors { get; } = new();

    private State CurrentState { get; set; } = State.New;

    /// <summary>
    /// Returns true if the state machine is in a state that can accept new block processing.
    /// Used to prevent starting ProcessBlocksAsync when in Error state (which handles its own recovery).
    /// </summary>
    public bool CanProcessNewBlocks => CurrentState is State.WaitForNewBlock or State.Syncing or State.NotifySubscribers;

    // Status logging
    private DateTime _lastStatusLog = DateTime.UtcNow;
    private long _blocksProcessedSinceLastLog = 0;
    private long _totalBlocksProcessed = 0;

    // Track if REINDEX_FROM_BLOCK cleanup was already performed (to avoid re-running on retries)
    private bool _reindexCleanupDone;

    // Transient sync error tracking (block/receipts not yet available during node sync).
    // Tracked separately from Errors list so transient retries don't count toward the
    // permanent error limit and vice versa.
    private int _consecutiveTransientErrors;
    private DateTime? _transientErrorStartedAt;

    // Track if we've sent at least one pg_notify to signal "live mode" to subscribers (e.g., cache service).
    // During initial sync, large batches (>1000 blocks) skip notifications to avoid flooding.
    // This flag ensures we send at least one notification when entering WaitForNewBlock,
    // so subscribers know sync is complete and they can start warming up.
    private bool _hasSentLiveNotification;

    // Bulk sync mode: when the gap between chain head and indexer head is large (>10K blocks),
    // caches operate in BulkMode (no locks, no diff tracking) for maximum throughput.
    // On transition to live mode, caches are re-seeded from DB and BulkMode is disabled.
    private bool _inBulkSyncMode;
    private const long BulkSyncThreshold = 10_000;
    private const long BulkSyncExitThreshold = 1_000;

    public async Task HandleEvent(IEvent e)
    {
        try
        {
            switch (CurrentState)
            {
                case State.New:
                    return;

                case State.Initial:
                    switch (e)
                    {
                        case EnterState:
                            {
                                // Handle REINDEX_FROM_BLOCK for full reindexing (only once per process lifetime)
                                //
                                // IMPORTANT: Partial table reindexing is NOT supported because it creates data
                                // inconsistency between tables and caches. All tables must be at the same block height.
                                //
                                // Use REINDEX_FROM_BLOCK=12000000 to reindex ALL tables from that block.
                                // Remove the env var after reindexing completes to avoid re-deleting data on restart.

                                if (!_reindexCleanupDone && context.Settings.ReindexFromBlock.HasValue)
                                {
                                    var reindexFromBlock = context.Settings.ReindexFromBlock.Value;
                                    context.Logger.Info($"[REINDEX] Reindexing ALL tables from block {reindexFromBlock:N0}");

                                    // Delete from all tables
                                    await context.Database.DeleteAllGreaterOrEqualBlock(reindexFromBlock);

                                    context.Logger.Info("[REINDEX] Data deletion complete. Will resync from specified block.");
                                    _reindexCleanupDone = true;
                                }

                                // Determine the effective resume point by checking both System_Block and all event tables.
                                // This is critical: we must use the SAME block for cleanup and sync start to avoid
                                // cache conflicts (caches track block numbers and require monotonically increasing blocks).
                                context.Logger.Info("Initializing: Finding the safe resume point...");

                                var latestBlock = context.Database.LatestBlock() ?? 0;
                                var firstGap = context.Database.FirstGap();
                                var safeResumeBlock = context.Database.GetSafeResumeBlock();

                                // Use the minimum of all these values to ensure consistency
                                var effectiveResumeBlock = latestBlock;
                                if (firstGap.HasValue && firstGap.Value < effectiveResumeBlock)
                                {
                                    effectiveResumeBlock = firstGap.Value;
                                }
                                if (safeResumeBlock.HasValue && safeResumeBlock.Value < effectiveResumeBlock)
                                {
                                    effectiveResumeBlock = safeResumeBlock.Value;
                                }

                                context.Logger.Info(
                                    $"Initializing: LatestBlock={latestBlock}, FirstGap={firstGap?.ToString() ?? "none"}, " +
                                    $"SafeResumeBlock={safeResumeBlock?.ToString() ?? "none"} => Effective resume block: {effectiveResumeBlock}");

                                context.Logger.Info("Initializing: Warming up all caches...");
                                await InitializeCaches(effectiveResumeBlock);

                                // If starting from block 0, skip Reorg - there's nothing to clean up.
                                // This prevents accidental full database wipes when System_Block is empty.
                                if (effectiveResumeBlock == 0)
                                {
                                    context.Logger.Info(
                                        "Initializing: Starting fresh from block 0, skipping Reorg cleanup.");
                                    await TransitionTo(State.WaitForNewBlock);
                                    return;
                                }

                                if (effectiveResumeBlock < latestBlock)
                                {
                                    context.Logger.Warn(
                                        $"Detected inconsistent index state. Will clean up from block {effectiveResumeBlock} " +
                                        $"to ensure caches and database are synchronized.");
                                }

                                context.Logger.Info(
                                    $"Initializing: Transitioning to 'Reorg' to clean up from block {effectiveResumeBlock}...");
                                await TransitionTo(State.Reorg, effectiveResumeBlock);
                                return;
                            }
                    }

                    break;

                case State.Reorg:
                    switch (e)
                    {
                        case EnterState<long> enterState:
                            context.Logger.Info(
                                $"Reorg at {enterState.Arg}. Deleting all events from this block onwards...");

                            await context.Database.DeleteAllGreaterOrEqualBlock(enterState.Arg);


                            // In BulkMode, caches have no diff history — rollback is impossible.
                            // Re-seed from DB instead (DB was already cleaned above).
                            if (_inBulkSyncMode)
                            {
                                context.Logger.Warn(
                                    $"Reorg during bulk sync at {enterState.Arg}. " +
                                    $"Re-seeding all caches from DB (rollback not available in BulkMode)...");
                                await InitializeCaches(context.Database.LatestBlock() ?? 0);
                                context.Logger.Info("Reorg cleanup complete (bulk re-seed). Ready to process new blocks.");
                                await TransitionTo(State.WaitForNewBlock);
                            }
                            else
                            {
                                context.Logger.Info(
                                    $"Reorg at {enterState.Arg}. Removing objects from caches...");

                                try
                                {
                                    var allCaches = context.LogParsers.SelectMany(o => o.Caches).ToArray();
                                    foreach (var cache in allCaches)
                                    {
                                        var countBefore = cache.Count;
                                        var stats = cache.DeleteAllGreaterOrEqualBlock(enterState.Arg);
                                        var removed = stats.Removed;
                                        var restored = stats.Restored;
                                        var deleted = countBefore - cache.Count;
                                        context.Logger.Info(
                                            $"Cache '{cache.Name}' maintenance: removed {removed}, restored {restored}, count delta {deleted}.");
                                    }

                                    context.Logger.Info("Reorg cleanup complete. Ready to process new blocks.");
                                    await TransitionTo(State.WaitForNewBlock);
                                }
                                catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot roll back beyond stored history"))
                                {
                                    // Cache rollback capacity exceeded - we can't roll back this far.
                                    // Need to reinitialize caches from database.
                                    context.Logger.Warn(
                                        $"Cache rollback capacity exceeded at block {enterState.Arg}. " +
                                        $"Reinitializing caches from database...");
                                    await TransitionTo(State.Initial);
                                }
                            }
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case EnterState:
                            // On first entry to WaitForNewBlock after sync, send a notification
                            // to signal to subscribers (e.g., cache service) that sync is complete.
                            // This handles the case where all sync batches were >1000 blocks
                            // and no notifications were sent during catchup.
                            if (!_hasSentLiveNotification)
                            {
                                var latestIndexedBlock = context.Database.LatestBlock() ?? 0;
                                context.Logger.Info(
                                    $"Sending initial sync-complete notification at block {latestIndexedBlock}");
                                await NotifyViaPostgres(new Range<long> { Min = latestIndexedBlock, Max = latestIndexedBlock });
                                _hasSentLiveNotification = true;
                            }
                            return;

                        case NewHead newHead:
                            var latestBlock = context.Database.LatestBlock();
                            if (newHead.Head <= latestBlock)
                            {
                                await TransitionTo(State.Reorg, newHead.Head);
                                return;
                            }

                            // Enable bulk sync mode when the gap is large
                            var gap = newHead.Head - (latestBlock ?? 0);
                            if (!_inBulkSyncMode && gap > BulkSyncThreshold)
                            {
                                SetBulkMode(true);
                            }

                            await TransitionTo(State.Syncing, newHead.Head);
                            return;
                    }

                    break;

                case State.Syncing:
                    switch (e)
                    {
                        case EnterState<long> enterSyncing:
                            var importedBlockRange = await Sync(enterSyncing.Arg);

                            // Check if bulk sync mode should be exited
                            if (_inBulkSyncMode)
                            {
                                var latestIndexed = context.Database.LatestBlock() ?? 0;
                                var remainingGap = enterSyncing.Arg - latestIndexed;
                                if (remainingGap <= BulkSyncExitThreshold)
                                {
                                    await TransitionToLiveMode();
                                }
                            }

                            // Track blocks processed for status logging
                            if (importedBlockRange.Min.HasValue && importedBlockRange.Max.HasValue)
                            {
                                var blocksInRange = importedBlockRange.Max.Value - importedBlockRange.Min.Value + 1;
                                _blocksProcessedSinceLastLog += blocksInRange;
                                _totalBlocksProcessed += blocksInRange;
                            }

                            // Log status periodically:
                            // - Every 30 seconds during live sync (at chain head)
                            // - Every 1000 blocks during catch-up
                            var now = DateTime.UtcNow;
                            var timeSinceLastLog = now - _lastStatusLog;
                            if (timeSinceLastLog.TotalSeconds >= 30 || _blocksProcessedSinceLastLog >= 1000)
                            {
                                var latestDbBlock = context.Database.LatestBlock() ?? 0;
                                var blocksPerSecond = _blocksProcessedSinceLastLog / Math.Max(1, timeSinceLastLog.TotalSeconds);

                                // Determine if we're in live sync mode (processing ~1 block per 5 seconds)
                                var isLiveSync = blocksPerSecond < 1;
                                if (isLiveSync)
                                {
                                    context.Logger.Info(
                                        $"Live sync: indexed block {latestDbBlock:N0} " +
                                        $"({_blocksProcessedSinceLastLog} blocks in {timeSinceLastLog.TotalSeconds:F0}s)");
                                }
                                else
                                {
                                    context.Logger.Info(
                                        $"Catch-up: indexed to block {latestDbBlock:N0} " +
                                        $"({_blocksProcessedSinceLastLog:N0} blocks in {timeSinceLastLog.TotalSeconds:F1}s, " +
                                        $"{blocksPerSecond:F1} blk/s)");
                                }
                                _lastStatusLog = now;
                                _blocksProcessedSinceLastLog = 0;
                            }

                            context.Logger.Debug($"Imported blocks from {importedBlockRange.Min} " +
                                                 $"to {importedBlockRange.Max}");
                            Errors.Clear();
                            _consecutiveTransientErrors = 0;
                            _transientErrorStartedAt = null;

                            await TransitionTo(State.NotifySubscribers, importedBlockRange);
                            return;
                    }

                    break;

                case State.NotifySubscribers:
                    switch (e)
                    {
                        case EnterState<Range<long>> importedBlockRange:
                            var range = importedBlockRange.Arg;
                            var min = range.Min;
                            var max = range.Max;

                            if (!min.HasValue || !max.HasValue || max.Value < min.Value)
                            {
                                context.Logger.Debug("No completed block range to notify subscribers about.");
                                await TransitionTo(State.WaitForNewBlock);
                                return;
                            }

                            var span = max.Value - min.Value;
                            if (span > 1000)
                            {
                                context.Logger.Warn(
                                    $"Skipping LISTEN/NOTIFY broadcast because range {min}-{max} spans {span} blocks.");
                            }
                            else
                            {
                                await NotifyViaPostgres(range);
                                _hasSentLiveNotification = true;
                            }

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.Error:
                    switch (e)
                    {
                        case EnterState:
                            var lastError = Errors.LastOrDefault();
                            bool isTransientError = IsTransientSyncError(lastError);

                            if (isTransientError)
                            {
                                // --- Transient path: block/receipts not yet synced by Nethermind ---
                                // Retry indefinitely — Nethermind may need hours to sync historical blocks.
                                // Do NOT add to Errors list (keeps permanent error counter clean).
                                _consecutiveTransientErrors++;
                                _transientErrorStartedAt ??= DateTime.UtcNow;

                                // Remove from Errors list since we tracked it separately
                                if (Errors.Count > 0 && ReferenceEquals(Errors[^1], lastError))
                                    Errors.RemoveAt(Errors.Count - 1);

                                var blockNumber = GetTransientErrorBlockNumber(lastError);
                                var waitingDuration = DateTime.UtcNow - _transientErrorStartedAt.Value;

                                // Log escalation: Info for first 10, Warn every 10th after
                                if (_consecutiveTransientErrors <= 10)
                                {
                                    context.Logger.Info(
                                        $"Waiting for Nethermind to sync block {blockNumber:N0}... " +
                                        $"(attempt {_consecutiveTransientErrors}, waiting {waitingDuration.TotalMinutes:F0}min)");
                                }
                                else if (_consecutiveTransientErrors % 10 == 0)
                                {
                                    context.Logger.Warn(
                                        $"Still waiting for Nethermind to sync block {blockNumber:N0}. " +
                                        $"Attempt {_consecutiveTransientErrors}, waiting {waitingDuration.TotalHours:F1}h. " +
                                        $"This is normal during initial node sync.");
                                }

                                // Linear backoff: count * 5s, capped at 120s, plus jitter
                                var transientDelay = Math.Min(_consecutiveTransientErrors * 5000, 120_000);
                                var jitter = Random.Shared.Next(0, 2000);
                                transientDelay += jitter;

                                await Task.Delay(transientDelay, cancellationToken);

                                // Clean up caches via Reorg(LatestBlock+1) — same recovery as before
                                var latestFlushed = context.Database.LatestBlock() ?? 0;
                                var cleanupFrom = latestFlushed + 1;
                                await TransitionTo(State.Reorg, cleanupFrom);
                            }
                            else
                            {
                                // --- Non-transient path: DB errors, parser bugs, etc. ---
                                // Keep existing behavior: 10-error limit, quadratic backoff, terminal failure.
                                const int maxConsecutiveErrors = 10;
                                if (Errors.Count >= maxConsecutiveErrors)
                                {
                                    context.Logger.Error(
                                        $"CRITICAL: Reached maximum consecutive error limit ({maxConsecutiveErrors}). " +
                                        $"The indexer will stop retrying. Manual intervention required. " +
                                        $"Last error: {lastError?.Message ?? "unknown"}");

                                    for (int i = 0; i < Errors.Count; i++)
                                    {
                                        context.Logger.Error($"  Error {i + 1}: {Errors[i].Message}");
                                    }

                                    return;
                                }

                                var delay = Errors.Count * Errors.Count * 1000;
                                if (delay > 60000) delay = 60000;

                                var jitter = Random.Shared.Next(0, 1000);
                                delay += jitter;

                                context.Logger.Info($"Waiting {delay} ms before retrying after error {Errors.Count}/{maxConsecutiveErrors}...");
                                await Task.Delay(delay, cancellationToken);

                                context.Logger.Info("Transitioning to 'Initial' state after an error...");
                                await TransitionTo(State.Initial);
                            }
                            return;
                        case LeaveState:
                            return;
                    }

                    break;

                case State.End:
                    return;
            }

            context.Logger.Trace($"Unhandled event {e} in state {CurrentState}");
        }
        catch (Exception ex)
        {
            context.Logger.Error($"Error while handling {e} event in state {CurrentState}", ex);
            Errors.Add(ex);

            await TransitionTo(State.Error);
        }
    }

    private async Task TransitionTo<TArgument>(State newState, TArgument? argument)
    {
        context.Logger.Debug($"Transitioning from {CurrentState} to {newState}");
        if (newState is not State.Error)
        {
            await HandleEvent(new LeaveState());
        }

        CurrentState = newState;

        await HandleEvent(new EnterState<TArgument?>(argument));
    }

    public async Task TransitionTo(State newState)
    {
        await TransitionTo<object>(newState, null);
    }

    private async Task InitializeCaches(long lastPersistedBlock)
    {
        //
        // Init all LogParser caches
        //
        await Task.WhenAll(
            context.LogParsers.Select(o => o.InitCaches(
                context.Logger,
                context.Database,
                context.Settings)));
    }

    private void SetBulkMode(bool enabled)
    {
        var allCaches = context.LogParsers.SelectMany(o => o.Caches).ToArray();
        foreach (var cache in allCaches)
        {
            cache.BulkMode = enabled;
        }

        _inBulkSyncMode = enabled;
        context.Logger.Info(enabled
            ? $"Bulk sync mode ENABLED — cache locks and diff tracking disabled for {allCaches.Length} caches"
            : $"Bulk sync mode DISABLED — cache rollback capability restored for {allCaches.Length} caches");
    }

    private async Task TransitionToLiveMode()
    {
        context.Logger.Info("Transitioning from bulk sync to live mode — re-seeding caches from DB...");

        // Disable BulkMode first so Seed() uses proper locking
        SetBulkMode(false);

        try
        {
            // Re-seed all caches from DB to get clean state with rollback capability
            await InitializeCaches(context.Database.LatestBlock() ?? 0);
        }
        catch (Exception ex)
        {
            // Restore BulkMode to maintain consistent state — let error handler retry
            context.Logger.Error($"Failed to re-seed caches during live mode transition: {ex.Message}");
            SetBulkMode(true);
            throw;
        }
    }

    private async IAsyncEnumerable<long> GetBlocksToSync(long toBlock)
    {
        // After the Initial state and Reorg cleanup, the database should be consistent.
        // LatestBlock() now represents the true safe resume point since we cleaned up
        // everything above the minimum of (LatestBlock, FirstGap, SafeResumeBlock) during init.
        long lastIndexHeight = context.Database.LatestBlock() ?? 0;

        if (lastIndexHeight == toBlock)
        {
            yield break;
        }

        var nextBlock = lastIndexHeight + 1;
        if (nextBlock < context.Settings.StartBlock)
        {
            nextBlock = context.Settings.StartBlock;
        }

        for (long i = nextBlock; i <= toBlock; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }

    private async Task<Range<long>> Sync(long toBlock)
    {
        Range<long> importedBlockRange = new();
        try
        {
            ImportFlow flow = new(blockTree, receiptFinder, context);
            IAsyncEnumerable<long> blocksToSync = GetBlocksToSync(toBlock);
            importedBlockRange = await flow.Run(blocksToSync, cancellationToken);

            await context.Sink.Flush();
            await flow.FlushBlocks();
        }
        catch (TaskCanceledException)
        {
            context.Logger.Info("Cancelled indexing blocks.");
        }

        return importedBlockRange;
    }

    private async Task NotifyViaPostgres(Range<long> range)
    {
        if (range.Min is null || range.Max is null)
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(context.Settings.IndexDbConnectionString);
            await connection.OpenAsync(cancellationToken);

            var payload = JsonSerializer.Serialize(new
            {
                fromBlock = range.Min,
                toBlock = range.Max,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
            command.Parameters.AddWithValue("channel", context.Settings.PgNotifyChannel);
            command.Parameters.AddWithValue("payload", payload);

            await command.ExecuteNonQueryAsync(cancellationToken);
            context.Logger.Debug($"Sent {context.Settings.PgNotifyChannel} notification for blocks {range.Min}-{range.Max}.");
        }
        catch (Exception ex)
        {
            context.Logger.Error($"Failed to send {context.Settings.PgNotifyChannel} notification for blocks {range.Min}-{range.Max}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if an exception represents a transient sync error (block or receipts not yet available).
    /// These errors should be retried without full reinitialization.
    /// </summary>
    private static bool IsTransientSyncError(Exception? ex)
    {
        if (ex == null) return false;

        // Direct match
        if (ex is BlockNotAvailableException || ex is ReceiptsNotAvailableException)
            return true;

        // Check inner exception
        if (ex.InnerException is BlockNotAvailableException || ex.InnerException is ReceiptsNotAvailableException)
            return true;

        // Check AggregateException (from dataflow pipeline).
        // Only transient if ALL inner exceptions are transient — a mixed bag containing
        // e.g. NpgsqlException should NOT be routed to the unlimited-retry path.
        if (ex is AggregateException ae)
        {
            var inners = ae.Flatten().InnerExceptions;
            if (inners.Count > 0 && inners.All(inner =>
                    inner is BlockNotAvailableException || inner is ReceiptsNotAvailableException))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the block number from a transient sync error for logging.
    /// Unwraps AggregateException and InnerException as needed.
    /// </summary>
    private static long GetTransientErrorBlockNumber(Exception? ex)
    {
        if (ex is BlockNotAvailableException bna) return bna.BlockNumber;
        if (ex is ReceiptsNotAvailableException rna) return rna.BlockNumber;
        if (ex?.InnerException is BlockNotAvailableException innerBna) return innerBna.BlockNumber;
        if (ex?.InnerException is ReceiptsNotAvailableException innerRna) return innerRna.BlockNumber;

        if (ex is AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
            {
                if (inner is BlockNotAvailableException agBna) return agBna.BlockNumber;
                if (inner is ReceiptsNotAvailableException agRna) return agRna.BlockNumber;
            }
        }

        return -1;
    }
}

using Circles.Index.Common;
// using Circles.Rpc;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Npgsql;
using System.Text.Json;

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

    // Status logging
    private DateTime _lastStatusLog = DateTime.UtcNow;
    private long _blocksProcessedSinceLastLog = 0;
    private long _totalBlocksProcessed = 0;

    // Track if TABLE_START_BLOCKS cleanup was already performed (to avoid re-running on retries)
    private bool _tableStartBlocksCleanupDone = false;

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
                                // Track if we did targeted reindex cleanup in THIS run
                                var tableStartBlocksUsed = false;

                                // Handle TABLE_START_BLOCKS for reindexing (only once per process lifetime)
                                // Use "*:BlockNumber" to reindex ALL tables, or specific tables with their start blocks
                                if (!_tableStartBlocksCleanupDone)
                                {
                                    if (context.Settings.ReindexAllTables && context.Settings.ReindexAllFromBlock.HasValue)
                                    {
                                        var reindexFrom = context.Settings.ReindexAllFromBlock.Value;
                                        context.Logger.Info($"[TABLE_START_BLOCKS] Reindexing ALL tables from block {reindexFrom}");

                                        // Delete from all tables
                                        await context.Database.DeleteAllGreaterOrEqualBlock(reindexFrom);

                                        context.Logger.Info("[TABLE_START_BLOCKS] All data deletion complete. Indexer will resync from the specified block.");
                                        tableStartBlocksUsed = true;
                                        _tableStartBlocksCleanupDone = true;
                                    }
                                    else if (context.Settings.TableStartBlocks.Count > 0)
                                    {
                                        var minStartBlock = context.Settings.TableStartBlocks.Values.Min();
                                        var tablesToReindex = context.Settings.TableStartBlocks.Keys.ToArray();

                                        context.Logger.Info($"[TABLE_START_BLOCKS] Reindexing {tablesToReindex.Length} tables from block {minStartBlock}:");
                                        foreach (var (table, startBlock) in context.Settings.TableStartBlocks)
                                        {
                                            context.Logger.Info($"  - {table}: from block {startBlock}");
                                        }

                                        // Delete from specified tables
                                        await context.Database.DeleteFromTablesGreaterOrEqualBlock(minStartBlock, tablesToReindex);

                                        // Also delete from System_Block to force re-sync from the minimum start block
                                        context.Logger.Info($"[TABLE_START_BLOCKS] Deleting System_Block from block {minStartBlock} to force resync...");
                                        await context.Database.DeleteSystemBlockGreaterOrEqualBlock(minStartBlock);

                                        context.Logger.Info("[TABLE_START_BLOCKS] Data deletion complete. Indexer will resync from the minimum start block.");
                                        tableStartBlocksUsed = true;
                                        _tableStartBlocksCleanupDone = true;
                                    }
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

                                // If TABLE_START_BLOCKS was used, skip Reorg cleanup since we've already
                                // done targeted deletion. The Reorg state would otherwise delete from ALL tables.
                                if (tableStartBlocksUsed)
                                {
                                    context.Logger.Info(
                                        "[TABLE_START_BLOCKS] Cleanup already complete. Skipping Reorg state, proceeding to WaitForNewBlock.");
                                    await TransitionTo(State.WaitForNewBlock);
                                    return;
                                }

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


                            context.Logger.Info(
                                $"Reorg at {enterState.Arg}. Removing objects from caches...");


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
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case NewHead newHead:
                            context.Logger.Debug($"New head received: {newHead.Head}");
                            if (newHead.Head <= context.Database.LatestBlock())
                            {
                                await TransitionTo(State.Reorg, newHead.Head);
                                return;
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
                            }

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.Error:
                    switch (e)
                    {
                        case EnterState:
                            // Check if the last error was a transient "not available yet" error
                            var lastError = Errors.LastOrDefault();
                            bool isTransientError = IsTransientSyncError(lastError);

                            // Exponential backoff based on the number of errors
                            var delay = Errors.Count * Errors.Count * 1000;

                            // If the delay is larger than 60 sec, clear the oldest errors
                            if (delay > 60000)
                            {
                                Errors.RemoveAt(0);
                            }

                            // Add some jitter to the delay
                            var jitter = new Random((int)DateTime.Now.TimeOfDay.TotalSeconds).Next(0, 1000);
                            delay += jitter;

                            // Wait 'delay' ms
                            context.Logger.Info($"Waiting {delay} ms before retrying after an error...");
                            await Task.Delay(delay, cancellationToken);

                            if (isTransientError)
                            {
                                // For transient errors (block/receipts not yet available), just go back to
                                // WaitForNewBlock - no need to reinitialize caches or clean up the database.
                                context.Logger.Info(
                                    "Transient error (block/receipts not available). " +
                                    "Transitioning to 'WaitForNewBlock' to retry...");
                                await TransitionTo(State.WaitForNewBlock);
                            }
                            else
                            {
                                // For other errors, do full reinitialization
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

    private async IAsyncEnumerable<long> GetBlocksToSync(long toBlock)
    {
        // After the Initial state and Reorg cleanup, the database should be consistent.
        // LatestBlock() now represents the true safe resume point since we cleaned up
        // everything above the minimum of (LatestBlock, FirstGap, SafeResumeBlock) during init.
        long lastIndexHeight = context.Database.LatestBlock() ?? 0;

        if (lastIndexHeight == toBlock)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        var nextBlock = lastIndexHeight + 1;
        if (nextBlock < context.Settings.StartBlock)
        {
            context.Logger.Debug(
                $"Enumerating blocks to sync from {context.Settings.StartBlock} (StartBlock) to {toBlock}");
            nextBlock = context.Settings.StartBlock;
        }
        else
        {
            context.Logger.Debug($"Enumerating blocks to sync from {nextBlock} (LatestBlock + 1) to {toBlock}");
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

            // context.Database.TryUpdateCrcUiTransferHistory();
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

        // Check AggregateException (from dataflow pipeline)
        if (ex is AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
            {
                if (inner is BlockNotAvailableException || inner is ReceiptsNotAvailableException)
                    return true;
            }
        }

        return false;
    }
}
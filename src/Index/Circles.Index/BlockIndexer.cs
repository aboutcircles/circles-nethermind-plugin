using System.Threading.Tasks.Dataflow;
using Circles.Common;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index;

/// <summary>
/// Thrown when a block is not yet available in Nethermind's block tree.
/// This is a transient error that should be retried without full reinitialization.
/// </summary>
public class BlockNotAvailableException : Exception
{
    public long BlockNumber { get; }

    public BlockNotAvailableException(long blockNumber)
        : base($"Block {blockNumber:N0} not found in block tree. Nethermind may still be syncing.")
    {
        BlockNumber = blockNumber;
    }
}

/// <summary>
/// Thrown when receipts are not available for a block that has transactions.
/// This is a transient error that should be retried without full reinitialization.
/// </summary>
public class ReceiptsNotAvailableException : Exception
{
    public long BlockNumber { get; }

    public ReceiptsNotAvailableException(long blockNumber, int transactionCount, long startBlock)
        : base($"Block {blockNumber:N0} has {transactionCount} transaction(s) but no receipts. " +
               $"Nethermind may still be syncing receipts. START_BLOCK is {startBlock}.")
    {
        BlockNumber = blockNumber;
    }
}

public record BlockWithReceipts(Block Block, TxReceipt[] Receipts);


/// <summary>
/// Represents the import flow for indexing blockchain blocks in the Circles Index system.
/// This class orchestrates the retrieval of blocks, their associated transaction receipts,
/// parsing of logs and transactions to extract index events, and sinking those events
/// into the database. It utilizes a dataflow pipeline for efficient parallel processing
/// and includes mechanisms for buffering, flushing, and logging progress and statistics.
/// </summary>
public class ImportFlow(
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    Context context)
{
    private static readonly IndexPerformanceMetrics Metrics = new();

    private readonly InsertBuffer<BlockWithEventCounts> _blockBuffer = new();
    private long _blocksSinceLastInfoLog = 0;
    private DateTime _lastFlushTime = DateTime.UtcNow;
    private DateTime _lastFlushLogTime = DateTime.UtcNow;

    private long _totalBlocksWithMissingReceipts = 0;
    private long _totalBlocksWithReceipts = 0;

    private ExecutionDataflowBlockOptions CreateOptions(
        CancellationToken cancellationToken
        , int boundedCapacity = -1
        , int parallelism = -1) =>
        new()
        {
            MaxDegreeOfParallelism = parallelism > -1 ? parallelism : Environment.ProcessorCount,
            EnsureOrdered = true,
            CancellationToken = cancellationToken,
            BoundedCapacity = boundedCapacity
        };


    private async Task Sink((BlockWithReceipts, IEnumerable<IIndexEvent>) data)
    {
        Dictionary<string, int> eventCounts = new();
        var allEvents = data.Item2.ToList();

        foreach (var indexEvent in allEvents)
        {
            await context.Sink.AddEvent(indexEvent);
            var tableName = context.Database.Schema.EventDtoTableMap.Map[indexEvent.GetType()];
            var tableNameString = $"{tableName.Namespace}_{tableName.Table}";
            eventCounts[tableNameString] = eventCounts.GetValueOrDefault(tableNameString) + 1;
        }

        var block = data.Item1.Block;
        await AddBlock(new BlockWithEventCounts(
            new SimpleBlock(block.Number, block.Timestamp, block.Hash?.ToString()),
            eventCounts));
        Metrics.LogBlockWithReceipts(data.Item1);
    }

    private (TransformBlock<long, Block> Source, ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> Sink)
        BuildPipeline(CancellationToken cancellationToken)
    {
        TransformBlock<long, Block> sourceBlock = new(
            blockNo =>
            {
                var block = blockTree.FindBlock(blockNo);
                if (block == null)
                {
                    context.Logger.Warn($"Block {blockNo:N0} NOT FOUND in block tree!");
                    throw new BlockNotAvailableException(blockNo);
                }
                return block;
            },
            CreateOptions(cancellationToken, 3, 3));

        TransformBlock<Block, BlockWithReceipts> receiptsSourceBlock =
            new(block =>
                {
                    var receipts = receiptFinder.Get(block);

                    // Track receipt availability statistics
                    bool hasTransactions = block.Transactions.Length > 0;
                    bool hasReceipts = receipts != null && receipts.Length > 0;

                    if (hasTransactions)
                    {
                        if (hasReceipts)
                        {
                            long totalWithReceipts = Interlocked.Increment(ref _totalBlocksWithReceipts);

                            // Log progress every 10_000 blocks checked
                            if (totalWithReceipts % 10_000 == 0)
                            {
                                long totalMissing = Interlocked.Read(ref _totalBlocksWithMissingReceipts);
                                long totalChecked = totalWithReceipts + totalMissing;
                                double percentAvailable = totalWithReceipts * 100.0 / totalChecked;
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref _totalBlocksWithMissingReceipts);

                            // Always warn about missing receipts
                            long totalWithReceipts = Interlocked.Read(ref _totalBlocksWithReceipts);
                            long totalMissing = Interlocked.Read(ref _totalBlocksWithMissingReceipts);
                            long totalChecked = totalWithReceipts + totalMissing;
                            double percentAvailable = totalChecked > 0 ? totalWithReceipts * 100.0 / totalChecked : 0;

                            context.Logger.Warn(
                                $"Missing receipts at block {block.Number:N0}! " +
                                $"Availability so far: {totalWithReceipts:N0}/{totalChecked:N0} ({percentAvailable:F1}%)");
                        }
                    }

                    // Validate that receipts are available for blocks with transactions
                    // If a block has transactions but no receipts, Nethermind likely hasn't synced them
                    // (e.g., blocks before AncientReceiptsBarrier in fast/snap sync mode)
                    if (hasTransactions && !hasReceipts)
                    {
                        throw new ReceiptsNotAvailableException(
                            block.Number, block.Transactions.Length, context.Settings.StartBlock);
                    }

                    return new BlockWithReceipts(block, receipts ?? []);
                }
                , CreateOptions(cancellationToken, Environment.ProcessorCount, Environment.ProcessorCount));

        sourceBlock.LinkTo(receiptsSourceBlock, new DataflowLinkOptions { PropagateCompletion = true });

        TransformBlock<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)> parserBlock = new(
            blockWithReceipts =>
            {
                var blockNum = blockWithReceipts.Block.Number;
                Dictionary<Hash256, Transaction> transactionsByHash = new();
                Dictionary<Hash256, int> transactionIndexByHash = new();

                // Index the transactions in this block
                for (int i = 0; i < blockWithReceipts.Block.Transactions.Length; i++)
                {
                    var tx = blockWithReceipts.Block.Transactions[i];
                    if (tx.Hash == null) continue;

                    transactionsByHash[tx.Hash] = tx;
                    transactionIndexByHash[tx.Hash] = i;
                }

                // We'll collect final events for the entire block in 'allEvents'
                List<IIndexEvent> allEvents = new();

                // Go through every receipt (which belongs to a transaction).
                foreach (var receipt in blockWithReceipts.Receipts)
                {
                    var txHash = receipt.TxHash;
                    if (txHash == null || !transactionsByHash.TryGetValue(txHash, out var transaction))
                        continue;

                    var transactionIndex = transactionIndexByHash[txHash];

                    // A dictionary mapping each parser -> the events it produced from logs
                    var parserToEvents = new Dictionary<ILogParser, List<IIndexEvent>>(context.LogParsers.Length);
                    foreach (var parser in context.LogParsers)
                    {
                        parserToEvents[parser] = new List<IIndexEvent>();
                    }

                    // 1) Parse logs with each parser; each parser only stores in its own list
                    if (receipt.Logs != null)
                    {
                        for (int logIndex = 0; logIndex < receipt.Logs.Length; logIndex++)
                        {
                            var logEntry = receipt.Logs[logIndex];
                            foreach (var parser in context.LogParsers)
                            {
                                try
                                {
                                    var parsedLogEvents = parser.ParseLog(
                                        blockWithReceipts.Block,
                                        transaction,
                                        receipt,
                                        logEntry,
                                        logIndex);

                                    parserToEvents[parser].AddRange(parsedLogEvents);
                                }
                                catch (Exception e)
                                {
                                    context.Logger.Error($"Error parsing log at block {blockNum:N0}, tx {receipt.TxHash}, log {logIndex}: {e.Message}");
                                    throw;
                                }
                            }
                        }
                    }

                    // 2) For each parser, call ParseTransaction with the events from that parser only
                    foreach (var parser in context.LogParsers)
                    {
                        try
                        {
                            // Watchdog: log warning if ParseTransaction takes too long
                            var parserName = parser.GetType().Name;
                            var eventCount = parserToEvents[parser].Count;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            using var watchdog = new Timer(_ =>
                            {
                                context.Logger.Warn(
                                    $"SLOW: {parserName}.ParseTransaction running for {sw.Elapsed.TotalSeconds:F1}s " +
                                    $"on block {blockNum:N0}, tx {receipt.TxHash}, {eventCount} events");
                            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

                            var txEvents = parser.ParseTransaction(
                                blockWithReceipts.Block,
                                transactionIndex,
                                transaction,
                                receipt,
                                parserToEvents[parser]);

                            sw.Stop();
                            if (sw.Elapsed.TotalSeconds > 1)
                            {
                                context.Logger.Warn(
                                    $"{parserName}.ParseTransaction took {sw.Elapsed.TotalSeconds:F2}s " +
                                    $"on block {blockNum:N0}, tx {receipt.TxHash}, {eventCount} events");
                            }

                            // Add these newly derived events to that parser's list
                            parserToEvents[parser].AddRange(txEvents);
                        }
                        catch (Exception e)
                        {
                            context.Logger.Error($"Error in {parser.GetType().Name}.ParseTransaction at block {blockNum:N0}, tx {receipt.TxHash}: {e.Message}");
                            throw;
                        }
                    }

                    // 3) Combine everything from parserToEvents into our final block-level list
                    foreach (var kvp in parserToEvents)
                    {
                        allEvents.AddRange(kvp.Value);
                    }
                }

                return (blockWithReceipts, allEvents);
            },
            CreateOptions(cancellationToken, 1, 1));


        receiptsSourceBlock.LinkTo(parserBlock, new DataflowLinkOptions { PropagateCompletion = true });

        ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> sinkBlock = new(Sink,
            CreateOptions(cancellationToken, 64 * 1024, 1));
        parserBlock.LinkTo(sinkBlock, new DataflowLinkOptions { PropagateCompletion = true });

        return (sourceBlock, sinkBlock);
    }

    public async Task<Range<long>> Run(IAsyncEnumerable<long> blocksToIndex, CancellationToken? cancellationToken)
    {
        var (sourceBlock, sinkBlock) = BuildPipeline(cancellationToken ?? CancellationToken.None);

        long min = long.MaxValue;
        long max = long.MinValue;
        long count = 0;
        long lastSuccessfulBlock = -1;
        DateTime lastLogTime = DateTime.UtcNow;

        try
        {
            await foreach (var blockNo in blocksToIndex.WithCancellation(cancellationToken ?? CancellationToken.None))
            {
                // Check if the pipeline has faulted before sending more blocks
                if (sourceBlock.Completion.IsFaulted)
                {
                    context.Logger.Warn($"Pipeline faulted at block {blockNo:N0}, stopping enumeration.");
                    break;
                }

                await sourceBlock.SendAsync(blockNo, cancellationToken ?? CancellationToken.None);

                min = Math.Min(min, blockNo);
                max = Math.Max(max, blockNo);
                count++;
                lastSuccessfulBlock = blockNo;

                // Log progress every 1000 blocks or every 5 minutes
                var now = DateTime.UtcNow;
                var timeSinceLastLog = now - lastLogTime;

                bool isTimeLog = timeSinceLastLog.TotalMinutes >= 5;
                bool isCountLog = count % 1000 == 0;

                // Suppress count log if it's too frequent (e.g. during fast sync)
                if (isCountLog && timeSinceLastLog.TotalSeconds < 30)
                {
                    isCountLog = false;
                }

                if (isTimeLog || isCountLog)
                {
                    context.Logger.Info($"Indexing progress: block {blockNo:N0} ({count:N0} blocks processed)");
                    lastLogTime = now;
                }
            }
        }
        finally
        {
            sourceBlock.Complete();
        }

        // Wait for the entire pipeline to complete and check for faults
        try
        {
            await sinkBlock.Completion;
        }
        catch (AggregateException ae)
        {
            // Log and rethrow - the actual exception from the pipeline
            foreach (var ex in ae.Flatten().InnerExceptions)
            {
                context.Logger.Error($"Pipeline error: {ex.Message}");
            }
            throw;
        }

        // Also check if source block faulted (might not propagate to sink)
        if (sourceBlock.Completion.IsFaulted)
        {
            var sourceEx = sourceBlock.Completion.Exception;
            if (sourceEx != null)
            {
                context.Logger.Error($"Source block faulted: {sourceEx.Flatten().Message}");
                throw sourceEx.Flatten();
            }
        }

        if (count % 10_000 == 0)
        {
            context.Logger.Info($"Indexing completed: blocks {min:N0} to {max:N0} ({count:N0} total)");
        }

        return new Range<long>
        {
            Min = min == long.MaxValue ? null : min,
            Max = max == long.MinValue ? null : max
        };
    }

    private async Task AddBlock(BlockWithEventCounts block)
    {
        _blockBuffer.Add(block);

        bool timeToFlush = (DateTime.UtcNow - _lastFlushTime).TotalMinutes >= 5;
        bool bufferFull = _blockBuffer.Length >= context.Settings.BlockBufferSize;

        if (bufferFull || timeToFlush)
        {
            // FLush events
            await context.Sink.Flush();

            // Flush blocks
            await FlushBlocks();
            _lastFlushTime = DateTime.UtcNow;
        }
    }

    public async Task FlushBlocks()
    {
        try
        {
            var blocks = _blockBuffer.TakeSnapshot();
            if (!blocks.IsEmpty)
            {
                var minBlock = blocks.Min(b => b.Block.Number);
                var maxBlock = blocks.Max(b => b.Block.Number);
                _blocksSinceLastInfoLog += blocks.Count;

                var now = DateTime.UtcNow;
                var timeSinceLastLog = now - _lastFlushLogTime;

                bool isTimeLog = timeSinceLastLog.TotalMinutes >= 5;
                bool isSizeLog = _blocksSinceLastInfoLog >= context.Settings.BlockBufferSize;

                // Suppress size log if it's too frequent (e.g. during fast sync)
                if (isSizeLog && timeSinceLastLog.TotalSeconds < 30)
                {
                    isSizeLog = false;
                }

                if (isTimeLog || isSizeLog)
                {
                    context.Logger.Info(
                        $"Flushed {_blocksSinceLastInfoLog:N0} blocks since last report. " +
                        $"Latest batch: {blocks.Count:N0} blocks (range: {minBlock:N0} - {maxBlock:N0})");
                    _blocksSinceLastInfoLog = 0;
                    _lastFlushLogTime = now;
                }
            }

            // Use the same write mode logic as Sink for consistency
            // In Auto mode, try COPY first and fall back to upsert on duplicate key errors
            var writeMode = context.Settings.WriteMode;
            if (writeMode == WriteMode.Upsert)
            {
                await context.Sink.Database.WriteBatchWithUpsert("System", "Block", blocks,
                    context.Database.Schema.SchemaPropertyMap);
            }
            else if (writeMode == WriteMode.Auto)
            {
                try
                {
                    await context.Sink.Database.WriteBatch("System", "Block", blocks,
                        context.Database.Schema.SchemaPropertyMap);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    // Duplicate key error - retry with upsert mode
                    context.Logger.Info("System_Block duplicate key detected, retrying with upsert mode...");
                    await context.Sink.Database.WriteBatchWithUpsert("System", "Block", blocks,
                        context.Database.Schema.SchemaPropertyMap);
                }
            }
            else // WriteMode.Copy
            {
                await context.Sink.Database.WriteBatch("System", "Block", blocks,
                    context.Database.Schema.SchemaPropertyMap);
            }
        }
        catch (Exception e)
        {
            context.Logger.Error("Error flushing blocks", e);
            throw;
        }
    }
}

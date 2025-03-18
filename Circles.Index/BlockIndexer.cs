using System.Threading.Tasks.Dataflow;
using Circles.Index.Common;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Circles.Index;

public record BlockWithReceipts(Block Block, TxReceipt[] Receipts);

public class ImportFlow(
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    Context context)
{
    private static readonly IndexPerformanceMetrics Metrics = new();

    private readonly InsertBuffer<BlockWithEventCounts> _blockBuffer = new();

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

        foreach (var indexEvent in data.Item2)
        {
            await context.Sink.AddEvent(indexEvent);
            var tableName = context.Database.Schema.EventDtoTableMap.Map[indexEvent.GetType()];
            var tableNameString = $"{tableName.Namespace}_{tableName.Table}";
            eventCounts[tableNameString] = eventCounts.GetValueOrDefault(tableNameString) + 1;
        }

        await AddBlock(new BlockWithEventCounts(data.Item1.Block, eventCounts));
        Metrics.LogBlockWithReceipts(data.Item1);
    }

    private (TransformBlock<long, Block?> Source, ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> Sink)
        BuildPipeline(CancellationToken cancellationToken)
    {
        TransformBlock<long, Block?> sourceBlock = new(
            blockTree.FindBlock,
            CreateOptions(cancellationToken, 3, 3));

        TransformBlock<Block, BlockWithReceipts> receiptsSourceBlock =
            new(block => new BlockWithReceipts(block, receiptFinder.Get(block))
                , CreateOptions(cancellationToken, Environment.ProcessorCount, Environment.ProcessorCount));

        sourceBlock.LinkTo(receiptsSourceBlock!, new DataflowLinkOptions { PropagateCompletion = true },
            o => o != null);

        TransformBlock<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)> parserBlock = new(
            blockWithReceipts =>
            {
                Dictionary<Hash256, Transaction> transactionsByHash = new();

                for (int i = 0; i < blockWithReceipts.Block.Transactions.Length; i++)
                {
                    var tx = blockWithReceipts.Block.Transactions[i];
                    if (tx.Hash == null)
                    {
                        continue;
                    }

                    transactionsByHash[tx.Hash] = tx;
                }

                // We'll collect final events for the entire block in 'allEvents'
                List<IIndexEvent> allEvents = new();

                // Go through every receipt (which belongs to a transaction).
                foreach (var receipt in blockWithReceipts.Receipts)
                {
                    var txHash = receipt.TxHash;
                    if (txHash == null || !transactionsByHash.TryGetValue(txHash, out var transaction))
                        continue;

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
                                    context.Logger.Error(
                                        $"Error parsing log entry ({blockWithReceipts.Block.Number}, {receipt.Index}, {logIndex}) with parser {parser}");
                                    context.Logger.Error($"Block: {blockWithReceipts.Block.Number}");
                                    context.Logger.Error($"Receipt.TxHash: {receipt.TxHash}");
                                    context.Logger.Error("Exception:", e);
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
                            var txEvents = parser.ParseTransaction(
                                blockWithReceipts.Block,
                                transaction,
                                receipt,
                                parserToEvents[parser].ToArray());

                            // Add these newly derived events to that parser's list
                            txEvents.ForEach(e =>
                            {
                                e.Switch(
                                    left => parserToEvents[parser].Add(left),
                                    right => { Console.WriteLine($"Parsed call data: {right.FunctionName}"); });
                            });
                        }
                        catch (Exception e)
                        {
                            context.Logger.Error($"Error parsing transaction {transaction} with parser {parser}");
                            context.Logger.Error($"Block: {blockWithReceipts.Block.Number}");
                            context.Logger.Error($"Receipt.TxHash: {receipt.TxHash}");
                            context.Logger.Error("Exception: ", e);
                            throw;
                        }
                    }

                    // 3) Combine everything from parserToEvents into our final block-level list
                    foreach (var kvp in parserToEvents)
                    {
                        allEvents.AddRange(kvp.Value);
                    }
                }

                // Finally, return (block, allEvents)
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

        await foreach (var blockNo in blocksToIndex.WithCancellation(cancellationToken ?? CancellationToken.None))
        {
            await sourceBlock.SendAsync(blockNo, cancellationToken ?? CancellationToken.None);

            min = Math.Min(min, blockNo);
            max = Math.Max(max, blockNo);
        }

        sourceBlock.Complete();

        await sinkBlock.Completion;

        return new Range<long>
        {
            Min = min,
            Max = max
        };
    }

    private async Task AddBlock(BlockWithEventCounts block)
    {
        _blockBuffer.Add(block);

        if (_blockBuffer.Length >= context.Settings.BlockBufferSize)
        {
            // FLush events
            await context.Sink.Flush();

            // Flush blocks
            await FlushBlocks();
        }
    }

    public async Task FlushBlocks()
    {
        try
        {
            var blocks = _blockBuffer.TakeSnapshot();
            await context.Sink.Database.WriteBatch("System", "Block", blocks,
                context.Database.Schema.SchemaPropertyMap);
        }
        catch (Exception e)
        {
            context.Logger.Error("Error flushing blocks", e);
            throw;
        }
    }
}
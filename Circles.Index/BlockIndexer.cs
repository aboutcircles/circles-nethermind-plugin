using System.Threading.Tasks.Dataflow;
using Circles.Index.Common;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;

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

    // Config on 16 core AMD:
    // blockSource: 3 buffer, 3 parallel
    // findReceipts: 6 buffer, 6 parallel

    public async Task<Range<long>> RunPipeline(
        IAsyncEnumerable<long> blocksToIndex,
        CancellationToken cancellationToken)
    {
        var sourceBlock = new BufferBlock<long>(CreateOptions(cancellationToken, 1, 1));
        var downloadBlock = CreateDownloadBlock(cancellationToken, 3, 3);
        var receiptsSourceBlock =
            CreateReceiptsSourceBlock(cancellationToken, Environment.ProcessorCount, Environment.ProcessorCount);
        var parserBlock = CreateParserBlock(cancellationToken);
        var sinkBlock = CreateSinkBlock(cancellationToken);

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

        sourceBlock.LinkTo(downloadBlock, linkOptions);
        downloadBlock.LinkTo(receiptsSourceBlock!, linkOptions, o => o != null);
        receiptsSourceBlock.LinkTo(parserBlock, linkOptions);
        parserBlock.LinkTo(sinkBlock, linkOptions);

        long min = long.MaxValue;
        long max = long.MinValue;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var blockNo in blocksToIndex.WithCancellation(cancellationToken))
                {
                    await sourceBlock.SendAsync(blockNo, cancellationToken);

                    min = Math.Min(min, blockNo);
                    max = Math.Max(max, blockNo);
                }
            }
            finally
            {
                sourceBlock.Complete();
            }
        }, cancellationToken);

        await sinkBlock.Completion;

        return new Range<long>
        {
            Min = min,
            Max = max
        };
    }

    private ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> CreateSinkBlock(
        CancellationToken cancellationToken) => new(Sink, CreateOptions(cancellationToken, 64 * 1024, 1));

    private TransformBlock<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)> CreateParserBlock(
        CancellationToken cancellationToken) => new(
        blockWithReceipts =>
        {
            List<IIndexEvent> events = [];
            foreach (var receipt in blockWithReceipts.Receipts)
            {
                for (int i = 0; i < receipt.Logs?.Length; i++)
                {
                    LogEntry log = receipt.Logs[i];
                    foreach (var parser in context.LogParsers)
                    {
                        var parsedEvents = parser.ParseLog(blockWithReceipts.Block, receipt, log, i);
                        events.AddRange(parsedEvents);
                    }
                }
            }

            return (blockWithReceipts, events);
        },
        CreateOptions(cancellationToken, Environment.ProcessorCount));

    private TransformBlock<Block, BlockWithReceipts> CreateReceiptsSourceBlock(CancellationToken cancellationToken,
        int boundedCapacity = 1, int parallelism = 1) =>
        new(
            block =>
                new BlockWithReceipts(
                    block
                    , receiptFinder.Get(block))
            , CreateOptions(cancellationToken, boundedCapacity, parallelism));

    private TransformBlock<long, Block?> CreateDownloadBlock(CancellationToken cancellationToken,
        int boundedCapacity = 1, int parallelism = 1) => new(
        blockTree.FindBlock,
        CreateOptions(cancellationToken, boundedCapacity, parallelism));

    private async Task AddBlock(BlockWithEventCounts block)
    {
        _blockBuffer.Add(block);

        if (_blockBuffer.Length >= context.Settings.BlockBufferSize)
        {
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
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

    private readonly InsertBuffer<Block> _blockBuffer = new();

    private ExecutionDataflowBlockOptions CreateOptions(
        CancellationToken cancellationToken
        , int boundedCapacity = -1
        , int parallelism = -1) =>
        new()
        {
            MaxDegreeOfParallelism = parallelism > -1 ? parallelism : Environment.ProcessorCount,
            EnsureOrdered = false,
            CancellationToken = cancellationToken,
            BoundedCapacity = boundedCapacity
        };


    private async Task Sink((BlockWithReceipts, IEnumerable<IIndexEvent>) data)
    {
        foreach (var indexEvent in data.Item2)
        {
            await context.Sink.AddEvent(indexEvent);
        }

        await AddBlock(data.Item1.Block);
        Metrics.LogBlockWithReceipts(data.Item1);
    }

    // Config on 16 core AMD:
    // blockSource: 3 buffer, 3 parallel
    // findReceipts: 6 buffer, 6 parallel

    private (TransformBlock<long, Block?> Source, ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> Sink)
        BuildPipeline(CancellationToken cancellationToken)
    {
        TransformBlock<long, Block?> sourceBlock = new(
            blockTree.FindBlock,
            CreateOptions(cancellationToken, 3, 3));

        TransformBlock<Block, BlockWithReceipts> receiptsSourceBlock = new(
            block =>
                new BlockWithReceipts(
                    block
                    , receiptFinder.Get(block))
            , CreateOptions(cancellationToken, Environment.ProcessorCount, Environment.ProcessorCount));

        sourceBlock.LinkTo(receiptsSourceBlock!, new DataflowLinkOptions { PropagateCompletion = true },
            o => o != null);

        TransformBlock<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)> parserBlock = new(
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

        receiptsSourceBlock.LinkTo(parserBlock, new DataflowLinkOptions { PropagateCompletion = true });

        ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> sinkBlock = new(Sink,
            CreateOptions(cancellationToken, 50000, 1));
        parserBlock.LinkTo(sinkBlock, new DataflowLinkOptions { PropagateCompletion = true });

        return (sourceBlock, sinkBlock);
    }

    public async Task<Range<long>> Run(IAsyncEnumerable<long> blocksToIndex, CancellationToken? cancellationToken)
    {
        var (sourceBlock, sinkBlock) = BuildPipeline(CancellationToken.None);

        long min = long.MaxValue;
        long max = long.MinValue;

        if (cancellationToken == null)
        {
            CancellationTokenSource cts = new();
            cancellationToken = cts.Token;
        }

        await foreach (var blockNo in blocksToIndex.WithCancellation(cancellationToken.Value))
        {
            await sourceBlock.SendAsync(blockNo, cancellationToken.Value);

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

    private async Task AddBlock(Block block)
    {
        _blockBuffer.Add(block);

        if (_blockBuffer.Length >= context.Settings.BlockBufferSize)
        {
            await FlushBlocks();
        }
    }

    public async Task FlushBlocks()
    {
        var blocks = _blockBuffer.TakeSnapshot();

        var map = new SchemaPropertyMap();
        map.Add(("System", "Block"), new Dictionary<string, Func<Block, object?>>
        {
            { "blockNumber", o => o.Number },
            { "timestamp", o => (long)o.Timestamp },
            { "blockHash", o => o.Hash!.ToString() }
        });

        await context.Sink.Database.WriteBatch("System", "Block", blocks, map);
    }
}
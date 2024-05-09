using System.Threading.Tasks.Dataflow;
using Circles.Index.Common;
using Circles.Index.Data;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;

namespace Circles.Index.Indexer;

public record BlockWithReceipts(Block Block, TxReceipt[] Receipts);

public class ImportFlow
{
    private static readonly IndexPerformanceMetrics Metrics = new();

    private readonly MeteredCaller<Block, Task> _addBlock;
    private readonly MeteredCaller<object?, Task> _flushBlocks;

    private readonly InsertBuffer<Block> _blockBuffer = new();
    private readonly IBlockTree _blockTree;
    private readonly IReceiptFinder _receiptFinder;
    private readonly ILogParser[] _parsers;
    private readonly Sink _sink;

    public ImportFlow(
        IBlockTree blockTree,
        IReceiptFinder receiptFinder,
        ILogParser[] parsers,
        Sink sink)
    {
        _blockTree = blockTree;
        _receiptFinder = receiptFinder;
        _parsers = parsers;
        _sink = sink;
        _addBlock = new MeteredCaller<Block, Task>("BlockIndexer: AddBlock", PerformAddBlock);
        _flushBlocks = new MeteredCaller<object?, Task>("BlockIndexer: FlushBlocks", _ => PerformFlushBlocks());
    }


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
            await _sink.AddEvent(indexEvent);
        }

        await AddBlock(data.Item1.Block);
        Metrics.LogBlockWithReceipts(data.Item1);
    }

    // Config on 16 core AMD:
    // blockSource: 3 buffer, 3 parallel
    // findReceipts: 6 buffer, 6 parallel

    private TransformBlock<long, Block?> BuildPipeline(CancellationToken cancellationToken)
    {
        MeteredCaller<long, Block?> findBlock = new("BlockIndexer: FindBlock", _blockTree.FindBlock);
        TransformBlock<long, Block?> blockSource = new(
            blockNo => findBlock.Call(blockNo),
            CreateOptions(cancellationToken, 3, 3));

        MeteredCaller<Block, BlockWithReceipts> findReceipts = new("BlockIndexer: FindReceipts", block =>
            new BlockWithReceipts(
                block
                , _receiptFinder.Get(block)));
        TransformBlock<Block?, BlockWithReceipts> receiptsSource = new(
            block => findReceipts.Call(block!)
            , CreateOptions(cancellationToken, Environment.ProcessorCount, Environment.ProcessorCount));

        blockSource.LinkTo(receiptsSource, b => b != null);

        MeteredCaller<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)>
            parseLogs = new("BlockIndexer: ParseLogs", blockWithReceipts =>
            {
                List<IIndexEvent> events = new();
                foreach (var receipt in blockWithReceipts.Receipts)
                {
                    for (int i = 0; i < receipt.Logs?.Length; i++)
                    {
                        LogEntry log = receipt.Logs[i];
                        foreach (var parser in _parsers)
                        {
                            var parsedEvents = parser.ParseLog(blockWithReceipts.Block, receipt, log, i);
                            events.AddRange(parsedEvents);
                        }
                    }
                }

                return (blockWithReceipts, events);
            });

        TransformBlock<BlockWithReceipts, (BlockWithReceipts, IEnumerable<IIndexEvent>)> parser = new(
            blockWithReceipts => parseLogs.Call(blockWithReceipts),
            CreateOptions(cancellationToken, Environment.ProcessorCount));

        receiptsSource.LinkTo(parser);

        ActionBlock<(BlockWithReceipts, IEnumerable<IIndexEvent>)> sink = new(Sink,
            CreateOptions(cancellationToken, 50000, 1));
        parser.LinkTo(sink);

        return blockSource;
    }

    public async Task<Range<long>> Run(IAsyncEnumerable<long> blocksToIndex, CancellationToken? cancellationToken)
    {
        TransformBlock<long, Block?> pipeline = BuildPipeline(CancellationToken.None);

        long min = long.MaxValue;
        long max = long.MinValue;

        if (cancellationToken == null)
        {
            CancellationTokenSource cts = new();
            cancellationToken = cts.Token;
        }

        var source = blocksToIndex.WithCancellation(cancellationToken.Value);

        MeteredCaller<long, Task> sendBlock = new("BlockIndexer: pipeline.SendAsync",
            blockNo => pipeline.SendAsync(blockNo, cancellationToken.Value));

        await foreach (var blockNo in source)
        {
            await sendBlock.Call(blockNo);

            min = Math.Min(min, blockNo);
            max = Math.Max(max, blockNo);
        }

        pipeline.Complete();
        await pipeline.Completion;

        await FlushBlocks();

        return new Range<long>
        {
            Min = min,
            Max = max
        };
    }

    private Task AddBlock(Block block)
    {
        return _addBlock.Call(block);
    }

    private async Task PerformAddBlock(Block block)
    {
        _blockBuffer.Add(block);
        if (_blockBuffer.Length >= 20000)
        {
            await FlushBlocks();
        }
    }

    public Task FlushBlocks()
    {
        return _flushBlocks.Call(null);
    }

    private async Task PerformFlushBlocks()
    {
        var blocks = _blockBuffer.TakeSnapshot();

        var map = new SchemaPropertyMap();
        map.Add(("System", "Block"), new Dictionary<string, Func<Block, object?>>
        {
            { "blockNumber", o => o.Number },
            { "timestamp", o => (long)o.Timestamp },
            { "blockHash", o => o.Hash!.ToString() }
        });

        await _sink.Database.WriteBatch("System", "Block", blocks, map);
    }
}
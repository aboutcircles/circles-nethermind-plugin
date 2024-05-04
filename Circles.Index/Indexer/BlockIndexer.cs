using System.Threading.Tasks.Dataflow;
using Circles.Index.Common;
using Circles.Index.Data;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Indexer;

public record BlockWithReceipts(Block Block, TxReceipt[] Receipts);

public class ImportFlow
{
    private static readonly IndexPerformanceMetrics Metrics = new();

    private readonly MeteredCaller<Block, Task> _addBlock;
    private readonly MeteredCaller<object?, Task> _flushBlocks;

    private readonly InsertBuffer<Block> _blockBuffer = new();
    private readonly Settings _settings;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptFinder _receiptFinder;
    private readonly INewIndexerVisitor[] _parsers;
    private readonly IEventSink[] _sinks;

    public ImportFlow(Settings settings,
        IBlockTree blockTree,
        IReceiptFinder receiptFinder,
        INewIndexerVisitor[] parsers,
        IEventSink[] sinks)
    {
        _settings = settings;
        _blockTree = blockTree;
        _receiptFinder = receiptFinder;
        _parsers = parsers;
        _sinks = sinks;
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
            foreach (var sink in _sinks)
            {
                await sink.AddEvent(indexEvent);
            }
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
            , CreateOptions(cancellationToken, 6, 6));

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
            CreateOptions(cancellationToken, 200000, 1));
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
            //await pipeline.SendAsync(blockNo, cancellationToken.Value);
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
        if (_blockBuffer.Length >= 10000)
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
        await using var connection = new NpgsqlConnection(_settings.IndexDbConnectionString);
        await connection.OpenAsync();

        await using var writer = await connection.BeginBinaryImportAsync(
            $@"
                COPY {Tables.Block.GetIdentifier()} (
                    block_number, timestamp, block_hash
                ) FROM STDIN (FORMAT BINARY)"
        );

        foreach (var block in blocks)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(block.Number, NpgsqlDbType.Bigint);
            await writer.WriteAsync((long)block.Timestamp, NpgsqlDbType.Bigint);
            await writer.WriteAsync(block.Hash!.ToString(), NpgsqlDbType.Text);
        }

        await writer.CompleteAsync();
    }
}
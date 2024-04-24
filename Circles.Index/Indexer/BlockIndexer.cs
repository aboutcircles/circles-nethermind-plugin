using System.Threading.Tasks.Dataflow;
using Circles.Index.Common;
using Circles.Index.Data;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.Indexer;

public record BlockWithReceipts(Nethermind.Core.Block Block, TxReceipt[] Receipts);

public class ImportFlow(
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    IIndexerVisitor visitor,
    ISink dataSink)
{
    private static readonly IndexPerformanceMetrics _metrics = new();

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


    private BlockWithReceipts Sink(BlockWithReceipts data)
    {
        try
        {
            visitor.VisitBlock(data.Block);

            bool indexed = false;
            foreach (var receipt in data.Receipts)
            {
                visitor.VisitReceipt(data.Block, receipt);
                if (receipt.Logs != null)
                {
                    for (int logIndex = 0; logIndex < receipt.Logs.Length; logIndex++)
                    {
                        var logEntry = receipt.Logs[logIndex];
                        visitor.VisitLog(data.Block, receipt, logEntry, logIndex);
                    }
                }

                visitor.LeaveReceipt(data.Block, receipt, indexed);
            }

            visitor.LeaveBlock(data.Block, indexed);
            _metrics.LogBlockWithReceipts(data);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return data;
    }

    private TransformBlock<long, Nethermind.Core.Block> BuildPipeline(CancellationToken cancellationToken)
    {
        var flushIntervalInBlocks = 100000;

        BlockHeader GenerateDummyBlockHeader(long blockNo)
        {
            var dummyHash = Keccak.Compute(new[] { (byte)blockNo });
            var dummyAddress = Address.Zero;
            return new(dummyHash, dummyHash, dummyAddress, 0, blockNo, 0, 0, Array.Empty<byte>(), 0, 0, dummyHash);
        }

        TransformBlock<long, Nethermind.Core.Block> blocks = new(
            blockNo =>
            {
                return blockNo < Caches.MaxKnownBlock && !Caches.KnownBlocks.ContainsKey(blockNo)
                    ? new Nethermind.Core.Block(GenerateDummyBlockHeader(blockNo))
                    : blockTree.FindBlock(blockNo) ?? throw new Exception($"Couldn't find block {blockNo}");
            }
            , CreateOptions(cancellationToken, 4));

        TransformBlock<Nethermind.Core.Block, BlockWithReceipts> receipts = new(
            block => new BlockWithReceipts(
                block
                , block.Number < Caches.MaxKnownBlock && !Caches.KnownBlocks.ContainsKey(block.Number)
                    ? []
                    : receiptFinder.Get(block))
            , CreateOptions(cancellationToken, 8));
        blocks.LinkTo(receipts);

        TransformBlock<BlockWithReceipts, BlockWithReceipts> sink = new(Sink,
            CreateOptions(cancellationToken, 16));
        receipts.LinkTo(sink);

        long accumulated = 0;
        bool isFlushing = false;
        ActionBlock<BlockWithReceipts> flush = new(_ =>
            {
                if (accumulated >= flushIntervalInBlocks && !isFlushing)
                {
                    isFlushing = true;
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    dataSink.Flush().ContinueWith(_ =>
                    {
                        isFlushing = false;
                        accumulated = 0;
                        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp;
                        Console.WriteLine($"Flushed {flushIntervalInBlocks} blocks in {elapsed}ms");
                    }, cancellationToken);
                }
                else
                {
                    accumulated++;
                }
            },
            CreateOptions(cancellationToken, flushIntervalInBlocks * 5, 1));
        sink.LinkTo(flush);

        return blocks;
    }

    public async Task<Range<long>> Run(IAsyncEnumerable<long> blocksToIndex, CancellationToken? cancellationToken)
    {
        var flushIntervalInMs = 5000;
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        TransformBlock<long, Nethermind.Core.Block> pipeline = BuildPipeline(CancellationToken.None);

        long min = long.MaxValue;
        long max = long.MinValue;

        if (cancellationToken == null)
        {
            CancellationTokenSource cts = new();
            cancellationToken = cts.Token;
        }

        var source = blocksToIndex.WithCancellation(cancellationToken.Value);
        var flushing = false;

        var timer = new Timer(e =>
        {
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;
            if (elapsed >= flushIntervalInMs && !flushing)
            {
                flushing = true;
                dataSink.Flush().ContinueWith(_ =>
                {
                    start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    flushing = false;
                });
            }
        }, null, flushIntervalInMs, flushIntervalInMs);

        await foreach (var blockNo in source)
        {
            await pipeline.SendAsync(blockNo, cancellationToken.Value);

            min = Math.Min(min, blockNo);
            max = Math.Max(max, blockNo);
        }

        pipeline.Complete();
        await pipeline.Completion;

        await timer.DisposeAsync();
        await dataSink.Flush();

        return new Range<long>
        {
            Min = min,
            Max = max
        };
    }
}
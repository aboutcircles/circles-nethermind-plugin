using System.Threading.Tasks.Dataflow;
using Circles.Index.Data.Cache;
using Circles.Index.Data.Sqlite;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Circles.Index.Data.Model;

namespace Circles.Index.Indexer;

public static class BlockIndexer
{
    public static async Task<Range<long>> IndexBlocks(
        IBlockTree blockTree,
        IReceiptFinder receiptFinder,
        MemoryCache cache,
        Sink sink,
        ILogger logger,
        IEnumerable<long> remainingKnownRelevantBlocks,
        CancellationToken cancellationToken,
        Settings settings)
    {
        int maxParallelism = Environment.ProcessorCount;
        switch (maxParallelism)
        {
            case >= 3:
                maxParallelism /= 3;
                maxParallelism *= 2;
                break;
            default:
                maxParallelism = 1;
                break;
        }
        
        if (settings.MaxParallelism > 0)
        {
            maxParallelism = settings.MaxParallelism;
        }

        logger.Debug($"Indexing blocks with max parallelism {maxParallelism}");

        TransformBlock<long, (long blockNo, ulong Timestamp, Hash256 blockHash, TxReceipt[] receipts)> getReceiptsBlock = new(
            blockNo => FindBlockReceipts(blockTree, receiptFinder, blockNo)
            , new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                EnsureOrdered = true,
                CancellationToken = cancellationToken
            });

        ActionBlock<(long blockNo, ulong timestamp, Hash256 blockHash, TxReceipt[] receipts)> indexReceiptsBlock = new(
            data =>
            {
                HashSet<(long BlockNo, ulong Timestamp, Hash256 BlockHash)> relevantBlocks =
                    ReceiptIndexer.IndexReceipts(data, settings, cache, sink);
                foreach ((long BlockNo, ulong Timestamp, Hash256 BlockHash) relevantBlock in relevantBlocks)
                {
                    sink.AddRelevantBlock(relevantBlock.BlockNo, relevantBlock.Timestamp, relevantBlock.BlockHash.ToString(true));
                }

                if (!relevantBlocks.Contains((data.blockNo, data.timestamp, data.blockHash)))
                {
                    sink.AddIrrelevantBlock(data.blockNo, data.timestamp, data.blockHash.ToString(true));
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1,
                BoundedCapacity = 1,
                EnsureOrdered = true,
                CancellationToken = cancellationToken
            });

        getReceiptsBlock.LinkTo(indexReceiptsBlock, new DataflowLinkOptions { PropagateCompletion = true });

        long min = long.MaxValue;
        long max = long.MinValue;
        
        foreach (long blockNo in remainingKnownRelevantBlocks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            getReceiptsBlock.Post(blockNo);
            
            min = Math.Min(min, blockNo);
            max = Math.Max(max, blockNo);
        }

        getReceiptsBlock.Complete();

        await indexReceiptsBlock.Completion;

        return new Range<long>
        {
            Min = min,
            Max = max
        };
    }

    private static (long BlockNumber, ulong timestamp, Hash256 BlockHash, TxReceipt[] Receipts) FindBlockReceipts(
        IBlockTree blockTree,
        IReceiptFinder receiptFinder,
        long blockNo)
    {
        Block? block = blockTree.FindBlock(blockNo);
        if (block == null)
        {
            throw new Exception($"Couldn't find block {blockNo}.");
        }

        TxReceipt[] receipts = receiptFinder.Get(block);
        return (blockNo, block.Header.Timestamp, block.Hash!, receipts);
    }
}
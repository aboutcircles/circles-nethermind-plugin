namespace Circles.Cache.Service;

/// <summary>
/// A thread-safe ring buffer that stores the last N blocks with their block numbers and hashes.
/// Used to detect chain reorganizations by comparing block hashes when new blocks arrive.
/// </summary>
public class BlockRingBuffer
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly List<(long BlockNumber, string BlockHash)> _blocks = new();

    /// <summary>
    /// Creates a new block ring buffer with the specified capacity.
    /// </summary>
    /// <param name="capacity">The number of blocks to retain (should match rollback capacity)</param>
    public BlockRingBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentException("Capacity must be at least 1", nameof(capacity));

        _capacity = capacity;
    }

    /// <summary>
    /// The number of blocks currently stored in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _blocks.Count;
            }
        }
    }

    /// <summary>
    /// Gets the highest block number in the buffer, or null if empty.
    /// </summary>
    public long? LatestBlockNumber
    {
        get
        {
            lock (_lock)
            {
                return _blocks.Count > 0 ? _blocks[^1].BlockNumber : null;
            }
        }
    }

    /// <summary>
    /// Adds a block to the ring buffer.
    /// </summary>
    /// <param name="blockNumber">The block number</param>
    /// <param name="blockHash">The block hash as a hex string</param>
    /// <exception cref="InvalidOperationException">If the block number is not greater than the latest</exception>
    public void Add(long blockNumber, string blockHash)
    {
        if (string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash cannot be null or empty", nameof(blockHash));

        lock (_lock)
        {
            if (_blocks.Count > 0 && blockNumber <= _blocks[^1].BlockNumber)
            {
                throw new InvalidOperationException(
                    $"Block number must be greater than current latest ({_blocks[^1].BlockNumber}). Got: {blockNumber}");
            }

            _blocks.Add((blockNumber, blockHash));

            // Remove oldest blocks if we exceed capacity
            while (_blocks.Count > _capacity)
            {
                _blocks.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Checks if the given block (by number and hash) matches what's stored in the buffer.
    /// Returns the reorg point (block number from which to rollback) if a mismatch is detected,
    /// or null if the block matches or is new.
    /// </summary>
    /// <param name="blockNumber">The block number to check</param>
    /// <param name="blockHash">The expected block hash</param>
    /// <returns>The block number to rollback to if a reorg is detected, otherwise null</returns>
    public long? DetectReorg(long blockNumber, string blockHash)
    {
        lock (_lock)
        {
            // Find the block in our buffer
            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                var (storedNumber, storedHash) = _blocks[i];

                if (storedNumber == blockNumber)
                {
                    // Same block number - check if hash matches (case-insensitive comparison)
                    if (!string.Equals(storedHash, blockHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Reorg detected! Need to rollback from this block
                        return blockNumber;
                    }
                    // Block matches, no reorg
                    return null;
                }

                if (storedNumber < blockNumber)
                {
                    // This is a new block beyond our current head
                    return null;
                }
            }

            // Block number not in buffer (either too old or buffer is empty)
            return null;
        }
    }

    /// <summary>
    /// Removes all blocks with block number >= the specified block number.
    /// Used when rolling back the cache due to a reorg.
    /// </summary>
    /// <param name="fromBlockNumber">The block number to rollback from (inclusive)</param>
    /// <returns>The number of blocks removed</returns>
    public int Rollback(long fromBlockNumber)
    {
        lock (_lock)
        {
            int initialCount = _blocks.Count;
            _blocks.RemoveAll(b => b.BlockNumber >= fromBlockNumber);
            return initialCount - _blocks.Count;
        }
    }

    /// <summary>
    /// Clears all blocks from the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _blocks.Clear();
        }
    }

    /// <summary>
    /// Gets a snapshot of all blocks currently in the buffer (ordered from oldest to newest).
    /// </summary>
    public IReadOnlyList<(long BlockNumber, string BlockHash)> GetSnapshot()
    {
        lock (_lock)
        {
            return new List<(long BlockNumber, string BlockHash)>(_blocks);
        }
    }

    /// <summary>
    /// Updates the buffer with a batch of blocks, typically from querying the database.
    /// Detects reorgs by comparing block hashes for any overlapping blocks.
    /// </summary>
    /// <param name="newBlocks">Blocks ordered from oldest to newest</param>
    /// <returns>The block number to rollback to if a reorg is detected, otherwise null</returns>
    public long? UpdateFromBlocks(IEnumerable<(long BlockNumber, string BlockHash)> newBlocks)
    {
        lock (_lock)
        {
            long? reorgPoint = null;
            var blockList = newBlocks.OrderBy(b => b.BlockNumber).ToList();

            foreach (var (blockNumber, blockHash) in blockList)
            {
                // Check for reorg
                var existingBlock = _blocks.FirstOrDefault(b => b.BlockNumber == blockNumber);
                if (existingBlock != default)
                {
                    if (!string.Equals(existingBlock.BlockHash, blockHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Reorg detected at this block
                        if (!reorgPoint.HasValue || blockNumber < reorgPoint.Value)
                        {
                            reorgPoint = blockNumber;
                        }
                    }
                }
            }

            if (reorgPoint.HasValue)
            {
                // Rollback our buffer to the reorg point
                Rollback(reorgPoint.Value);

                // Now add all blocks from reorg point onwards
                foreach (var (blockNumber, blockHash) in blockList.Where(b => b.BlockNumber >= reorgPoint.Value).OrderBy(b => b.BlockNumber))
                {
                    // Replace/add in sorted order
                    var idx = _blocks.FindIndex(b => b.BlockNumber == blockNumber);
                    if (idx >= 0)
                    {
                        _blocks[idx] = (blockNumber, blockHash);
                    }
                    else
                    {
                        // Add at the correct position
                        _blocks.Add((blockNumber, blockHash));
                    }
                }
            }
            else
            {
                // No reorg, just add new blocks that are ahead of our current head
                long currentHead = _blocks.Count > 0 ? _blocks[^1].BlockNumber : -1;
                foreach (var (blockNumber, blockHash) in blockList.Where(b => b.BlockNumber > currentHead).OrderBy(b => b.BlockNumber))
                {
                    _blocks.Add((blockNumber, blockHash));
                }
            }

            // Trim to capacity
            while (_blocks.Count > _capacity)
            {
                _blocks.RemoveAt(0);
            }

            return reorgPoint;
        }
    }
}

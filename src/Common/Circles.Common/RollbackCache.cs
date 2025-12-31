namespace Circles.Common;

public interface IRollbackCache
{
    /// <summary>The number of blocks that can be rolled back.</summary>
    int RollbackCapacity { get; }

    RollbackStats DeleteAllGreaterOrEqualBlock(long toBlockNo);

    int Count { get; }

    string Name { get; }
}

public readonly struct RollbackStats
{
    public int Removed { get; }
    public int Restored { get; }

    public RollbackStats(int removed, int restored)
    {
        Removed = removed;
        Restored = restored;
    }
}

/// <summary>
/// A cache that stores events for up to <see cref="RollbackCapacity"/> blocks and can be rolled back in
/// the case of a chain re-organisation. State is kept in <see cref="_current"/> while per-block diffs are
/// tracked so that reverting changes is O(changed items per block).
/// </summary>
public sealed class RollbackCache<TKey, TValue> : IRollbackCache where TKey : notnull
{
    private readonly struct Change
    {
        public readonly bool HadPrevious;
        public readonly TValue PreviousValue;

        public Change(bool hadPrevious, TValue previousValue)
        {
            HadPrevious = hadPrevious;
            PreviousValue = previousValue;
        }
    }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly Dictionary<TKey, TValue> _current = new();
    private readonly Dictionary<long, Dictionary<TKey, Change>> _blockDiffs = new();
    private readonly LinkedList<long> _blockOrder = new();

    public IReadOnlyDictionary<TKey, TValue> ReadOnlyDictionary
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return new Dictionary<TKey, TValue>(_current);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public long LastBlockNo => _lastBlockNo;
    private long _lastBlockNo = long.MinValue;

    public string Name { get; }

    /// <summary>Creates a new cache that can roll back the specified number of blocks.</summary>
    public RollbackCache(string name, int rollbackCapacity = 12)
    {
        Name = name;

        ArgumentOutOfRangeException.ThrowIfLessThan(rollbackCapacity, 1);
        RollbackCapacity = rollbackCapacity;
    }

    /// <summary>The number of blocks that can be rolled back.</summary>
    public int RollbackCapacity { get; }

    /// <summary>
    /// Replaces the current state with <paramref name="seedData"/> and resets history.
    /// After seeding, the next call to <see cref="Add"/> must use a block number greater than <paramref name="atBlockNo"/>.
    /// </summary>
    /// <param name="seedData">The data to seed the cache with.</param>
    /// <param name="atBlockNo">The block number at which this seed data is valid. Defaults to 0.</param>
    public void Seed(IReadOnlyDictionary<TKey, TValue> seedData, long atBlockNo = 0)
    {
        if (seedData is null) throw new ArgumentNullException(nameof(seedData));

        _lock.EnterWriteLock();
        try
        {
            _current.Clear();
            foreach (var (k, v) in seedData) _current[k] = v;

            _blockDiffs.Clear();
            _blockOrder.Clear();
            _lastBlockNo = atBlockNo;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds or replaces <paramref name="key"/> with <paramref name="value"/> at <paramref name="blockNo"/>.
    /// Calls must arrive in monotonically non-decreasing <paramref name="blockNo"/> order; whenever the
    /// number increases, the previous block is considered committed and a new diff bucket is started.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the key did not previously exist in the cache; <c>false</c> if it was overwritten.
    /// </returns>
    public bool Add(long blockNo, TKey key, TValue value)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        _lock.EnterWriteLock();
        try
        {
            if (blockNo < _lastBlockNo)
                throw new ArgumentException("Block number must be monotonically increasing.", nameof(blockNo));

            if (blockNo > _lastBlockNo)
            {
                _lastBlockNo = blockNo;
                _blockDiffs[blockNo] = new Dictionary<TKey, Change>();
                _blockOrder.AddLast(blockNo);

                if (_blockOrder.Count > RollbackCapacity)
                {
                    var oldest = _blockOrder.First!.Value;
                    _blockOrder.RemoveFirst();
                    _blockDiffs.Remove(oldest);
                }
            }

            var diff = _blockDiffs[blockNo];
            var hadPrev = _current.TryGetValue(key, out var prevVal);

            if (!diff.ContainsKey(key))
                diff[key] = new Change(hadPrev, prevVal!);

            _current[key] = value;
            return !hadPrev;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Deletes the state of every block whose number is <b>greater than</b> <paramref name="toBlockNo"/>.
    /// The cache is left in the exact state it had after finishing block <paramref name="toBlockNo"/>.
    /// The target block must still be within the retained history window.
    /// </summary>
    /// <summary>
    /// Deletes every block whose number is <b>&gt;=</b> <paramref name="toBlockNo"/>.
    /// After the call, the cache looks exactly as it did after finishing the block
    /// immediately preceding <paramref name="toBlockNo"/>.  
    /// If <paramref name="toBlockNo"/> is greater than the current head, the call
    /// is a no-op.  
    /// If the target precedes the retained history window an exception is thrown.
    /// </summary>
    public RollbackStats DeleteAllGreaterOrEqualBlock(long toBlockNo)
    {
        _lock.EnterWriteLock();
        try
        {
            // Nothing stored yet or already behind the requested head.
            if (_lastBlockNo == long.MinValue || _lastBlockNo < toBlockNo)
                return new RollbackStats(0, 0);

            if (_blockOrder.Count == 0)
                return new RollbackStats(0, 0);

            if (toBlockNo < _blockOrder.First!.Value)
                throw new InvalidOperationException("Cannot roll back beyond stored history.");

            // Roll back blocks one by one while _lastBlockNo >= toBlockNo
            var removed = 0;
            var restored = 0;
            while (_lastBlockNo >= toBlockNo)
            {
                var diff = _blockDiffs[_lastBlockNo];

                foreach (var (key, change) in diff)
                {
                    if (change.HadPrevious)
                    {
                        _current[key] = change.PreviousValue;
                        restored++;
                    }
                    else
                    {
                        _current.Remove(key);
                        removed++;
                    }
                }

                _blockDiffs.Remove(_lastBlockNo);
                _blockOrder.RemoveLast();
                _lastBlockNo = _blockOrder.Count > 0 ? _blockOrder.Last!.Value : long.MinValue;
            }

            return new RollbackStats(removed, restored);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _current.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public bool Remove(TKey key)
    {
        _lock.EnterWriteLock();
        try
        {
            return _current.Remove(key);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool ContainsKey(TKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _current.ContainsKey(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public TValue Get(TKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _current[key];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        _lock.EnterReadLock();
        try
        {
            return _current.TryGetValue(key, out value!);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
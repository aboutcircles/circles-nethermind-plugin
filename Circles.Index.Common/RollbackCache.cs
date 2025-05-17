namespace Circles.Index.Common;

/// <summary>
/// A cache that stores events for up to <see cref="RollbackCapacity"/> blocks and can be rolled back in
/// the case of a chain re-organisation. State is kept in <see cref="_current"/> while per-block diffs are
/// tracked so that reverting changes is O(changed items per block).
/// </summary>
public sealed class RollbackCache<TKey, TValue> where TKey : notnull
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

    private long _lastBlockNo = long.MinValue;

    /// <summary>Creates a new cache that can roll back the specified number of blocks.</summary>
    public RollbackCache(int rollbackCapacity = 12)
    {
        if (rollbackCapacity < 1) throw new ArgumentOutOfRangeException(nameof(rollbackCapacity));
        RollbackCapacity = rollbackCapacity;
    }

    /// <summary>The number of blocks that can be rolled back.</summary>
    public int RollbackCapacity { get; }

    /// <summary>
    /// Replaces the current state with <paramref name="seedData"/> and resets history.  
    /// After seeding, the next call to <see cref="Add"/> must use a block number greater than 0.
    /// </summary>
    public void Seed(IReadOnlyDictionary<TKey, TValue> seedData)
    {
        if (seedData is null) throw new ArgumentNullException(nameof(seedData));

        _lock.EnterWriteLock();
        try
        {
            _current.Clear();
            foreach (var (k, v) in seedData) _current[k] = v;

            _blockDiffs.Clear();
            _blockOrder.Clear();
            _lastBlockNo = 0;
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
    /// <returns>'true' if the key is new, 'false' if it already existed and is overwritten</returns>
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
            {
                diff[key] = new Change(hadPrev, prevVal!);
            }

            _current[key] = value;

            return !hadPrev;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rolls the cache back to <paramref name="toBlockNo"/> (inclusive).  
    /// The target must be within the retained history window.
    /// </summary>
    public void Rollback(long toBlockNo)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_blockOrder.Count == 0 || toBlockNo < _blockOrder.First!.Value)
                throw new InvalidOperationException("Cannot roll back beyond stored history.");

            while (_lastBlockNo > toBlockNo)
            {
                var diff = _blockDiffs[_lastBlockNo];

                foreach (var (key, change) in diff)
                {
                    if (change.HadPrevious)
                        _current[key] = change.PreviousValue;
                    else
                        _current.Remove(key);
                }

                _blockDiffs.Remove(_lastBlockNo);
                _blockOrder.RemoveLast();
                _lastBlockNo = _blockOrder.Count > 0 ? _blockOrder.Last!.Value : toBlockNo;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
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
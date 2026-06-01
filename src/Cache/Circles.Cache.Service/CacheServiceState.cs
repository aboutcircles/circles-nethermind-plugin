namespace Circles.Cache.Service;

/// <summary>
/// Tracks the state of the Cache Service including warmup status, processed blocks, and cache instances.
/// Thread-safe for concurrent access.
/// </summary>
public class CacheServiceState
{
    private readonly object _lock = new();
    private long _lastProcessedBlock = -1;
    private long _warmupTargetBlock = -1;
    private long _currentBlockTimestamp = 0;
    private bool _warmupComplete = false;
    private bool _listenerConnected = false;

    /// <summary>
    /// Ring buffer to track recent blocks and detect reorgs.
    /// </summary>
    public BlockRingBuffer BlockRingBuffer { get; }

    /// <param name="rollbackCapacity">Blocks of balance-diff history retained for rollback.</param>
    /// <param name="reorgDetectionWindow">
    /// Block hashes retained for reorg detection. Defaults to <paramref name="rollbackCapacity"/>
    /// when not supplied (preserves the pre-decoupling behavior). When larger, reorgs deeper than
    /// the rollback window are still detected and trigger a safe full re-warmup.
    /// </param>
    public CacheServiceState(int rollbackCapacity, int? reorgDetectionWindow = null)
    {
        BlockRingBuffer = new BlockRingBuffer(reorgDetectionWindow ?? rollbackCapacity);
    }

    /// <summary>
    /// The target block number that warmup should reach.
    /// This is set at the start of warmup to the database head at that time.
    /// </summary>
    public long WarmupTargetBlock
    {
        get
        {
            lock (_lock)
            {
                return _warmupTargetBlock;
            }
        }
        set
        {
            lock (_lock)
            {
                _warmupTargetBlock = value;
            }
        }
    }

    /// <summary>
    /// The last block number that has been successfully processed and applied to all caches.
    /// -1 indicates no blocks have been processed yet.
    /// </summary>
    public long LastProcessedBlock
    {
        get
        {
            lock (_lock)
            {
                return _lastProcessedBlock;
            }
        }
        set
        {
            lock (_lock)
            {
                _lastProcessedBlock = value;
            }
        }
    }

    /// <summary>
    /// Timestamp (unix seconds) of the latest canonical block the cache has processed.
    /// Used for evaluating time-dependent state (e.g. V2 trust expiry) against chain time,
    /// not wall-clock time.
    /// </summary>
    public long CurrentBlockTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _currentBlockTimestamp;
            }
        }
        set
        {
            lock (_lock)
            {
                _currentBlockTimestamp = value;
            }
        }
    }

    /// <summary>
    /// Indicates whether the initial warmup (full replay from Postgres) has completed.
    /// </summary>
    public bool WarmupComplete
    {
        get
        {
            lock (_lock)
            {
                return _warmupComplete;
            }
        }
        set
        {
            lock (_lock)
            {
                _warmupComplete = value;
            }
        }
    }

    /// <summary>
    /// Indicates whether the pg_notify listener is currently connected.
    /// </summary>
    public bool ListenerConnected
    {
        get
        {
            lock (_lock)
            {
                return _listenerConnected;
            }
        }
        set
        {
            lock (_lock)
            {
                _listenerConnected = value;
            }
        }
    }

    /// <summary>
    /// Checks if the service is ready to serve requests.
    /// </summary>
    public bool IsReady(long dbHead, int maxLag)
    {
        lock (_lock)
        {
            if (!_warmupComplete)
                return false;

            if (!_listenerConnected)
                return false;

            // Check if we're within acceptable lag
            var lag = dbHead - _lastProcessedBlock;
            return lag <= maxLag;
        }
    }

    /// <summary>
    /// Gets the current lag (in blocks) behind the database head.
    /// </summary>
    public long GetLag(long dbHead)
    {
        lock (_lock)
        {
            return dbHead - _lastProcessedBlock;
        }
    }
}

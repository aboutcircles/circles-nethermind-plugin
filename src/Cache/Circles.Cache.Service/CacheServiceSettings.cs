namespace Circles.Cache.Service;

/// <summary>
/// Configuration settings for the Circles Cache Service.
/// Reads from environment variables and provides defaults.
/// </summary>
public class CacheServiceSettings
{
    /// <summary>
    /// PostgreSQL connection string with write access (for pg_notify operations).
    /// Environment variable: POSTGRES_CONNECTION_STRING
    /// </summary>
    public string PostgresConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// PostgreSQL read-only connection string for queries.
    /// Environment variable: POSTGRES_READONLY_CONNECTION_STRING
    /// Falls back to PostgresConnectionString if not set.
    /// </summary>
    public string PostgresReadonlyConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// PostgreSQL NOTIFY channel name for block import notifications.
    /// Environment variable: CIRCLES_PG_NOTIFY_CHANNEL
    /// Default: "circles_index_events"
    /// </summary>
    public string PgNotifyChannel { get; init; } = "circles_index_events";

    /// <summary>
    /// Number of blocks to retain in rollback history for each cache.
    /// Environment variable: ROLLBACK_CAPACITY
    /// Default: 12 blocks
    /// </summary>
    public int RollbackCapacity { get; init; } = 12;

    /// <summary>
    /// Number of recent block hashes retained for reorg DETECTION (the BlockRingBuffer size and the
    /// number of blocks fetched per notification). Decoupled from <see cref="RollbackCapacity"/>:
    /// the rollback fast-path can only undo <see cref="RollbackCapacity"/> blocks of balance diffs,
    /// but reorgs deeper than that must still be DETECTED so they trigger a safe full re-warmup
    /// instead of being silently missed (which leaves the in-memory balances desynced from the DB
    /// until the next rewarmup). Block hashes are cheap (~70 bytes each), so this can be much larger
    /// than the rollback window. Must be >= RollbackCapacity.
    /// Environment variable: REORG_DETECTION_WINDOW
    /// Default: 256 blocks
    /// </summary>
    public int ReorgDetectionWindow { get; init; } = 256;

    /// <summary>
    /// Maximum lag (in blocks) allowed before the service is considered "not ready".
    /// Environment variable: MAX_CATCHUP_LAG
    /// Default: 10 blocks
    /// </summary>
    public int MaxCatchupLag { get; init; } = 10;

    /// <summary>
    /// Whether the tail self-heal reconciliation runs. When enabled, every
    /// <see cref="ReconciliationIntervalBlocks"/> blocks the service re-derives the balances of
    /// recently-active accounts directly from the authoritative DB aggregation and overwrites any
    /// that drifted (e.g. a reorg-triggered one-sided over-credit on a group-mint router that the
    /// incremental path advanced past and never re-read). Set false to disable without redeploying.
    /// Environment variable: RECONCILIATION_ENABLED
    /// Default: true
    /// </summary>
    public bool ReconciliationEnabled { get; init; } = true;

    /// <summary>
    /// Window (in blocks, back from the cache head) of accounts to re-derive on each reconciliation
    /// pass. Must comfortably exceed <see cref="ReorgDetectionWindow"/> is NOT required, but it
    /// should cover the depth at which reorg-induced drift can form; the wider it is, the longer a
    /// stuck phantom keeps being re-checked. Bounded by the per-account composite index, so cost
    /// scales with distinct accounts active in the window, not full history.
    /// Environment variable: RECONCILIATION_WINDOW_BLOCKS
    /// Default: 256 blocks
    /// </summary>
    public int ReconciliationWindowBlocks { get; init; } = 256;

    /// <summary>
    /// Minimum number of blocks the cache head must advance between reconciliation passes. Throttles
    /// the (DB-bound) reconciliation off the per-block notification cadence. At ~5s/block, the
    /// default ≈ 40s between passes.
    /// Environment variable: RECONCILIATION_INTERVAL_BLOCKS
    /// Default: 8 blocks
    /// </summary>
    public int ReconciliationIntervalBlocks { get; init; } = 8;

    /// <summary>
    /// Safety cap on the number of recently-active accounts a single reconciliation pass will
    /// process. Because reconciliation runs inline on the block-ingestion path, a pathological
    /// window (airdrop, bulk migration) returning tens of thousands of accounts could stall
    /// ingestion. If the active-account set exceeds this, the pass is skipped (counted via
    /// <c>circles_cache_reconciliation_skipped_oversized_total</c>) rather than risking a long
    /// inline query; a smaller subsequent window then heals normally.
    /// Environment variable: RECONCILIATION_MAX_ACCOUNTS
    /// Default: 5000 accounts
    /// </summary>
    public int ReconciliationMaxAccounts { get; init; } = 5000;

    /// <summary>
    /// HTTP port for the service (health checks and API endpoints).
    /// Environment variable: PORT
    /// Default: 5002
    /// </summary>
    public int Port { get; init; } = 5002;

    /// <summary>
    /// Maximum number of IPFS profile content entries to cache.
    /// Environment variable: IPFS_CACHE_MAX_ENTRIES
    /// Default: 50000
    /// </summary>
    public int IpfsCacheMaxEntries { get; init; } = 50000;

    /// <summary>
    /// Gets the effective readonly connection string (falls back to main connection if not set).
    /// </summary>
    public string EffectiveReadonlyConnectionString =>
        string.IsNullOrWhiteSpace(PostgresReadonlyConnectionString)
            ? PostgresConnectionString
            : PostgresReadonlyConnectionString;

    /// <summary>
    /// Validates that required settings are configured.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            throw new InvalidOperationException(
                "POSTGRES_CONNECTION_STRING environment variable is required");
        }

        if (RollbackCapacity < 1 || RollbackCapacity > 1000)
        {
            throw new InvalidOperationException(
                $"ROLLBACK_CAPACITY must be between 1 and 1000 (got {RollbackCapacity})");
        }

        if (ReorgDetectionWindow < RollbackCapacity || ReorgDetectionWindow > 10000)
        {
            throw new InvalidOperationException(
                $"REORG_DETECTION_WINDOW must be between RollbackCapacity ({RollbackCapacity}) and 10000 (got {ReorgDetectionWindow})");
        }

        if (MaxCatchupLag < 0)
        {
            throw new InvalidOperationException(
                $"MAX_CATCHUP_LAG must be non-negative (got {MaxCatchupLag})");
        }

        if (ReconciliationWindowBlocks < 1 || ReconciliationWindowBlocks > 100000)
        {
            throw new InvalidOperationException(
                $"RECONCILIATION_WINDOW_BLOCKS must be between 1 and 100000 (got {ReconciliationWindowBlocks})");
        }

        if (ReconciliationIntervalBlocks < 1)
        {
            throw new InvalidOperationException(
                $"RECONCILIATION_INTERVAL_BLOCKS must be >= 1 (got {ReconciliationIntervalBlocks})");
        }

        if (ReconciliationMaxAccounts < 1)
        {
            throw new InvalidOperationException(
                $"RECONCILIATION_MAX_ACCOUNTS must be >= 1 (got {ReconciliationMaxAccounts})");
        }
    }

    /// <summary>
    /// Creates settings from environment variables.
    /// </summary>
    public static CacheServiceSettings FromEnvironment()
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString =
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? string.Empty,
            PostgresReadonlyConnectionString =
                Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING") ??
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? string.Empty,
            PgNotifyChannel =
                Environment.GetEnvironmentVariable("CIRCLES_PG_NOTIFY_CHANNEL") ?? "circles_index_events",
            RollbackCapacity =
                int.TryParse(Environment.GetEnvironmentVariable("ROLLBACK_CAPACITY"), out var capacity)
                    ? capacity
                    : 12,
            ReorgDetectionWindow =
                int.TryParse(Environment.GetEnvironmentVariable("REORG_DETECTION_WINDOW"), out var detectionWindow)
                    ? detectionWindow
                    : 256,
            MaxCatchupLag =
                int.TryParse(Environment.GetEnvironmentVariable("MAX_CATCHUP_LAG"), out var lag)
                    ? lag
                    : 10,
            ReconciliationEnabled =
                !bool.TryParse(Environment.GetEnvironmentVariable("RECONCILIATION_ENABLED"), out var reconEnabled)
                || reconEnabled,
            ReconciliationWindowBlocks =
                int.TryParse(Environment.GetEnvironmentVariable("RECONCILIATION_WINDOW_BLOCKS"), out var reconWindow)
                    ? reconWindow
                    : 256,
            ReconciliationIntervalBlocks =
                int.TryParse(Environment.GetEnvironmentVariable("RECONCILIATION_INTERVAL_BLOCKS"), out var reconInterval)
                    ? reconInterval
                    : 8,
            ReconciliationMaxAccounts =
                int.TryParse(Environment.GetEnvironmentVariable("RECONCILIATION_MAX_ACCOUNTS"), out var reconMaxAccounts)
                    ? reconMaxAccounts
                    : 5000,
            Port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) ? port : 5002,
            IpfsCacheMaxEntries = int.TryParse(Environment.GetEnvironmentVariable("IPFS_CACHE_MAX_ENTRIES"), out var ipfsMax) ? ipfsMax : 50000
        };

        settings.Validate();
        return settings;
    }
}

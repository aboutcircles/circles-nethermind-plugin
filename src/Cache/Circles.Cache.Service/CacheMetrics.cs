using Prometheus;

namespace Circles.Cache.Service.Metrics;

/// <summary>
/// Prometheus metrics for the Circles Cache Service.
/// </summary>
public static class CacheMetrics
{
    // Cache state metrics
    public static readonly Gauge LastProcessedBlock = Prometheus.Metrics
        .CreateGauge("circles_cache_last_processed_block",
            "The last block number processed by the cache service");

    public static readonly Gauge DatabaseLag = Prometheus.Metrics
        .CreateGauge("circles_cache_database_lag_blocks",
            "Number of blocks the cache is behind the database");

    public static readonly Gauge WarmupComplete = Prometheus.Metrics
        .CreateGauge("circles_cache_warmup_complete",
            "1 if warmup is complete, 0 otherwise");

    public static readonly Gauge ListenerConnected = Prometheus.Metrics
        .CreateGauge("circles_cache_listener_connected",
            "1 if pg_notify listener is connected, 0 otherwise");

    // Cache size metrics
    public static readonly Gauge V1Avatars = Prometheus.Metrics
        .CreateGauge("circles_cache_v1_avatars_total",
            "Total number of V1 avatars in cache");

    public static readonly Gauge V2Avatars = Prometheus.Metrics
        .CreateGauge("circles_cache_v2_avatars_total",
            "Total number of V2 avatars in cache");

    public static readonly Gauge Groups = Prometheus.Metrics
        .CreateGauge("circles_cache_groups_total",
            "Total number of groups in cache");

    public static readonly Gauge V1Balances = Prometheus.Metrics
        .CreateGauge("circles_cache_v1_balances_total",
            "Total number of V1 balance entries in cache");

    public static readonly Gauge V2Balances = Prometheus.Metrics
        .CreateGauge("circles_cache_v2_balances_total",
            "Total number of V2 balance entries in cache");

    public static readonly Gauge IndexedAddressesV1 = Prometheus.Metrics
        .CreateGauge("circles_cache_indexed_addresses_v1_total",
            "Total number of V1 addresses in secondary index");

    public static readonly Gauge IndexedAddressesV2 = Prometheus.Metrics
        .CreateGauge("circles_cache_indexed_addresses_v2_total",
            "Total number of V2 addresses in secondary index");

    public static readonly Gauge TotalCacheEntries = Prometheus.Metrics
        .CreateGauge("circles_cache_entries_total",
            "Total number of entries across all caches");

    // Operation counters
    public static readonly Counter ReorgsDetected = Prometheus.Metrics
        .CreateCounter("circles_cache_reorgs_detected_total",
            "Total number of blockchain reorgs detected and handled");

    public static readonly Counter BlocksProcessed = Prometheus.Metrics
        .CreateCounter("circles_cache_blocks_processed_total",
            "Total number of blocks processed by the cache service");

    public static readonly Counter NotificationsReceived = Prometheus.Metrics
        .CreateCounter("circles_cache_notifications_received_total",
            "Total number of pg_notify notifications received");

    // API request metrics (supplement HTTP metrics)
    public static readonly Counter BalanceQueriesTotal = Prometheus.Metrics
        .CreateCounter("circles_cache_balance_queries_total",
            "Total number of balance queries",
            new CounterConfiguration { LabelNames = new[] { "version" } });

    public static readonly Counter AvatarQueriesTotal = Prometheus.Metrics
        .CreateCounter("circles_cache_avatar_queries_total",
            "Total number of avatar info queries",
            new CounterConfiguration { LabelNames = new[] { "batch" } });

    public static readonly Counter ProfileQueriesTotal = Prometheus.Metrics
        .CreateCounter("circles_cache_profile_queries_total",
            "Total number of profile CID queries",
            new CounterConfiguration { LabelNames = new[] { "batch" } });

    // Performance metrics
    public static readonly Histogram BalanceQueryDuration = Prometheus.Metrics
        .CreateHistogram("circles_cache_balance_query_duration_seconds",
            "Duration of balance queries in seconds",
            new HistogramConfiguration
            {
                LabelNames = new[] { "version" },
                Buckets = Histogram.ExponentialBuckets(0.001, 2, 10) // 1ms to ~1s
            });
}

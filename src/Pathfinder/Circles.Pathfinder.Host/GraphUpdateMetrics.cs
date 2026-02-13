using Prometheus;

namespace Circles.Pathfinder.Host;

/// <summary>
/// Prometheus metrics for monitoring graph update health and performance.
/// Use these metrics to set up alerts for stale data and update failures.
/// </summary>
internal static class GraphUpdateMetrics
{
    /// <summary>
    /// Counter for total graph update attempts, labeled by status (success/failure).
    /// </summary>
    public static readonly Counter UpdateTotal = Metrics.CreateCounter(
        "circles_graph_update_total",
        "Total graph update attempts",
        new CounterConfiguration { LabelNames = new[] { "status" } });

    /// <summary>
    /// Histogram for graph update duration in seconds, labeled by graph type.
    /// Buckets: 0.1s to ~100s (exponential)
    /// </summary>
    public static readonly Histogram UpdateDuration = Metrics.CreateHistogram(
        "circles_graph_update_duration_seconds",
        "Graph update duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "graph" },
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 10) // 0.1, 0.2, 0.4, ... ~102s
        });

    /// <summary>
    /// Current number of consecutive errors (0 when healthy, max 5 before crash).
    /// Alert when this exceeds 2-3.
    /// </summary>
    public static readonly Gauge ConsecutiveErrors = Metrics.CreateGauge(
        "circles_graph_consecutive_errors",
        "Current consecutive error count (0-5, crashes at 5)");

    /// <summary>
    /// Unix timestamp of the last successful graph update.
    /// Alert when (current_time - this) > 5 minutes.
    /// </summary>
    public static readonly Gauge LastUpdateTimestamp = Metrics.CreateGauge(
        "circles_graph_last_update_timestamp",
        "Unix timestamp of last successful graph update");

    /// <summary>
    /// Block number of the last successfully processed block.
    /// </summary>
    public static readonly Gauge LastProcessedBlock = Metrics.CreateGauge(
        "circles_graph_last_processed_block",
        "Block number of last successful graph update");

    // O3: Graph size gauges — set after each successful update
    public static readonly Gauge AvatarCount = Metrics.CreateGauge(
        "circles_graph_avatar_count",
        "Number of avatars in the balance graph");

    public static readonly Gauge EdgeCount = Metrics.CreateGauge(
        "circles_graph_edge_count",
        "Number of edges in the capacity graph");

    public static readonly Gauge BalanceCount = Metrics.CreateGauge(
        "circles_graph_balance_count",
        "Number of balance nodes in the balance graph");

    public static readonly Gauge GroupCount = Metrics.CreateGauge(
        "circles_graph_group_count",
        "Number of group nodes in the capacity graph");

    // O9: Address pool size
    public static readonly Gauge AddressPoolSize = Metrics.CreateGauge(
        "circles_address_pool_size",
        "Number of entries in the AddressIdPool");

    // O4: DB query duration — labeled by query name (balances, trust, groups, group_trusts, consented_flow)
    public static readonly Histogram DbQueryDuration = Metrics.CreateHistogram(
        "circles_db_query_duration_seconds",
        "Duration of individual DB queries in the LoadGraph service",
        new HistogramConfiguration
        {
            LabelNames = new[] { "query" },
            Buckets = Histogram.ExponentialBuckets(0.01, 2, 12) // 10ms to ~41s
        });

    public static readonly Counter PathfinderGraphSourceTotal = Metrics.CreateCounter(
        "circles_pathfinder_graph_source",
        "Pathfinder graph data source usage",
        new CounterConfiguration { LabelNames = new[] { "source" } });

    public static readonly Histogram CacheGraphFetchDuration = Metrics.CreateHistogram(
        "circles_pathfinder_cache_graph_fetch_duration_seconds",
        "Cache graph fetch duration in seconds",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.01, 2, 12) });

    public static readonly Histogram CacheGraphPayloadBytes = Metrics.CreateHistogram(
        "circles_pathfinder_cache_graph_payload_bytes",
        "Cache graph payload size in bytes",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1024, 2, 12) });

    public static readonly Counter CacheGraphNotModifiedTotal = Metrics.CreateCounter(
        "circles_pathfinder_cache_graph_not_modified_total",
        "Total number of cache graph 304 Not Modified responses");

    public static readonly Counter CacheGraphErrorsTotal = Metrics.CreateCounter(
        "circles_pathfinder_cache_graph_errors_total",
        "Total number of cache graph fetch errors");
}

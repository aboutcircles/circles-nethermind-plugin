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
}

// Circles.Pathfinder.Host/Constants/LoggingConstants.cs
namespace Circles.Pathfinder.Host;

/// <summary>
/// Central place for tunable timings & log settings.
/// </summary>
public static class Constants
{
    // Request-level
    public const int SlowRequestThresholdMs = 2_000;
    public const int FindPathSlaMs          = 1_500;

    // Aggregation
    public static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);

    // Blockchain polling
    public const int MaxGetBlockErrors = 20;

    // Histogram buckets (same as before)
    public static readonly double[] RequestDurationBuckets =
        Prometheus.Histogram.ExponentialBuckets(0.005, 2, 15);
}
using Prometheus;

namespace Circles.Rpc.Host;

/// <summary>
/// Prometheus metrics for RPC method-level tracking.
/// </summary>
public static class RpcMetrics
{
    /// <summary>
    /// Total RPC requests by method.
    /// </summary>
    public static readonly Counter RequestsTotal = Metrics.CreateCounter(
        "circles_rpc_requests_total",
        "Total number of RPC requests",
        new CounterConfiguration { LabelNames = new[] { "method" } });

    /// <summary>
    /// RPC request duration by method.
    /// </summary>
    public static readonly Histogram RequestDuration = Metrics.CreateHistogram(
        "circles_rpc_request_duration_seconds",
        "RPC request duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method" },
            Buckets = new[] { 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0 }
        });

    /// <summary>
    /// RPC errors by method and error type.
    /// </summary>
    public static readonly Counter ErrorsTotal = Metrics.CreateCounter(
        "circles_rpc_errors_total",
        "Total number of RPC errors",
        new CounterConfiguration { LabelNames = new[] { "method", "error_type" } });

    /// <summary>
    /// Currently in-flight RPC requests by method.
    /// </summary>
    public static readonly Gauge InFlightRequests = Metrics.CreateGauge(
        "circles_rpc_inflight_requests",
        "Number of RPC requests currently being processed",
        new GaugeConfiguration { LabelNames = new[] { "method" } });

    /// <summary>
    /// Active WebSocket subscriptions.
    /// </summary>
    public static readonly Gauge ActiveSubscriptions = Metrics.CreateGauge(
        "circles_rpc_active_subscriptions",
        "Number of active WebSocket subscriptions");

    /// <summary>
    /// Total proxied RPC requests forwarded to Nethermind by method.
    /// </summary>
    public static readonly Counter ProxiedTotal = Metrics.CreateCounter(
        "circles_rpc_proxied_total",
        "Total number of RPC requests proxied to Nethermind",
        new CounterConfiguration { LabelNames = new[] { "method" } });

    /// <summary>
    /// Proxied RPC request duration by method.
    /// </summary>
    public static readonly Histogram ProxyDuration = Metrics.CreateHistogram(
        "circles_rpc_proxy_duration_seconds",
        "Proxied RPC request duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method" },
            Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0 }
        });
}

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
    /// Active Circles WebSocket subscriptions (via CirclesSubscriptionService).
    /// </summary>
    public static readonly Gauge ActiveCirclesSubscriptions = Metrics.CreateGauge(
        "circles_rpc_active_circles_subscriptions",
        "Number of active Circles WebSocket subscriptions");

    /// <summary>
    /// Active Ethereum WebSocket subscriptions (proxied to Nethermind).
    /// </summary>
    public static readonly Gauge ActiveEthSubscriptions = Metrics.CreateGauge(
        "circles_rpc_active_eth_subscriptions",
        "Number of active eth_subscribe WebSocket subscriptions");

    /// <summary>
    /// Total eth_subscribe subscriptions created.
    /// </summary>
    public static readonly Counter EthSubscriptionsTotal = Metrics.CreateCounter(
        "circles_rpc_eth_subscriptions_total",
        "Total eth_subscribe subscriptions created");

    /// <summary>
    /// Nethermind WebSocket reconnection count.
    /// </summary>
    public static readonly Counter NethermindWsReconnects = Metrics.CreateCounter(
        "circles_rpc_nethermind_ws_reconnects_total",
        "Number of Nethermind WebSocket reconnections");

    /// <summary>
    /// Active WebSocket sessions (client connections).
    /// </summary>
    public static readonly Gauge ActiveWsSessions = Metrics.CreateGauge(
        "circles_rpc_active_ws_sessions",
        "Number of active WebSocket client sessions");

    /// <summary>
    /// Total RPC requests rejected due to concurrency limit.
    /// </summary>
    public static readonly Counter RejectedTotal = Metrics.CreateCounter(
        "circles_rpc_rejected_total",
        "Total number of RPC requests rejected due to concurrency limit");

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

    /// <summary>
    /// Total requests rejected by per-IP rate limiting.
    /// </summary>
    public static readonly Counter RateLimitedTotal = Metrics.CreateCounter(
        "circles_rpc_rate_limited_total",
        "Total number of RPC requests rejected by per-IP rate limiting");

    /// <summary>
    /// Total batch requests received.
    /// </summary>
    public static readonly Counter BatchTotal = Metrics.CreateCounter(
        "circles_rpc_batch_total",
        "Total number of batch JSON-RPC requests");

    /// <summary>
    /// Batch size distribution (items per batch).
    /// </summary>
    public static readonly Histogram BatchSize = Metrics.CreateHistogram(
        "circles_rpc_batch_size",
        "Number of items per batch request",
        new HistogramConfiguration
        {
            Buckets = new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 30.0, 50.0 }
        });

    // ---- circles_searchProfiles instrumentation (Phase 4 measurement) ----
    // Path label values (kept here so dashboards/alerts can reference one source):
    //   proxy_hit              proxy returned >=1 result, no SQL ran
    //   proxy_zero_then_sql    proxy returned [], SQL ran as fallback
    //   proxy_error_then_sql   proxy non-2xx, SQL ran as fallback
    //   proxy_timeout_then_sql proxy threw timeout/transport, SQL ran as fallback
    //   sql_only               ProfilePinningServiceUrl unset, SQL ran directly
    //   short_circuit_empty    all tokens <=1 char, returned empty without searching

    /// <summary>
    /// Total circles_searchProfiles requests, labelled by which code path served them.
    /// </summary>
    public static readonly Counter SearchProfilesPathTotal = Metrics.CreateCounter(
        "circles_search_profiles_path_total",
        "Total circles_searchProfiles requests by served path",
        new CounterConfiguration { LabelNames = new[] { "path" } });

    /// <summary>
    /// End-to-end circles_searchProfiles duration (RPC entry → return), by path.
    /// Includes the proxy attempt latency for *_then_sql paths.
    /// </summary>
    public static readonly Histogram SearchProfilesDuration = Metrics.CreateHistogram(
        "circles_search_profiles_duration_seconds",
        "circles_searchProfiles end-to-end duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "path" },
            Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0, 20.0 }
        });

    /// <summary>
    /// Distribution of search query lengths after trim, observed for every request
    /// (including short-circuit-empty rejections).
    /// </summary>
    public static readonly Histogram SearchProfilesQueryLength = Metrics.CreateHistogram(
        "circles_search_profiles_query_length_chars",
        "Distribution of circles_searchProfiles query lengths in characters",
        new HistogramConfiguration
        {
            Buckets = new[] { 1.0, 2.0, 3.0, 5.0, 10.0, 20.0, 50.0 }
        });

    /// <summary>
    /// Total circles_searchProfiles requests that returned zero results, by path.
    /// Useful for spotting paths where the proxy is "successful" but useless.
    /// </summary>
    public static readonly Counter SearchProfilesZeroResultsTotal = Metrics.CreateCounter(
        "circles_search_profiles_zero_results_total",
        "Total circles_searchProfiles requests that returned an empty result set",
        new CounterConfiguration { LabelNames = new[] { "path" } });
}

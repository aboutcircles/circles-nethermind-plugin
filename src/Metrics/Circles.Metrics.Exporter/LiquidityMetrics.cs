using Prometheus;

namespace Circles.Metrics.Exporter;

/// <summary>
/// Prometheus metrics for Circles liquidity monitoring, drain detection, and whale tracking.
/// These metrics are derived from PostgreSQL queries run periodically.
/// </summary>
public static class LiquidityMetrics
{
    // ===========================================
    // Balancer Pool Liquidity Metrics
    // ===========================================

    public static readonly Gauge BalancerVaultBalance = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_balance",
            "Current token balance in Balancer vault (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "token_name" } });

    public static readonly Gauge BalancerVaultBalanceTotal = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_balance_total",
            "Sum of all token balances in Balancer vault (in wei)");

    public static readonly Gauge BalancerVaultTokensCount = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_tokens_count",
            "Number of distinct tokens with liquidity in Balancer vault");

    // ===========================================
    // Group Treasury Liquidity Metrics
    // ===========================================

    public static readonly Gauge GroupTreasuryBalance = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_balance",
            "Collateral balance per token in group treasury (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "group_address", "group_name", "token_id" } });

    public static readonly Gauge GroupTreasuryTotal = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_total",
            "Total collateral value per group (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "group_address", "group_name" } });

    public static readonly Gauge GroupMemberCount = Prometheus.Metrics
        .CreateGauge("circles_group_member_count",
            "Number of members in group",
            new GaugeConfiguration { LabelNames = new[] { "group_address", "group_name" } });

    // ===========================================
    // Draining Detection Metrics (Z-Score Based)
    // ===========================================

    public static readonly Gauge BalancerVaultChange1h = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_change_1h",
            "Balance change in last hour (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "token_address" } });

    public static readonly Gauge BalancerVaultChange24h = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_change_24h",
            "Balance change in last 24 hours (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "token_address" } });

    public static readonly Gauge BalancerVaultZScore1h = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_zscore_1h",
            "Z-score of 1h change vs 30d historical standard deviation",
            new GaugeConfiguration { LabelNames = new[] { "token_address" } });

    public static readonly Gauge BalancerVaultAnomaly = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_anomaly",
            "1 if anomaly detected, 0 otherwise",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "severity" } });

    public static readonly Counter BalancerVaultDrainEvents = Prometheus.Metrics
        .CreateCounter("circles_balancer_vault_drain_events_total",
            "Cumulative count of drain anomalies detected",
            new CounterConfiguration { LabelNames = new[] { "token_address", "severity" } });

    public static readonly Gauge GroupTreasuryChange24h = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_change_24h",
            "Treasury change in last 24 hours (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "group_address" } });

    public static readonly Gauge GroupTreasuryZScore = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_zscore",
            "Z-score of treasury change vs historical",
            new GaugeConfiguration { LabelNames = new[] { "group_address" } });

    // ===========================================
    // Whale Transfer Tracking Metrics
    // ===========================================

    public static readonly Counter WhaleTransferTotal = Prometheus.Metrics
        .CreateCounter("circles_whale_transfer_total",
            "Count of whale transfers (in/out of Balancer vault)",
            new CounterConfiguration { LabelNames = new[] { "token_address", "direction" } });

    public static readonly Counter WhaleTransferVolume = Prometheus.Metrics
        .CreateCounter("circles_whale_transfer_volume",
            "Volume of whale transfers (in wei)",
            new CounterConfiguration { LabelNames = new[] { "token_address", "direction" } });

    public static readonly Gauge WhaleTransferLast = Prometheus.Metrics
        .CreateGauge("circles_whale_transfer_last",
            "Amount of most recent whale transfer (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "from", "to", "direction" } });

    public static readonly Gauge WhaleTransferTimestamp = Prometheus.Metrics
        .CreateGauge("circles_whale_transfer_timestamp",
            "Unix timestamp of last whale transfer",
            new GaugeConfiguration { LabelNames = new[] { "token_address" } });

    // ===========================================
    // Arbitrage Bot Activity Metrics
    // ===========================================

    public static readonly Counter ArbbotQuotesTotal = Prometheus.Metrics
        .CreateCounter("circles_arbbot_quotes_total",
            "Total quotes fetched from Balancer",
            new CounterConfiguration { LabelNames = new[] { "status" } });

    public static readonly Gauge ArbbotQuotesRate1m = Prometheus.Metrics
        .CreateGauge("circles_arbbot_quotes_rate_1m",
            "Quotes per minute (rolling average)");

    public static readonly Counter ArbbotOpportunitiesFound = Prometheus.Metrics
        .CreateCounter("circles_arbbot_opportunities_found_total",
            "Arbitrage opportunities identified");

    public static readonly Histogram ArbbotProfitEstimated = Prometheus.Metrics
        .CreateHistogram("circles_arbbot_profit_estimated",
            "Distribution of estimated profits (in wei)",
            new HistogramConfiguration
            {
                Buckets = new[] { 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18 }
            });

    public static readonly Gauge ArbbotLiquidityEstimate = Prometheus.Metrics
        .CreateGauge("circles_arbbot_liquidity_estimate",
            "Latest pathfinder liquidity estimate (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "source_avatar", "target_avatar" } });

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter LiquidityCollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_liquidity_collection_duration_seconds_total",
            "Total time spent collecting liquidity metrics");

    public static readonly Counter LiquidityCollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_liquidity_collection_errors_total",
            "Total number of liquidity collection errors",
            new CounterConfiguration { LabelNames = new[] { "metric" } });

    public static readonly Gauge LiquidityLastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_liquidity_last_collection_timestamp",
            "Unix timestamp of last successful liquidity collection");
}

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
    // Aggregate TVL Metrics (for alerting)
    // ===========================================

    public static readonly Gauge BalancerTvlTotal = Prometheus.Metrics
        .CreateGauge("circles_balancer_tvl_total",
            "Total value locked in Balancer vaults (in CRC, 18 decimals converted)");

    public static readonly Gauge BalancerTvlChange1h = Prometheus.Metrics
        .CreateGauge("circles_balancer_tvl_change_1h",
            "Absolute change in Balancer TVL over last hour (in CRC)");

    public static readonly Gauge BalancerTvlChangePct1h = Prometheus.Metrics
        .CreateGauge("circles_balancer_tvl_change_pct_1h",
            "Percentage change in Balancer TVL over last hour");

    public static readonly Gauge GroupTreasuryTvlTotal = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_tvl_total",
            "Total value locked across all group treasuries (in CRC)");

    public static readonly Gauge GroupTreasuryTvlChange1h = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_tvl_change_1h",
            "Absolute change in total group treasury TVL over last hour (in CRC)");

    public static readonly Gauge GroupTreasuryTvlChangePct1h = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_tvl_change_pct_1h",
            "Percentage change in total group treasury TVL over last hour");

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
            new GaugeConfiguration { LabelNames = new[] { "token_address", "token_name" } });

    public static readonly Gauge BalancerVaultChange24h = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_change_24h",
            "Balance change in last 24 hours (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "token_name" } });

    public static readonly Gauge BalancerVaultZScore1h = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_zscore_1h",
            "Z-score of 1h change vs 30d historical standard deviation",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "token_name" } });

    public static readonly Gauge BalancerVaultAnomaly = Prometheus.Metrics
        .CreateGauge("circles_balancer_vault_anomaly",
            "1 if anomaly detected, 0 otherwise",
            new GaugeConfiguration { LabelNames = new[] { "token_address", "token_name", "severity" } });

    public static readonly Counter BalancerVaultDrainEvents = Prometheus.Metrics
        .CreateCounter("circles_balancer_vault_drain_events_total",
            "Cumulative count of drain anomalies detected",
            new CounterConfiguration { LabelNames = new[] { "token_address", "token_name", "severity" } });

    public static readonly Gauge GroupTreasuryChange24h = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_change_24h",
            "Treasury change in last 24 hours (in wei)",
            new GaugeConfiguration { LabelNames = new[] { "group_address" } });

    public static readonly Gauge GroupTreasuryZScore = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_zscore",
            "Z-score of treasury change vs historical",
            new GaugeConfiguration { LabelNames = new[] { "group_address", "group_name" } });

    public static readonly Gauge GroupTreasuryAnomaly = Prometheus.Metrics
        .CreateGauge("circles_group_treasury_anomaly",
            "1 if anomaly detected in group treasury, 0 otherwise",
            new GaugeConfiguration { LabelNames = new[] { "group_address", "group_name", "severity" } });

    public static readonly Counter GroupTreasuryDrainEvents = Prometheus.Metrics
        .CreateCounter("circles_group_treasury_drain_events_total",
            "Cumulative count of group treasury drain anomalies detected",
            new CounterConfiguration { LabelNames = new[] { "group_address", "group_name", "severity" } });

    // ===========================================
    // Sybil Attack Detection Metrics
    // ===========================================

    public static readonly Gauge SybilWithdrawalCount = Prometheus.Metrics
        .CreateGauge("circles_sybil_withdrawal_count_1h",
            "Number of withdrawals from vault in last hour",
            new GaugeConfiguration { LabelNames = new[] { "vault" } });

    public static readonly Gauge SybilUniqueRecipients = Prometheus.Metrics
        .CreateGauge("circles_sybil_unique_recipients_1h",
            "Number of unique withdrawal recipients in last hour",
            new GaugeConfiguration { LabelNames = new[] { "vault" } });

    public static readonly Gauge SybilTotalWithdrawn = Prometheus.Metrics
        .CreateGauge("circles_sybil_total_withdrawn_1h",
            "Total amount withdrawn in last hour (in CRC)",
            new GaugeConfiguration { LabelNames = new[] { "vault" } });

    public static readonly Gauge SybilAvgWithdrawalSize = Prometheus.Metrics
        .CreateGauge("circles_sybil_avg_withdrawal_size_1h",
            "Average withdrawal size in last hour (in CRC)",
            new GaugeConfiguration { LabelNames = new[] { "vault" } });

    public static readonly Gauge SybilAnomaly = Prometheus.Metrics
        .CreateGauge("circles_sybil_anomaly",
            "1 if sybil-style attack pattern detected, 0 otherwise",
            new GaugeConfiguration { LabelNames = new[] { "vault", "severity" } });

    public static readonly Counter SybilAlertEvents = Prometheus.Metrics
        .CreateCounter("circles_sybil_alert_events_total",
            "Cumulative count of sybil attack pattern alerts",
            new CounterConfiguration { LabelNames = new[] { "vault", "severity" } });

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

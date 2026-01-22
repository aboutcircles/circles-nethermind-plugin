using Prometheus;

namespace Circles.Metrics.Exporter;

/// <summary>
/// Prometheus metrics for Circles trust score monitoring.
/// These metrics are derived from the trust_scores_current table in the analytics database.
/// </summary>
public static class TrustMetrics
{
    // ===========================================
    // Score Distribution Metrics
    // ===========================================

    public static readonly Gauge ScoreAvg = Prometheus.Metrics
        .CreateGauge("circles_trust_score_avg",
            "Average trust score across all avatars (0-100)");

    public static readonly Gauge ScoreMedian = Prometheus.Metrics
        .CreateGauge("circles_trust_score_median",
            "Median trust score (50th percentile)");

    public static readonly Gauge ScoreStdDev = Prometheus.Metrics
        .CreateGauge("circles_trust_score_stddev",
            "Standard deviation of trust scores (lower = more uniform distribution)");

    public static readonly Gauge ScorePercentile = Prometheus.Metrics
        .CreateGauge("circles_trust_score_percentile",
            "Trust score at various percentiles",
            new GaugeConfiguration { LabelNames = new[] { "percentile" } });

    public static readonly Gauge ScoreMin = Prometheus.Metrics
        .CreateGauge("circles_trust_score_min",
            "Minimum trust score");

    public static readonly Gauge ScoreMax = Prometheus.Metrics
        .CreateGauge("circles_trust_score_max",
            "Maximum trust score");

    // ===========================================
    // Trust Level Distribution
    // ===========================================

    public static readonly Gauge LevelCount = Prometheus.Metrics
        .CreateGauge("circles_trust_level_count",
            "Number of accounts at each trust level",
            new GaugeConfiguration { LabelNames = new[] { "level" } });

    public static readonly Gauge LevelPercentage = Prometheus.Metrics
        .CreateGauge("circles_trust_level_percentage",
            "Percentage of accounts at each trust level (0-100)",
            new GaugeConfiguration { LabelNames = new[] { "level" } });

    // ===========================================
    // Confidence Metrics
    // ===========================================

    public static readonly Gauge ConfidenceAvg = Prometheus.Metrics
        .CreateGauge("circles_trust_confidence_avg",
            "Average confidence level across all trust scores (0-1)");

    public static readonly Gauge ConfidenceMedian = Prometheus.Metrics
        .CreateGauge("circles_trust_confidence_median",
            "Median confidence level (0-1)");

    public static readonly Gauge LowConfidenceCount = Prometheus.Metrics
        .CreateGauge("circles_trust_low_confidence_count",
            "Number of accounts with low confidence scores (<0.5)");

    // ===========================================
    // Network Trust Health
    // ===========================================

    public static readonly Gauge TrustVelocity = Prometheus.Metrics
        .CreateGauge("circles_trust_velocity",
            "New trust relations created in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustChurn = Prometheus.Metrics
        .CreateGauge("circles_trust_churn",
            "Trust relations revoked in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustNetChange = Prometheus.Metrics
        .CreateGauge("circles_trust_net_change",
            "Net trust change (velocity - churn) in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustReciprocityRate = Prometheus.Metrics
        .CreateGauge("circles_trust_reciprocity_rate",
            "Percentage of trust relations that are mutual (0-100)");

    public static readonly Gauge TrustGraphDensity = Prometheus.Metrics
        .CreateGauge("circles_trust_graph_density",
            "Trust graph density (active trusts / possible trusts, 0-1)");

    public static readonly Gauge AvgOutDegree = Prometheus.Metrics
        .CreateGauge("circles_trust_avg_out_degree",
            "Average number of trusts given per avatar");

    public static readonly Gauge AvgInDegree = Prometheus.Metrics
        .CreateGauge("circles_trust_avg_in_degree",
            "Average number of trusts received per avatar");

    // ===========================================
    // Anomaly Detection Metrics
    // ===========================================

    public static readonly Gauge ScoreDrops = Prometheus.Metrics
        .CreateGauge("circles_trust_score_drops",
            "Count of significant score drops (>20 pts) in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge ScoreSpikes = Prometheus.Metrics
        .CreateGauge("circles_trust_score_spikes",
            "Count of suspicious score increases (>30 pts) in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge LowTrustNewAccounts = Prometheus.Metrics
        .CreateGauge("circles_trust_low_trust_new_accounts",
            "New accounts scoring LOW or VERY_LOW in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PenalizedAccounts = Prometheus.Metrics
        .CreateGauge("circles_trust_penalized_accounts",
            "Number of accounts with penalty applied to their score");

    // ===========================================
    // Score Buckets (histogram-like distribution)
    // ===========================================

    public static readonly Gauge ScoreBucketCount = Prometheus.Metrics
        .CreateGauge("circles_trust_score_bucket_count",
            "Number of accounts in score bucket ranges",
            new GaugeConfiguration { LabelNames = new[] { "bucket" } });

    // ===========================================
    // Economic-Trust Correlation
    // ===========================================

    public static readonly Gauge HighTrustVolume = Prometheus.Metrics
        .CreateGauge("circles_trust_high_trust_volume",
            "Transfer volume by HIGH+ trust accounts in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge LowTrustVolume = Prometheus.Metrics
        .CreateGauge("circles_trust_low_trust_volume",
            "Transfer volume by LOW/VERY_LOW trust accounts in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustWeightedVolume = Prometheus.Metrics
        .CreateGauge("circles_trust_weighted_volume",
            "Transfer volume weighted by trust scores in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge HighTrustVolumeShare = Prometheus.Metrics
        .CreateGauge("circles_trust_high_trust_volume_share",
            "Percentage of transfer volume from HIGH+ trust accounts (0-100)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Totals
    // ===========================================

    public static readonly Gauge TotalScoredAccounts = Prometheus.Metrics
        .CreateGauge("circles_trust_total_scored_accounts",
            "Total number of accounts with trust scores");

    public static readonly Gauge ScoredAccountsWithHistory = Prometheus.Metrics
        .CreateGauge("circles_trust_accounts_with_history",
            "Number of accounts with historical score data");

    public static readonly Gauge LastScoreComputeTime = Prometheus.Metrics
        .CreateGauge("circles_trust_last_compute_timestamp",
            "Unix timestamp of most recent trust score computation");

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter CollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_trust_collection_duration_seconds_total",
            "Total time spent collecting trust metrics");

    public static readonly Counter CollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_trust_collection_errors_total",
            "Total number of trust metric collection errors",
            new CounterConfiguration { LabelNames = new[] { "metric" } });

    public static readonly Gauge LastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_trust_last_collection_timestamp",
            "Unix timestamp of last successful trust metrics collection");
}

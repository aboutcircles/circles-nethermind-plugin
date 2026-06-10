using Prometheus;

namespace Circles.Metrics.Exporter;

public static class RepScoreMetrics
{
    // ===========================================
    // Blacklist / Bad Actor Radar
    // ===========================================

    public static readonly Gauge BlacklistTotal = Prometheus.Metrics
        .CreateGauge("circles_blacklist_total",
            "Total number of addresses in the TrustGard blacklist");

    public static readonly Gauge BlacklistMembersTotal = Prometheus.Metrics
        .CreateGauge("circles_blacklist_members_total",
            "Blacklisted addresses that are currently ScoreGroup members");

    public static readonly Gauge BlacklistMembersNonzeroScore = Prometheus.Metrics
        .CreateGauge("circles_blacklist_members_nonzero_score",
            "Blacklisted ScoreGroup members whose rep score is still > 0 — alert if > 0");

    public static readonly Gauge BlacklistAdditions24h = Prometheus.Metrics
        .CreateGauge("circles_blacklist_additions_24h",
            "Addresses added to the blacklist in the last 24 hours (TrustGard spike detector)");

    public static readonly Gauge BlacklistRemovals24h = Prometheus.Metrics
        .CreateGauge("circles_blacklist_removals_24h",
            "Addresses removed from the blacklist in the last 24 hours (false-positive corrections)");

    public static readonly Gauge BlacklistLastRefreshAgeSeconds = Prometheus.Metrics
        .CreateGauge("circles_blacklist_last_refresh_age_seconds",
            "Seconds since the last successful TrustGard blacklist refresh — alert if > 7200");

    // ===========================================
    // Rep Score Distribution (ScoreGroup members)
    // ===========================================

    public static readonly Gauge MemberCount = Prometheus.Metrics
        .CreateGauge("circles_rep_score_member_count",
            "Total number of avatars scored in the ScoreGroup");

    public static readonly Gauge ScoreAvg = Prometheus.Metrics
        .CreateGauge("circles_rep_score_avg",
            "Average reputation score across all ScoreGroup members (0-100)");

    public static readonly Gauge ScoreMedian = Prometheus.Metrics
        .CreateGauge("circles_rep_score_median",
            "Median reputation score (50th percentile, 0-100)");

    public static readonly Gauge ScoreStdDev = Prometheus.Metrics
        .CreateGauge("circles_rep_score_stddev",
            "Standard deviation of reputation scores — very low = gaming, very high = polarisation");

    public static readonly Gauge ScorePercentile = Prometheus.Metrics
        .CreateGauge("circles_rep_score_percentile",
            "Reputation score at key percentiles (0-100)",
            new GaugeConfiguration { LabelNames = ["percentile"] });

    public static readonly Gauge ScoreBucketCount = Prometheus.Metrics
        .CreateGauge("circles_rep_score_bucket_count",
            "Number of members in each 10-point score bucket",
            new GaugeConfiguration { LabelNames = ["bucket"] });

    public static readonly Gauge ZeroScoreCount = Prometheus.Metrics
        .CreateGauge("circles_rep_score_zero_count",
            "Members with score = 0 (blacklisted or gate-failed)");

    public static readonly Gauge HighScoreShare = Prometheus.Metrics
        .CreateGauge("circles_rep_score_high_share",
            "Fraction of members with score >= high threshold (0-1) — good citizen ratio");

    public static readonly Gauge LastRefreshAgeSeconds = Prometheus.Metrics
        .CreateGauge("circles_rep_score_last_refresh_age_seconds",
            "Seconds since the last AA rep_score refresh for this group — alert if > 600");

    // ===========================================
    // Anomaly Detection
    // ===========================================

    public static readonly Gauge ScoreDrops24h = Prometheus.Metrics
        .CreateGauge("circles_rep_score_drops_24h",
            "Members with a score drop >= threshold in the last 24 hours (organised attack signal)");

    public static readonly Gauge NewZeroScore24h = Prometheus.Metrics
        .CreateGauge("circles_rep_score_new_zero_24h",
            "Members whose score hit 0 in the last 24 hours (new blacklisting or gate failures) — alert if > 5");

    public static readonly Gauge NewMembers24h = Prometheus.Metrics
        .CreateGauge("circles_rep_score_new_members_24h",
            "Members admitted to the ScoreGroup in the last 24 hours");

    public static readonly Gauge LostMembers24h = Prometheus.Metrics
        .CreateGauge("circles_rep_score_lost_members_24h",
            "Members removed from the ScoreGroup in the last 24 hours");

    // ===========================================
    // Cross-cutting: Rep Score vs Transfer Volume
    // ===========================================

    public static readonly Gauge HighRepTransferShare = Prometheus.Metrics
        .CreateGauge("circles_rep_score_high_rep_transfer_share",
            "Fraction of CRC transfer volume sent by high-reputation members (score >= threshold, 0-1)",
            new GaugeConfiguration { LabelNames = ["window"] });

    public static readonly Gauge ZeroRepTransferShare = Prometheus.Metrics
        .CreateGauge("circles_rep_score_zero_rep_transfer_share",
            "Fraction of CRC transfer volume sent by zero-score actors — bad actor economic footprint (0-1)",
            new GaugeConfiguration { LabelNames = ["window"] });

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter CollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_rep_score_collection_duration_seconds_total",
            "Total time spent collecting rep score metrics");

    public static readonly Counter CollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_rep_score_collection_errors_total",
            "Total number of rep score metric collection errors",
            new CounterConfiguration { LabelNames = ["metric"] });

    public static readonly Gauge LastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_rep_score_last_collection_timestamp",
            "Unix timestamp of last successful rep score metrics collection");
}

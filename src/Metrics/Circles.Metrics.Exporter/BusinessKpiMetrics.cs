using Prometheus;

namespace Circles.Metrics.Exporter;

/// <summary>
/// Prometheus metrics for Circles business KPIs.
/// These metrics are derived from PostgreSQL queries run periodically.
/// </summary>
public static class BusinessKpiMetrics
{
    // ===========================================
    // User Metrics
    // ===========================================

    public static readonly Gauge TotalHumans = Prometheus.Metrics
        .CreateGauge("circles_total_humans",
            "Total number of registered humans",
            new GaugeConfiguration { LabelNames = new[] { "version" } });

    public static readonly Gauge TotalOrganizations = Prometheus.Metrics
        .CreateGauge("circles_total_organizations",
            "Total number of registered organizations");

    public static readonly Gauge TotalGroups = Prometheus.Metrics
        .CreateGauge("circles_total_groups",
            "Total number of registered groups");

    public static readonly Gauge NewUsers = Prometheus.Metrics
        .CreateGauge("circles_new_users",
            "Number of new users in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Trust Network Metrics
    // ===========================================

    public static readonly Gauge ActiveTrusts = Prometheus.Metrics
        .CreateGauge("circles_active_trusts",
            "Total number of active trust relationships");

    public static readonly Gauge NewTrusts = Prometheus.Metrics
        .CreateGauge("circles_new_trusts",
            "Number of new trusts in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustChanges = Prometheus.Metrics
        .CreateGauge("circles_trust_changes",
            "Trust relationship changes in time window",
            new GaugeConfiguration { LabelNames = new[] { "window", "type" } });

    // ===========================================
    // Economic Metrics
    // ===========================================

    public static readonly Gauge TotalBackers = Prometheus.Metrics
        .CreateGauge("circles_total_backers",
            "Total number of LBP backers");

    public static readonly Gauge ActiveMinters = Prometheus.Metrics
        .CreateGauge("circles_active_minters",
            "Number of active minters in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge DailyMintVolume = Prometheus.Metrics
        .CreateGauge("circles_daily_mint_volume_crc",
            "CRC minted in the last 24 hours");

    public static readonly Gauge DailyTransferVolume = Prometheus.Metrics
        .CreateGauge("circles_daily_transfer_volume_crc",
            "CRC transferred in the last 24 hours");

    public static readonly Gauge DailyTransferCount = Prometheus.Metrics
        .CreateGauge("circles_daily_transfer_count",
            "Number of transfers in the last 24 hours");

    public static readonly Gauge UniqueTransactingAddresses = Prometheus.Metrics
        .CreateGauge("circles_unique_transacting_addresses",
            "Unique addresses transacting in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Group Metrics
    // ===========================================

    public static readonly Gauge GroupMembersTotal = Prometheus.Metrics
        .CreateGauge("circles_group_members_total",
            "Total number of group memberships");

    public static readonly Gauge GroupMintVolume = Prometheus.Metrics
        .CreateGauge("circles_group_mint_volume",
            "Group mint volume in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Profiles Metrics
    // ===========================================

    public static readonly Gauge ProfilesCreated = Prometheus.Metrics
        .CreateGauge("circles_profiles_created",
            "Number of profiles created",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter CollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_kpi_collection_duration_seconds_total",
            "Total time spent collecting KPIs");

    public static readonly Counter CollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_kpi_collection_errors_total",
            "Total number of KPI collection errors",
            new CounterConfiguration { LabelNames = new[] { "metric" } });

    public static readonly Gauge LastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_kpi_last_collection_timestamp",
            "Unix timestamp of last successful KPI collection");
}

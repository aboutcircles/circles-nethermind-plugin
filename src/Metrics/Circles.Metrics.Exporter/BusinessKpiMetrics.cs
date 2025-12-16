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
    // NEW: Dune Parity KPIs
    // ===========================================

    public static readonly Gauge DailyMintCount = Prometheus.Metrics
        .CreateGauge("circles_daily_mint_count",
            "Number of mint events in the last 24 hours");

    public static readonly Gauge NewBackers = Prometheus.Metrics
        .CreateGauge("circles_new_backers",
            "Number of new backers in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintingFraction14d = Prometheus.Metrics
        .CreateGauge("circles_minting_fraction_14d",
            "Fraction of registered humans who minted in the last 14 days (0-1)");

    public static readonly Gauge NewOrganizations = Prometheus.Metrics
        .CreateGauge("circles_new_organizations",
            "Number of new organizations in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge NewGroups = Prometheus.Metrics
        .CreateGauge("circles_new_groups",
            "Number of new groups in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Activity Rates (Minters/Spenders by window)
    // ===========================================

    public static readonly Gauge MintingRate = Prometheus.Metrics
        .CreateGauge("circles_minting_rate",
            "Fraction of registered humans who minted in time window (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge SpendingRate = Prometheus.Metrics
        .CreateGauge("circles_spending_rate",
            "Fraction of registered humans who sent transfers in time window (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TransferVolume = Prometheus.Metrics
        .CreateGauge("circles_transfer_volume_crc",
            "CRC transferred in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintVolume = Prometheus.Metrics
        .CreateGauge("circles_mint_volume_crc",
            "CRC minted in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Sybil Detection Metrics
    // ===========================================

    public static readonly Gauge AccountsWithoutProfile = Prometheus.Metrics
        .CreateGauge("circles_accounts_no_profile",
            "Number of registered humans without a profile");

    public static readonly Gauge AccountsWithoutIncomingTrust = Prometheus.Metrics
        .CreateGauge("circles_accounts_no_trust_received",
            "Number of registered humans not trusted by anyone else");

    public static readonly Gauge BatchRegistrations = Prometheus.Metrics
        .CreateGauge("circles_batch_registrations",
            "Accounts registered in batches (same block) in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintAndDrainAccounts = Prometheus.Metrics
        .CreateGauge("circles_mint_and_drain_accounts",
            "Accounts that minted but have zero balance",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge HighVolumeInviters = Prometheus.Metrics
        .CreateGauge("circles_high_volume_inviters",
            "Number of inviters with unusually high invitee counts",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge SuspiciousAccounts = Prometheus.Metrics
        .CreateGauge("circles_suspicious_accounts",
            "Accounts matching multiple sybil indicators (no profile, no trust, but minting)");

    public static readonly Gauge OrganicAccounts = Prometheus.Metrics
        .CreateGauge("circles_organic_accounts",
            "Accounts with profile and incoming trust (healthy accounts)");

    // ===========================================
    // Network Health Metrics
    // ===========================================

    public static readonly Gauge AverageTrustConnections = Prometheus.Metrics
        .CreateGauge("circles_average_trust_connections",
            "Average number of trust connections per registered human");

    public static readonly Gauge IsolatedAccounts = Prometheus.Metrics
        .CreateGauge("circles_isolated_accounts",
            "Accounts with zero trust connections in either direction");

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

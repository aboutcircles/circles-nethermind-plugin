using Prometheus;

namespace Circles.Metrics.Exporter;

/// <summary>
/// Prometheus metrics for Circles deployment status monitoring across multiple environments.
/// Data collected by probing each environment's RPC endpoint (circles_tables + circles_query).
/// No direct database access required.
/// </summary>
public static class DeploymentMetrics
{
    // ===========================================
    // Table Existence (per environment)
    // ===========================================

    public static readonly Gauge TableExists = Prometheus.Metrics
        .CreateGauge("circles_table_exists",
            "Whether an expected indexer table exists and has data (1=yes, 0=no)",
            new GaugeConfiguration { LabelNames = new[] { "environment", "namespace", "table" } });

    // ===========================================
    // Summary (per environment)
    // ===========================================

    public static readonly Gauge ExpectedTableCount = Prometheus.Metrics
        .CreateGauge("circles_deployment_expected_tables",
            "Total number of expected indexer tables");

    public static readonly Gauge ExistingTableCount = Prometheus.Metrics
        .CreateGauge("circles_deployment_existing_tables",
            "Number of expected tables that exist and have data",
            new GaugeConfiguration { LabelNames = new[] { "environment" } });

    public static readonly Gauge MissingTableCount = Prometheus.Metrics
        .CreateGauge("circles_deployment_missing_tables",
            "Number of expected tables missing or empty",
            new GaugeConfiguration { LabelNames = new[] { "environment" } });

    public static readonly Gauge SchemaTableCount = Prometheus.Metrics
        .CreateGauge("circles_deployment_schema_tables",
            "Number of tables the environment code knows about (from circles_tables)",
            new GaugeConfiguration { LabelNames = new[] { "environment" } });

    public static readonly Gauge EnvironmentReachable = Prometheus.Metrics
        .CreateGauge("circles_deployment_environment_reachable",
            "Whether the environment RPC is reachable (1=yes, 0=no)",
            new GaugeConfiguration { LabelNames = new[] { "environment" } });

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter CollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_deployment_collection_duration_seconds_total",
            "Total time spent collecting deployment metrics");

    public static readonly Counter CollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_deployment_collection_errors_total",
            "Total number of deployment metric collection errors",
            new CounterConfiguration { LabelNames = new[] { "environment", "metric" } });

    public static readonly Gauge LastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_deployment_last_collection_timestamp",
            "Unix timestamp of last successful deployment metrics collection",
            new GaugeConfiguration { LabelNames = new[] { "environment" } });
}

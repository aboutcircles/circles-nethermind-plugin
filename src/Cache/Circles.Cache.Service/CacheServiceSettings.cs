namespace Circles.Cache.Service;

/// <summary>
/// Configuration settings for the Circles Cache Service.
/// Reads from environment variables and provides defaults.
/// </summary>
public class CacheServiceSettings
{
    /// <summary>
    /// PostgreSQL connection string with write access (for pg_notify operations).
    /// Environment variable: POSTGRES_CONNECTION_STRING
    /// </summary>
    public string PostgresConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// PostgreSQL read-only connection string for queries.
    /// Environment variable: POSTGRES_READONLY_CONNECTION_STRING
    /// Falls back to PostgresConnectionString if not set.
    /// </summary>
    public string PostgresReadonlyConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// PostgreSQL NOTIFY channel name for block import notifications.
    /// Environment variable: CIRCLES_PG_NOTIFY_CHANNEL
    /// Default: "circles_index_events"
    /// </summary>
    public string PgNotifyChannel { get; init; } = "circles_index_events";

    /// <summary>
    /// Number of blocks to retain in rollback history for each cache.
    /// Environment variable: ROLLBACK_CAPACITY
    /// Default: 12 blocks
    /// </summary>
    public int RollbackCapacity { get; init; } = 12;

    /// <summary>
    /// Maximum lag (in blocks) allowed before the service is considered "not ready".
    /// Environment variable: MAX_CATCHUP_LAG
    /// Default: 10 blocks
    /// </summary>
    public int MaxCatchupLag { get; init; } = 10;

    /// <summary>
    /// HTTP port for the service (health checks and API endpoints).
    /// Environment variable: PORT
    /// Default: 5002
    /// </summary>
    public int Port { get; init; } = 5002;

    /// <summary>
    /// Gets the effective readonly connection string (falls back to main connection if not set).
    /// </summary>
    public string EffectiveReadonlyConnectionString =>
        string.IsNullOrWhiteSpace(PostgresReadonlyConnectionString)
            ? PostgresConnectionString
            : PostgresReadonlyConnectionString;

    /// <summary>
    /// Validates that required settings are configured.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            throw new InvalidOperationException(
                "POSTGRES_CONNECTION_STRING environment variable is required");
        }

        if (RollbackCapacity < 1 || RollbackCapacity > 1000)
        {
            throw new InvalidOperationException(
                $"ROLLBACK_CAPACITY must be between 1 and 1000 (got {RollbackCapacity})");
        }

        if (MaxCatchupLag < 0)
        {
            throw new InvalidOperationException(
                $"MAX_CATCHUP_LAG must be non-negative (got {MaxCatchupLag})");
        }
    }

    /// <summary>
    /// Creates settings from environment variables.
    /// </summary>
    public static CacheServiceSettings FromEnvironment()
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString =
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? string.Empty,
            PostgresReadonlyConnectionString =
                Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING") ??
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? string.Empty,
            PgNotifyChannel =
                Environment.GetEnvironmentVariable("CIRCLES_PG_NOTIFY_CHANNEL") ?? "circles_index_events",
            RollbackCapacity =
                int.TryParse(Environment.GetEnvironmentVariable("ROLLBACK_CAPACITY"), out var capacity)
                    ? capacity
                    : 12,
            MaxCatchupLag =
                int.TryParse(Environment.GetEnvironmentVariable("MAX_CATCHUP_LAG"), out var lag)
                    ? lag
                    : 10,
            Port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) ? port : 5002
        };

        settings.Validate();
        return settings;
    }
}

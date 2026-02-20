namespace Circles.Rpc.Host;

/// <summary>
/// Settings for the Circles.Rpc.Host service.
/// </summary>
public class Settings : Circles.Common.Settings
{
    public new readonly string NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? "http://localhost:8545";

    public readonly string BalanceMode =
        Environment.GetEnvironmentVariable("BALANCE_MODE")
        ?? "live"; // "database" or "live"

    public readonly string? CacheServiceUrl =
        Environment.GetEnvironmentVariable("CACHE_SERVICE_URL");

    /// <summary>
    /// Feature flag to enable/disable Cache Service usage (default: false)
    /// Set to "true" to use Cache Service for balance/avatar queries
    /// Set to "false" to use traditional DB + Nethermind queries
    /// </summary>
    public readonly bool UseCacheService =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CACHE_SERVICE_URL"))
        && (Environment.GetEnvironmentVariable("USE_CACHE_SERVICE")?.ToLowerInvariant() == "true");

    /// <summary>
    /// URL for the profile pinning service (fast profile search proxy).
    /// When set, SearchProfiles will proxy to this service before falling back to SQL.
    /// </summary>
    public readonly string? ProfilePinningServiceUrl =
        Environment.GetEnvironmentVariable("PROFILE_PINNING_SERVICE_URL");

    #region RPC-specific database timeout configuration

    /// <summary>
    /// Timeout in seconds for general database queries (default: 30)
    /// </summary>
    public readonly int DatabaseQueryTimeoutSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("DATABASE_QUERY_TIMEOUT_SECONDS"), out var queryTimeout)
            ? queryTimeout
            : 30;

    /// <summary>
    /// Timeout in seconds for profile search queries (default: 30)
    /// </summary>
    public readonly int ProfileSearchTimeoutSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("PROFILE_SEARCH_TIMEOUT_SECONDS"), out var profileSearchTimeout)
            ? profileSearchTimeout
            : 30;

    #endregion

    #region Health check configuration

    /// <summary>
    /// Maximum allowed lag in blocks for the indexer sync health check (default: 100).
    /// If the indexer is more than this many blocks behind the chain head, the /ready endpoint will return 503.
    /// Set to a higher value during initial sync, or 0 to disable the check.
    /// </summary>
    public readonly long IndexerMaxLagBlocks =
        long.TryParse(Environment.GetEnvironmentVariable("INDEXER_MAX_LAG_BLOCKS"), out var maxLag)
            ? maxLag
            : 100;

    #endregion
}
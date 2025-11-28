namespace Circles.Rpc.Host;

/// <summary>
/// Settings for the Circles.Rpc.Host service.
/// </summary>
public class Settings : Circles.Index.Common.Settings
{
    public new readonly string NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? "http://localhost:8545";

    public readonly string BalanceMode =
        Environment.GetEnvironmentVariable("BALANCE_MODE")
        ?? "live"; // "database" or "live"

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
}
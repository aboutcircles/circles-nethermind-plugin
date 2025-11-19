namespace Circles.Pathfinder;

/// <summary>
/// Settings for the Circles Pathfinder library.
/// These settings are typically provided by the host application.
/// </summary>
public class Settings
{
    /// <summary>
    /// Timeout in seconds for complex balance queries (default: 300)
    /// </summary>
    public int PathfinderBalanceTimeoutSeconds { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_BALANCE_TIMEOUT_SECONDS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("PATHFINDER_BALANCE_TIMEOUT_SECONDS")!)
        : 300;

    /// <summary>
    /// Timeout in seconds for trust queries (default: 120)
    /// </summary>
    public int PathfinderTrustTimeoutSeconds { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_TRUST_TIMEOUT_SECONDS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("PATHFINDER_TRUST_TIMEOUT_SECONDS")!)
        : 120;

    /// <summary>
    /// Timeout in seconds for group queries (default: 60)
    /// </summary>
    public int PathfinderGroupTimeoutSeconds { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_GROUP_TIMEOUT_SECONDS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("PATHFINDER_GROUP_TIMEOUT_SECONDS")!)
        : 60;
}
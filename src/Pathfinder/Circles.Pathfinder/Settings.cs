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

    /// <summary>
    /// Target timestamp for demurrage calculations (default: null = use current time).
    /// When set, demurrage is calculated relative to this timestamp instead of NOW.
    /// Used for testing against frozen blocks where the block timestamp differs from current time.
    /// </summary>
    public DateTimeOffset? TargetDemurrageTimestamp { get; set; }

    /// <summary>
    /// Safety margin factor applied to demurraged balances to account for the delay
    /// between graph-build time and on-chain execution. Default 0.9995 (0.05% haircut).
    /// Set to 1.0 to disable. Only applies when TargetDemurrageTimestamp is null (live mode).
    /// </summary>
    public double DemurrageSafetyMargin { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN") != null
        ? double.Parse(Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN")!)
        : 0.9995;

    /// <summary>
    /// Timeout in seconds for the MaxFlow solver. Default 30s.
    /// Prevents a stuck solver from blocking a semaphore slot indefinitely.
    /// </summary>
    public int SolverTimeoutSeconds { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_SOLVER_TIMEOUT_SECONDS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("PATHFINDER_SOLVER_TIMEOUT_SECONDS")!)
        : 30;

    /// <summary>
    /// When true, consented avatars are excluded from intermediary positions in flows.
    /// They can still be source or sink. This avoids consent rule validation entirely
    /// for the risky multi-hop case. Default: true (conservative).
    /// When false, the existing consent validation logic runs (PathHasConsentViolation
    /// + ValidateConsentedFlow safety net).
    /// </summary>
    public bool DisableConsentedFlow { get; set; } =
        !string.Equals(
            Environment.GetEnvironmentVariable("PATHFINDER_DISABLE_CONSENTED_FLOW"),
            "false",
            StringComparison.OrdinalIgnoreCase);
}
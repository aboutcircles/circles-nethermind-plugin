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
        : 10;

    /// <summary>
    /// Master switch for incremental graph updates.
    /// When false, the original full-refresh-every-block behavior is used — all new code bypassed.
    /// When true, in-memory state is maintained and only deltas are loaded per block.
    /// </summary>
    public bool IncrementalEnabled { get; set; } =
        !string.Equals(
            Environment.GetEnvironmentVariable("PATHFINDER_INCREMENTAL_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Number of blocks between full DB refreshes (drift correction).
    /// Only used when IncrementalEnabled=true.
    /// Set to 1 to force full refresh every block (effectively disables incremental within the new code path).
    /// Default: 200 (~16 min on Gnosis with 5s blocks).
    /// </summary>
    public int FullRefreshIntervalBlocks { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_FULL_REFRESH_INTERVAL_BLOCKS") != null
        ? int.Parse(Environment.GetEnvironmentVariable("PATHFINDER_FULL_REFRESH_INTERVAL_BLOCKS")!)
        : 200;

    /// <summary>
    /// When true, consented avatars are excluded from intermediary positions in flows.
    /// They can still be source or sink. This avoids consent rule validation entirely
    /// for the risky multi-hop case. Default: true (conservative).
    /// When false, the existing consent validation logic runs (PathHasConsentViolation
    /// + ValidateConsentedFlow safety net).
    /// Reads PATHFINDER_EXCLUDE_CONSENTED_INTERMEDIARIES first, falls back to
    /// PATHFINDER_DISABLE_CONSENTED_FLOW for backward compatibility.
    /// </summary>
    public bool ExcludeConsentedIntermediaries { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_EXCLUDE_CONSENTED_INTERMEDIARIES") != null
            ? !string.Equals(
                Environment.GetEnvironmentVariable("PATHFINDER_EXCLUDE_CONSENTED_INTERMEDIARIES"),
                "false",
                StringComparison.OrdinalIgnoreCase)
            : !string.Equals(
                Environment.GetEnvironmentVariable("PATHFINDER_DISABLE_CONSENTED_FLOW"),
                "false",
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Backward-compatible alias for <see cref="ExcludeConsentedIntermediaries"/>.
    /// </summary>
    [Obsolete("Use ExcludeConsentedIntermediaries instead. Will be removed in a future release.")]
    public bool DisableConsentedFlow
    {
        get => ExcludeConsentedIntermediaries;
        set => ExcludeConsentedIntermediaries = value;
    }

    /// <summary>
    /// Address of the V2 base group router contract used to filter groups and group trusts.
    /// Previously hardcoded in SQL; now parameterized to avoid silent data loss if the router changes.
    /// Reads V2_BASE_GROUP_ROUTER env var (same as Common.Settings.BaseGroupRouter).
    /// </summary>
    public string GroupRouterAddress { get; set; } =
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")?.ToLowerInvariant()
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

}

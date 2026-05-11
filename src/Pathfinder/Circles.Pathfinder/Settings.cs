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
    /// between graph-build time and on-chain execution. Default 0.999999 (~7 min drift).
    /// Only relevant for transfers using 100% of available balance (e.g. refunds).
    /// The previous default of 0.9995 (~60h drift) blocked exact-amount transfers
    /// when the sender held precisely the required balance (e.g. organization refunds).
    /// Override via PATHFINDER_DEMURRAGE_SAFETY_MARGIN. Set to 1.0 to disable entirely.
    /// Only applies when TargetDemurrageTimestamp is null (live mode).
    /// </summary>
    public double DemurrageSafetyMargin { get; set; } =
        Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN") != null
        ? double.Parse(Environment.GetEnvironmentVariable("PATHFINDER_DEMURRAGE_SAFETY_MARGIN")!)
        : 0.999999;

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
    /// They can still be source or sink for direct (non-group) transfers, but NOT
    /// for group-mint routes — consented sender → Group is unconditionally excluded
    /// (even when the consented avatar is the source) because the post-Router
    /// edge Avatar(consented)→Router always reverts on-chain (Router has no
    /// advancedUsageFlags). This makes the flag broader than its name implies:
    /// it excludes consented sources from group-mint routes too. Default: true.
    ///
    /// When false, the path-level filter PathHasConsentViolation enforces full
    /// per-edge consent validation matching Hub.sol's isPermittedFlow rules.
    /// Validation mode is more permissive: it allows consented sources for direct
    /// transfers (with mutual consent + trust) and rejects only the specific edges
    /// Hub.sol would reject.
    ///
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
    /// Address of the V2 base group router contract. Used by GraphFactory to track
    /// the router node and filter group collateral edges (AddTokenPoolOutEdges router
    /// trust check). NOT used for SQL group filtering — see StandardMintPolicyAddress.
    /// Reads V2_BASE_GROUP_ROUTER env var (same as Common.Settings.BaseGroupRouter).
    /// </summary>
    public string GroupRouterAddress { get; set; } =
        Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")?.ToLowerInvariant()
        ?? "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    /// <summary>
    /// Address of the standard group mint policy contract. Used to filter which groups
    /// participate in pathfinding (only groups registered with this mint policy are included).
    /// Must match the Cache Service's StandardTreasuryMint constant.
    /// </summary>
    public string StandardMintPolicyAddress { get; set; } =
        Environment.GetEnvironmentVariable("V2_STANDARD_MINT_POLICY")?.ToLowerInvariant()
        ?? "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";

    /// <summary>
    /// Additional mint policies whose groups participate in pathfinding.
    /// Score groups are included only when an indexed
    /// CrcV2_ScoreGroup_GroupInitialized event provides their path mint router.
    /// </summary>
    public string[] ScoreGroupMintPolicies { get; set; } =
        Environment.GetEnvironmentVariable("V2_SCORE_GROUP_MINT_POLICIES")?.Split(',')
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray()
        ?? [];
}

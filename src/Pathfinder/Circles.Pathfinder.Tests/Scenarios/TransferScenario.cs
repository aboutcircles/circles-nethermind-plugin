using System.Text.Json.Serialization;
using Circles.Common.Dto;

namespace Circles.Pathfinder.Tests.Scenarios;

/// <summary>
/// Represents a transfer scenario for snapshot testing.
/// Scenarios are loaded from JSON files and used to drive data-driven tests.
///
/// Each scenario defines:
/// - A specific block number (frozen blockchain state)
/// - Source and sink addresses
/// - Expected outcomes (path found, min flow, contract execution result)
/// - Optional filters (fromTokens, toTokens, excludedTokens)
/// </summary>
public record TransferScenario
{
    /// <summary>
    /// Unique identifier for the scenario (e.g., "group-multi-collateral-001").
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name describing the scenario.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Category grouping (e.g., "group-minting", "direct-transfer", "self-conversion").
    /// </summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>
    /// Block number at which the test environment should be frozen.
    /// This ensures deterministic, reproducible test results.
    /// Scenarios without a block number (0) are incomplete and will be skipped.
    /// </summary>
    [JsonPropertyName("block")]
    public long Block { get; init; }

    /// <summary>
    /// Source address for the transfer (sender).
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>
    /// Sink address for the transfer (receiver).
    /// </summary>
    [JsonPropertyName("sink")]
    public required string Sink { get; init; }

    /// <summary>
    /// Detailed description of the scenario and what it tests.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    // ============================================================
    // Expected Outcomes
    // ============================================================

    /// <summary>
    /// Whether the pathfinder should find a valid path.
    /// Set to false for scenarios that should have no path.
    /// </summary>
    [JsonPropertyName("shouldFindPath")]
    public bool ShouldFindPath { get; init; } = true;

    /// <summary>
    /// Minimum expected flow in wei (as string for large numbers).
    /// If null, any positive flow is acceptable.
    /// </summary>
    [JsonPropertyName("minFlow")]
    public string? MinFlow { get; init; }

    /// <summary>
    /// If contract execution should revert, the expected revert reason.
    /// If null, execution should succeed.
    /// </summary>
    [JsonPropertyName("expectedRevertReason")]
    public string? ExpectedRevertReason { get; init; }

    /// <summary>
    /// Whether this scenario should be executed on Anvil for E2E validation.
    /// All transfer scenarios default to true for comprehensive testing.
    /// </summary>
    [JsonPropertyName("runOnAnvil")]
    public bool RunOnAnvil { get; init; } = true;

    // ============================================================
    // Flow Request Filters (optional)
    // ============================================================

    /// <summary>
    /// Restrict which tokens the source can use (token addresses).
    /// </summary>
    [JsonPropertyName("fromTokens")]
    public string[]? FromTokens { get; init; }

    /// <summary>
    /// Restrict which tokens the sink should receive.
    /// </summary>
    [JsonPropertyName("toTokens")]
    public string[]? ToTokens { get; init; }

    /// <summary>
    /// Tokens to exclude from path computation (legacy, maps to ExcludedFromTokens).
    /// </summary>
    [JsonPropertyName("excludedTokens")]
    public string[]? ExcludedTokens { get; init; }

    /// <summary>
    /// Tokens to exclude from source-side path computation.
    /// </summary>
    [JsonPropertyName("excludedFromTokens")]
    public string[]? ExcludedFromTokens { get; init; }

    /// <summary>
    /// Tokens to exclude from sink-side path computation.
    /// </summary>
    [JsonPropertyName("excludedToTokens")]
    public string[]? ExcludedToTokens { get; init; }

    /// <summary>
    /// Maximum number of transfer steps allowed.
    /// </summary>
    [JsonPropertyName("maxTransfers")]
    public int? MaxTransfers { get; init; }

    /// <summary>
    /// Whether to include wrapped tokens in path computation.
    /// </summary>
    [JsonPropertyName("withWrap")]
    public bool? WithWrap { get; init; }

    /// <summary>
    /// Addresses to simulate as having consented flow enabled.
    /// Useful for testing consented flow scenarios at blocks where
    /// the feature wasn't yet enabled or for specific avatars.
    /// </summary>
    [JsonPropertyName("simulatedConsentedAvatars")]
    public string[]? SimulatedConsentedAvatars { get; init; }

    // ============================================================
    // Simulation Features (Pathfinder-only, cannot execute on Anvil)
    // ============================================================

    /// <summary>
    /// Simulated token balances to inject into the capacity graph.
    /// These balances exist only in pathfinder memory, not on-chain.
    /// Scenarios with simulated balances should set runOnAnvil=false.
    /// </summary>
    [JsonPropertyName("simulatedBalances")]
    public SimulatedBalance[]? SimulatedBalances { get; init; }

    /// <summary>
    /// Simulated trust relationships to inject into the trust graph.
    /// These trusts exist only in pathfinder memory, not on-chain.
    /// Scenarios with simulated trusts should set runOnAnvil=false.
    /// </summary>
    [JsonPropertyName("simulatedTrusts")]
    public SimulatedTrust[]? SimulatedTrusts { get; init; }

    // ============================================================
    // Advanced Request Options
    // ============================================================

    /// <summary>
    /// When true, enforces 96 CRC quantization for sink-bound transfers.
    /// Used for invitation module scenarios.
    /// </summary>
    [JsonPropertyName("quantizedMode")]
    public bool? QuantizedMode { get; init; }

    /// <summary>
    /// When true, includes debug information showing all transformation stages.
    /// </summary>
    [JsonPropertyName("debugShowIntermediateSteps")]
    public bool? DebugShowIntermediateSteps { get; init; }

    // ============================================================
    // Metadata
    // ============================================================

    /// <summary>
    /// When this scenario was discovered/created (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("discoveredAt")]
    public string? DiscoveredAt { get; init; }

    /// <summary>
    /// Reference to the commit or method that fixed the issue (for regression tests).
    /// </summary>
    [JsonPropertyName("fixedIn")]
    public string? FixedIn { get; init; }

    /// <summary>
    /// Tags for filtering scenarios (e.g., ["regression", "edge-ordering"]).
    /// </summary>
    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }

    /// <summary>
    /// Free-text notes about the scenario (e.g., known issues, context).
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>
    /// Embedded subgraph data for offline unit tests.
    /// Populated by SubgraphPopulateFixtures utility.
    /// </summary>
    [JsonPropertyName("subgraph")]
    public object? Subgraph { get; init; }

    // ============================================================
    // ScoreGroup matrix extensions (used by ScoreGroupRegressionTests).
    // All optional — existing scenarios leave them null and behave unchanged.
    // ============================================================

    /// <summary>
    /// Real on-chain transaction hash to replay on the Anvil fork instead of building
    /// a path from the pathfinder. Used for ScoreGroup router/personal mints whose
    /// preconditions (valid Merkle proof, operator approval, snapshot atomicity) cannot
    /// be synthesized cold. The fork is taken at <see cref="Block"/> (set to txBlock-1).
    /// </summary>
    [JsonPropertyName("replayTxHash")]
    public string? ReplayTxHash { get; init; }

    /// <summary>
    /// Optional inline replay calldata captured from the real transaction. Preferred over
    /// <see cref="ReplayTxHash"/> for determinism: an Anvil fork at txBlock-1 does not contain
    /// the future tx, so fetching it by hash is unreliable. When present, this is executed
    /// directly (after any <see cref="Mutation"/>).
    /// </summary>
    [JsonPropertyName("replayCalldata")]
    public ReplayCalldata? ReplayCalldata { get; init; }

    /// <summary>
    /// Optional single-field mutation applied to the replayed calldata to isolate a
    /// downstream revert branch while keeping all other preconditions valid
    /// (e.g. corrupt the Merkle proof to hit InvalidScore).
    /// </summary>
    [JsonPropertyName("mutation")]
    public ScenarioMutation? Mutation { get; init; }

    /// <summary>
    /// Optional DB-state assertion run against the session's block-pinned query API.
    /// Validates the indexed CrcV2_ScoreGroup_* row math at the pinned block.
    /// </summary>
    [JsonPropertyName("dbStateAssertion")]
    public DbStateAssertion? DbStateAssertion { get; init; }

    /// <summary>
    /// When true, the pathfinder projection tier is skipped for this scenario.
    /// Set for replay-only cases (e.g. personal mints) where there is no meaningful
    /// source→sink flow path to compute.
    /// </summary>
    [JsonPropertyName("skipProjection")]
    public bool SkipProjection { get; init; }

    /// <summary>
    /// Whether this scenario should be executed against the live SDK (sdk-v2) in the
    /// TypeScript e2e suite. Informational marker for cross-repo coverage tracking.
    /// </summary>
    [JsonPropertyName("sdkE2E")]
    public bool SdkE2E { get; init; }

    /// <summary>
    /// Whether this scenario is complete enough to run (has block, source, sink).
    /// </summary>
    [JsonIgnore]
    public bool IsComplete => Block > 0 && !string.IsNullOrEmpty(Source) && !string.IsNullOrEmpty(Sink);

    /// <summary>
    /// Returns a display name for NUnit test output.
    /// </summary>
    public override string ToString() => $"{Category}/{Id}: {Name}";
}

/// <summary>
/// A single-field mutation applied to replayed transaction calldata to isolate a
/// specific revert branch. The mutation is applied by <c>AnvilExecutionHelper</c>.
/// </summary>
public record ScenarioMutation
{
    /// <summary>Which logical field to mutate: "proof", "amount", or "collateral".</summary>
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    /// <summary>
    /// Mutation operation: "corrupt" (flip bytes), "increment" (add Value), or "zero".
    /// </summary>
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    /// <summary>Optional operand (e.g. the increment delta as a decimal string).</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    /// Hex marker ("0x...") locating the field to mutate inside the calldata when the
    /// field cannot be derived structurally (e.g. the exact proof word). Optional.
    /// </summary>
    [JsonPropertyName("locator")]
    public string? Locator { get; init; }
}

/// <summary>
/// Inline real-transaction calldata for deterministic Anvil replay.
/// </summary>
public record ReplayCalldata
{
    /// <summary>Original transaction sender (impersonated on the fork).</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Original transaction recipient.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>Original transaction input (hex, 0x-prefixed).</summary>
    [JsonPropertyName("input")]
    public required string Input { get; init; }
}

/// <summary>
/// A block-pinned DB-state assertion executed via the test-env session query API.
/// </summary>
public record DbStateAssertion
{
    /// <summary>A read-only scalar SELECT evaluated at the session's pinned block.</summary>
    [JsonPropertyName("scalarSql")]
    public required string ScalarSql { get; init; }

    /// <summary>Expected scalar result (string comparison). Optional.</summary>
    [JsonPropertyName("expect")]
    public string? Expect { get; init; }

    /// <summary>Minimum acceptable scalar value (numeric). Optional.</summary>
    [JsonPropertyName("minValue")]
    public string? MinValue { get; init; }
}

/// <summary>
/// Categories for transfer scenarios.
/// </summary>
public static class ScenarioCategories
{
    public const string DirectTransfer = "direct-transfer";
    public const string GroupMinting = "group-minting";
    public const string SelfConversion = "self-conversion";
    public const string WrappedTokens = "wrapped-tokens";
    public const string ConsentedFlow = "consented-flow";

    // ScoreGroup matrix categories
    public const string ScoreGroupRouter = "score-group-router";
    public const string ScoreGroupMint = "score-group-mint";
    public const string ScoreGroupGraph = "score-group-graph";
    public const string GroupBacking = "group-backing";
    public const string GroupMigration = "group-migration";

    // Negative test categories
    public const string NoPath = "no-path";
    public const string InvalidInput = "invalid-input";
    public const string ContractRevert = "contract-revert";
}

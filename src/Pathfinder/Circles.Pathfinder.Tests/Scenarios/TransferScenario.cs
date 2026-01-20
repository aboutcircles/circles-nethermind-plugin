using System.Text.Json.Serialization;

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
    /// </summary>
    [JsonPropertyName("block")]
    public required long Block { get; init; }

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
    /// Tokens to exclude from path computation.
    /// </summary>
    [JsonPropertyName("excludedTokens")]
    public string[]? ExcludedTokens { get; init; }

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
    /// Returns a display name for NUnit test output.
    /// </summary>
    public override string ToString() => $"{Category}/{Id}: {Name}";
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

    // Negative test categories
    public const string NoPath = "no-path";
    public const string InvalidInput = "invalid-input";
    public const string ContractRevert = "contract-revert";
}

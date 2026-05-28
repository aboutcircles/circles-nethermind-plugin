using System.Text.Json.Serialization;
using Circles.Common;

namespace Circles.Pathfinder.Data;

/// <summary>
/// Deserialized graph snapshot from the Cache Service's /api/pathfinder/graph endpoint.
/// </summary>
public record PathfinderGraphSnapshot(
    int SchemaVersion,
    long LastProcessedBlock,
    long Timestamp,
    IReadOnlyList<PathfinderGraphBalanceRow>? Balances,
    IReadOnlyList<PathfinderGraphTrustRow>? Trust,
    IReadOnlyList<PathfinderGraphGroupRow>? Groups,
    IReadOnlyList<PathfinderGraphGroupTrustRow>? GroupTrusts,
    IReadOnlyList<PathfinderGraphConsentedFlowRow>? ConsentedFlow,
    IReadOnlyList<string>? Avatars,
    IReadOnlyList<string>? Organizations = null,
    IReadOnlyList<PathfinderGraphWrapperMappingRow>? WrapperMappings = null,
    IReadOnlyList<PathfinderGraphGroupRouterRow>? GroupRouters = null,
    IReadOnlyList<PathfinderGraphScoreGroupMintLimitRow>? ScoreGroupMintLimits = null,
    IReadOnlyList<PathfinderGraphOperatorApprovalRow>? OperatorApprovals = null,
    IReadOnlyList<string>? ScoreRouters = null
);

public record PathfinderGraphOperatorApprovalRow(
    string Account,
    string Operator
);

/// <summary>
/// Deserialized balance row from the cache service's /api/pathfinder/graph snapshot.
/// </summary>
/// <remarks>
/// <para><b>Wire-format note for the <c>DemurrageMode</c> field:</b> the JSON property
/// is intentionally <c>circlesType</c> (not <c>demurrageMode</c>) via
/// <c>[JsonPropertyName]</c>. The C# property was renamed away from <c>CirclesType</c>
/// to avoid a same-file name collision with <see cref="PathfinderGraphWrapperMappingRow.CirclesType"/>
/// (which is the typed enum). The wire field MUST remain <c>circlesType</c> for
/// backward compat — independent cache/pathfinder rollouts depend on this. Renaming
/// the wire field (dropping <c>[JsonPropertyName]</c>) would require a coordinated
/// cache+pathfinder deploy and a schema-version bump.</para>
/// <para>Default <c>"demurraged"</c> is the safe-degrade direction: a snapshot from
/// an older cache that omits the field deserializes to demurraged, which
/// <c>CacheLoadGraph.LoadV2Balances</c> routes via the non-static branch.</para>
/// </remarks>
public record PathfinderGraphBalanceRow(
    string Balance,
    string Account,
    string TokenAddress,
    long LastActivity,
    bool IsWrapped,
    [property: JsonPropertyName("circlesType")] string DemurrageMode = "demurraged"
);

public record PathfinderGraphTrustRow(
    string Truster,
    string Trustee,
    int Limit
);

public record PathfinderGraphGroupRow(string GroupAddress);

public record PathfinderGraphGroupRouterRow(
    string GroupAddress,
    string RouterAddress
);

public record PathfinderGraphScoreGroupMintLimitRow(
    string GroupAddress,
    string CollateralToken,
    string AvailableLimit
);

public record PathfinderGraphGroupTrustRow(
    string GroupAddress,
    string TrustedToken
);

public record PathfinderGraphConsentedFlowRow(
    string Avatar,
    bool HasConsentedFlow
);

/// <summary>
/// Wrapper-mapping row from the cache service's /api/pathfinder/graph snapshot.
/// </summary>
/// <remarks>
/// The <see cref="CirclesType"/> property is the typed enum representation of the
/// same protocol concept that <see cref="PathfinderGraphBalanceRow.DemurrageMode"/>
/// encodes as a string (<c>"static"</c> ↔ <see cref="Circles.Common.CirclesType.InflationaryCircles"/>,
/// <c>"demurraged"</c> ↔ <see cref="Circles.Common.CirclesType.DemurrageCircles"/>).
/// The dual encoding is a tracked oddity — see the comment on
/// <see cref="PathfinderGraphBalanceRow.DemurrageMode"/> for the wire-compat rationale.
/// </remarks>
public record PathfinderGraphWrapperMappingRow(
    string WrapperAddress,
    string UnderlyingAvatar,
    // Default DemurrageCircles is safe-degrade: old cache snapshots without this field deserialize
    // as demurraged, matching pre-PR-#408 canary behavior (false positives for inflationary
    // unwraps, no new reverts).
    CirclesType CirclesType = CirclesType.DemurrageCircles
);

/// <summary>
/// DTO for JSON deserialization — maps camelCase property names from the cache service response.
/// </summary>
internal sealed class PathfinderGraphSnapshotDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("lastProcessedBlock")]
    public long LastProcessedBlock { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("balances")]
    public List<PathfinderGraphBalanceRow>? Balances { get; set; }

    [JsonPropertyName("trust")]
    public List<PathfinderGraphTrustRow>? Trust { get; set; }

    [JsonPropertyName("groups")]
    public List<PathfinderGraphGroupRow>? Groups { get; set; }

    [JsonPropertyName("groupRouters")]
    public List<PathfinderGraphGroupRouterRow>? GroupRouters { get; set; }

    [JsonPropertyName("scoreGroupMintLimits")]
    public List<PathfinderGraphScoreGroupMintLimitRow>? ScoreGroupMintLimits { get; set; }

    [JsonPropertyName("groupTrusts")]
    public List<PathfinderGraphGroupTrustRow>? GroupTrusts { get; set; }

    [JsonPropertyName("consentedFlow")]
    public List<PathfinderGraphConsentedFlowRow>? ConsentedFlow { get; set; }

    [JsonPropertyName("avatars")]
    public List<string>? Avatars { get; set; }

    [JsonPropertyName("organizations")]
    public List<string>? Organizations { get; set; }

    [JsonPropertyName("wrapperMappings")]
    public List<PathfinderGraphWrapperMappingRow>? WrapperMappings { get; set; }

    [JsonPropertyName("operatorApprovals")]
    public List<PathfinderGraphOperatorApprovalRow>? OperatorApprovals { get; set; }

    [JsonPropertyName("scoreRouters")]
    public List<string>? ScoreRouters { get; set; }

    public PathfinderGraphSnapshot ToModel() => new(
        SchemaVersion,
        LastProcessedBlock,
        Timestamp,
        Balances,
        Trust,
        Groups,
        GroupTrusts,
        ConsentedFlow,
        Avatars,
        Organizations,
        WrapperMappings,
        GroupRouters,
        ScoreGroupMintLimits,
        OperatorApprovals,
        ScoreRouters);
}

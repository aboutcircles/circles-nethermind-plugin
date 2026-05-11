using System.Text.Json.Serialization;

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
    IReadOnlyList<PathfinderGraphScoreGroupMintLimitRow>? ScoreGroupMintLimits = null
);

public record PathfinderGraphBalanceRow(
    string Balance,
    string Account,
    string TokenAddress,
    long LastActivity,
    bool IsWrapped,
    string CirclesType
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

public record PathfinderGraphWrapperMappingRow(
    string WrapperAddress,
    string UnderlyingAvatar
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
        ScoreGroupMintLimits);
}

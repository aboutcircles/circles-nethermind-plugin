namespace Circles.Cache.Service.Models;

/// <summary>
/// Response for token balance queries
/// </summary>
public record TokenBalanceResponse(
    string TokenId,
    string Balance,
    string? TokenOwner = null,
    int Version = 0,
    string? TokenType = null,
    bool? IsGroup = null,
    bool? IsErc20 = null,
    bool? IsErc1155 = null,
    bool? IsWrapped = null,
    bool? IsInflationary = null,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for total balance queries
/// </summary>
public record TotalBalanceResponse(
    string Balance,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for avatar info queries
/// </summary>
public record AvatarInfoResponse(
    string Avatar,
    int Version,
    string Type,
    string? TokenId = null,
    bool HasV1 = false,
    string? V1Token = null,
    string? CidV0 = null,
    bool IsHuman = false,
    string? Name = null,
    string? Symbol = null,
    string? ShortName = null,
    long? RegisteredAt = null,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for profile CID queries
/// </summary>
public record ProfileCidResponse(
    string? Cid,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Batch request for avatar info
/// </summary>
public record AvatarInfoBatchRequest(string[] Addresses);

/// <summary>
/// Batch request for profile CIDs
/// </summary>
public record ProfileCidBatchRequest(string[] Addresses);

/// <summary>
/// Response for trust relation queries
/// </summary>
public record TrustRelationResponse(
    string Truster,
    string Trustee,
    long ExpiryTime,
    int Version,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for trust relations by address
/// </summary>
public record TrustRelationsResponse(
    string Address,
    TrustRelationResponse[] Trusts,
    TrustRelationResponse[] TrustedBy,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for group membership queries
/// </summary>
public record GroupMembershipResponse(
    string Group,
    string Member,
    long ExpiryTime,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for group members query
/// </summary>
public record GroupMembersResponse(
    string Group,
    GroupMembershipResponse[] Members,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for member's group memberships query
/// </summary>
public record MemberGroupsResponse(
    string Member,
    GroupMembershipResponse[] Groups,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Response for token info queries
/// </summary>
public record TokenInfoResponse(
    string TokenAddress,
    string TokenOwner,
    string TokenType,
    int Version,
    bool IsErc20,
    bool IsErc1155,
    bool IsWrapped,
    bool IsInflationary,
    bool IsGroup,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Batch request for token info
/// </summary>
public record TokenInfoBatchRequest(string[] TokenAddresses);

/// <summary>
/// Response for profile content queries (IPFS payload)
/// </summary>
public record ProfileContentResponse(
    string Cid,
    string? Content,
    long LastProcessedBlock = -1,
    long Timestamp = 0
);

/// <summary>
/// Batch request for profile content
/// </summary>
public record ProfileContentBatchRequest(string[] Cids);

// ─── Pathfinder Graph Snapshot DTOs ────────────────────────────────

/// <summary>
/// Canonical graph snapshot for the Pathfinder cache source endpoint.
/// </summary>
public record PathfinderGraphResponse(
    int SchemaVersion,
    long LastProcessedBlock,
    long Timestamp,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderBalanceRow>? Balances,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderTrustRow>? Trust,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderGroupRow>? Groups,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderGroupTrustRow>? GroupTrusts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderConsentedFlowRow>? ConsentedFlow,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Avatars,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderWrapperMappingRow>? WrapperMappings
);

public record PathfinderBalanceRow(
    string Balance,
    string Account,
    string TokenAddress,
    long LastActivity,
    bool IsWrapped,
    string CirclesType
);

public record PathfinderTrustRow(
    string Truster,
    string Trustee,
    int Limit
);

public record PathfinderGroupRow(string GroupAddress);

public record PathfinderGroupTrustRow(
    string GroupAddress,
    string TrustedToken
);

public record PathfinderConsentedFlowRow(
    string Avatar,
    bool HasConsentedFlow
);

public record PathfinderWrapperMappingRow(
    string WrapperAddress,
    string UnderlyingAvatar
);

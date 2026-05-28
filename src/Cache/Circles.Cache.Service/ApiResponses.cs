using System.Text.Json.Serialization;
using Circles.Common;

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
    IReadOnlyList<PathfinderGroupRouterRow>? GroupRouters,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderGroupTrustRow>? GroupTrusts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderConsentedFlowRow>? ConsentedFlow,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Avatars,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Organizations,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderWrapperMappingRow>? WrapperMappings,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderScoreGroupMintLimitRow>? ScoreGroupMintLimits,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ScoreRouters,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PathfinderOperatorApprovalRow>? OperatorApprovals
);

/// <summary>
/// Balance row for the /api/pathfinder/graph snapshot.
/// </summary>
/// <remarks>
/// <para><b>Wire-format note for the <c>DemurrageMode</c> field:</b> the JSON property
/// is intentionally <c>circlesType</c> (not <c>demurrageMode</c>) via
/// <c>[JsonPropertyName]</c>. The C# property was renamed away from <c>CirclesType</c>
/// to avoid a same-file name collision with <see cref="PathfinderWrapperMappingRow.CirclesType"/>
/// (which is the typed enum). The wire field MUST remain <c>circlesType</c> for
/// backward compat — independent cache/pathfinder rollouts depend on this. Renaming
/// the wire field (dropping <c>[JsonPropertyName]</c>) would require a coordinated
/// cache+pathfinder deploy and a schema-version bump.</para>
/// <para>Default <c>"demurraged"</c> is the safe-degrade direction: a snapshot from
/// an older cache that omits the field deserializes to demurraged, which the
/// pathfinder consumer (<c>CacheLoadGraph.LoadV2Balances</c>) routes via the
/// non-static branch — matches pre-field-introduction behavior.</para>
/// </remarks>
public record PathfinderBalanceRow(
    string Balance,
    string Account,
    string TokenAddress,
    long LastActivity,
    bool IsWrapped,
    [property: JsonPropertyName("circlesType")] string DemurrageMode = "demurraged"
);

public record PathfinderTrustRow(
    string Truster,
    string Trustee,
    int Limit
);

public record PathfinderGroupRow(string GroupAddress);

public record PathfinderGroupRouterRow(
    string GroupAddress,
    string RouterAddress
);

public record PathfinderScoreGroupMintLimitRow(
    string GroupAddress,
    string CollateralToken,
    string AvailableLimit
);

public record PathfinderGroupTrustRow(
    string GroupAddress,
    string TrustedToken
);

public record PathfinderConsentedFlowRow(
    string Avatar,
    bool HasConsentedFlow
);

/// <summary>
/// Wrapper-mapping row for the /api/pathfinder/graph snapshot.
/// </summary>
/// <remarks>
/// The <see cref="CirclesType"/> property is the typed enum representation of the
/// same protocol concept that <see cref="PathfinderBalanceRow.DemurrageMode"/>
/// encodes as a string (<c>"static"</c> ↔ <see cref="Circles.Common.CirclesType.InflationaryCircles"/>,
/// <c>"demurraged"</c> ↔ <see cref="Circles.Common.CirclesType.DemurrageCircles"/>).
/// The dual encoding is a tracked oddity — see the comment on
/// <see cref="PathfinderBalanceRow.DemurrageMode"/> for the wire-compat rationale.
/// </remarks>
public record PathfinderWrapperMappingRow(
    string WrapperAddress,
    string UnderlyingAvatar,
    CirclesType CirclesType = CirclesType.DemurrageCircles
);

public record PathfinderOperatorApprovalRow(
    string Account,
    string Operator
);

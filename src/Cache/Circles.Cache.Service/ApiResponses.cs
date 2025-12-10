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

namespace Circles.Cache.Service.Models;

/// <summary>
/// Response for token balance queries
/// </summary>
public record TokenBalanceResponse(
    string TokenId,
    string Balance,
    string? TokenOwner = null,
    int Version = 0,
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

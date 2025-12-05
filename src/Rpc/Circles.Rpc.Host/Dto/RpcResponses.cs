using System.Text.Json;
using Circles.Index.Common.Dto;

namespace Circles.Rpc.Host.Dto;

public record ProfileViewResponse(
    string Address,
    AvatarInfo? AvatarInfo,
    JsonElement? Profile,
    TrustStats TrustStats,
    string? V1Balance,
    string? V2Balance
);

public record TrustStats(
    int TrustsCount,
    int TrustedByCount
);

public record TrustNetworkSummaryResponse(
    string Address,
    int DirectTrustsCount,
    int DirectTrustedByCount,
    int MutualTrustsCount,
    string[] MutualTrusts,
    int NetworkReach
);

public record AggregatedTrustRelationsResponse(
    string Address,
    TrustRelationInfo[] Mutual,
    TrustRelationInfo[] Trusts,
    TrustRelationInfo[] TrustedBy
);

public record TrustRelationInfo(
    string Address,
    AvatarInfo? AvatarInfo,
    string RelationType
);

public record ValidInvitersResponse(
    string Address,
    InviterInfo[] ValidInviters
);

public record InviterInfo(
    string Address,
    string Balance,
    AvatarInfo AvatarInfo
);

public record EnrichedTransaction(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    int Version,
    string From,
    string To,
    string? Operator,
    string? Id,
    string Value,
    string Circles,
    string AttoCircles,
    string Crc,
    string AttoCrc,
    string StaticCircles,
    string StaticAttoCircles,
    JsonElement? FromProfile,
    JsonElement? ToProfile
);

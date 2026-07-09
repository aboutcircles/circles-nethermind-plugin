using Circles.Common;

namespace Circles.Index.CirclesV2.MultiAffiliateGroupRegistry;

public record AffiliateGroupAdded(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string AffiliateGroup,
    string Avatar,
    // True when this Added came from the registry's deployer-only initialize() bulk-seed (detected by
    // the calling tx's function selector). initialize() OVERWRITES an avatar's list to a single group
    // but does not emit AffiliateGroupRemoved for displaced groups, so the membership view treats a
    // seed as a per-avatar reset point. See V_CrcV2_AffiliateGroupMembers / …SeedConflicts.
    bool IsSeed) : IIndexEvent;

public record AffiliateGroupRemoved(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string AffiliateGroup,
    string Avatar) : IIndexEvent;

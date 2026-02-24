using Circles.Common;

namespace Circles.Index.CirclesV2.AffiliateGroupRegistry;

public record AffiliateGroupChanged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Human,
    string OldGroup,
    string NewGroup) : IIndexEvent;

public record NotificationFailed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string Human) : IIndexEvent;

public record NotificationSuccessful(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string Human) : IIndexEvent;

using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.V2;

public record RegisterOrganization(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Organization,
    string Name) : IIndexEvent;

public record RegisterGroup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Group,
    string Mint,
    string Treasury,
    string Name,
    string Symbol) : IIndexEvent;

public record RegisterHuman(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar) : IIndexEvent;

public record PersonalMint(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Human,
    UInt256 Amount,
    UInt256 StartPeriod,
    UInt256 EndPeriod) : IIndexEvent;

public record InviteHuman(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Inviter,
    string Invited) : IIndexEvent;

public record Trust(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Truster,
    string Trustee,
    UInt256 ExpiryTime) : IIndexEvent;

public record Stopped(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar) : IIndexEvent;

public record ApprovalForAll(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    string Operator,
    bool Approved) : IIndexEvent;

public record TransferSingle(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexEvent;

public record TransferBatch(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    int BatchIndex,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexEvent;

public record URI(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    UInt256 Id,
    string Value) : IIndexEvent;
using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

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
    string Avatar,
    string Inviter) : IIndexEvent;

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

public record ERC20WrapperDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar,
    string Erc20Wrapper,
    long CirclesType) : IIndexEvent;

public record Erc20WrapperTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string TokenAddress,
    string From,
    string To,
    UInt256 Value) : IIndexEvent;

public record DepositInflationary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    UInt256 Amount,
    UInt256 DemurragedAmount) : IIndexEvent;

public record WithdrawInflationary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    UInt256 Amount,
    UInt256 DemurragedAmount) : IIndexEvent;

public record DepositDemurraged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    UInt256 Amount,
    UInt256 InflationaryAmount) : IIndexEvent;

public record WithdrawDemurraged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    UInt256 Amount,
    UInt256 InflationaryAmount) : IIndexEvent;

public record StreamCompleted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Amount) : IIndexEvent;
    
public record DiscountCost(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Account,
    UInt256 Id,
    UInt256 Cost) : IIndexEvent;
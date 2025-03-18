using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Hub;

public record RegisterOrganization(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Organization,
    string Name) : IIndexedEventV2;

public record RegisterGroup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string Mint,
    string Treasury,
    string Name,
    string Symbol) : IIndexedEventV2;

public record RegisterHuman(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    string Inviter) : IIndexedEventV2;

public record PersonalMint(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Human,
    UInt256 Amount,
    UInt256 StartPeriod,
    UInt256 EndPeriod) : IIndexedEventV2;

public record Trust(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Truster,
    string Trustee,
    UInt256 ExpiryTime) : IIndexedEventV2;

public record Stopped(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar) : IIndexedEventV2;

public record ApprovalForAll(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    string Operator,
    bool Approved) : IIndexedEventV2;

public record TransferSingle(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexedEventV2;

public record TransferBatch(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    int BatchIndex,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexedEventV2;

public record ERC20WrapperDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    string Erc20Wrapper,
    long CirclesType) : IIndexedEventV2;

public record Erc20WrapperTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string TokenAddress,
    string From,
    string To,
    UInt256 Value) : IIndexedEventV2;

public record DepositInflationary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Amount,
    UInt256 DemurragedAmount) : IIndexedEventV2;

public record WithdrawInflationary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Amount,
    UInt256 DemurragedAmount) : IIndexedEventV2;

public record DepositDemurraged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Amount,
    UInt256 InflationaryAmount) : IIndexedEventV2;

public record WithdrawDemurraged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Amount,
    UInt256 InflationaryAmount) : IIndexedEventV2;

public record StreamCompleted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Operator,
    string From,
    string To,
    UInt256 Id,
    UInt256 Amount) : IIndexedEventV2;

public record DiscountCost(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Id,
    UInt256 Cost) : IIndexedEventV2;

public record GroupMint(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Sender,
    string Receiver,
    string Group,
    UInt256 Collateral,
    UInt256 Amount) : IIndexedEventV2;

public record FlowEdgesScopeSingleStarted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    UInt256 FlowEdgeId,
    UInt16 StreamId) : IIndexedEventV2;

public record FlowEdgesScopeLastEnded(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter) : IIndexedEventV2;

public record TransferSummary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string From,
    string To,
    UInt256 Amount,
    string Events
) : IIndexEvent;
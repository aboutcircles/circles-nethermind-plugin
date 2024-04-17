using Nethermind.Int256;

namespace Circles.Index.Indexer;

public record Block(long BlockNumber, long Timestamp, string BlockHash);

public record CirclesSignupData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string CirclesAddress,
    string? TokenAddress);

public record CirclesTrustData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string UserAddress,
    string CanSendToAddress,
    long Limit);

public record CirclesHubTransferData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string FromAddress,
    string ToAddress,
    UInt256 Amount);

public record Erc20TransferData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string TokenAddress,
    string FromAddress,
    string ToAddress,
    UInt256 Amount);

public record CrcV2RegisterHumanData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string Address);

public record CrcV2InviteHumanData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string InviterAddress,
    string InviteeAddress);

public record CrcV2RegisterOrganizationData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string OrganizationAddress,
    string OrganizationName);

public record CrcV2RegisterGroupData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string GroupAddress,
    string GroupMintPolicy,
    string GroupTreasury,
    string GroupName,
    string GroupSymbol);

public record CrcV2PersonalMintData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string ToAddress,
    UInt256 Amount,
    UInt256 StartPeriod,
    UInt256 EndPeriod);

public record CrcV2ConvertInflationData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    UInt256 InflationValue,
    UInt256 DemurrageValue,
    ulong Day);

public record CrcV2TrustData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string TrusterAddress,
    string TrusteeAddress,
    UInt256 ExpiryTime);

public record CrcV2StoppedData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string Address);

public record Erc1155TransferSingleData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string OperatorAddress,
    string FromAddress,
    string ToAddress,
    UInt256 TokenId,
    UInt256 Amount);

public record Erc1155TransferBatchData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    long BatchIndex,
    string TransactionHash,
    string OperatorAddress,
    string FromAddress,
    string ToAddress,
    UInt256 TokenId,
    UInt256 Amount);

public record Erc1155ApprovalForAllData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    string OwnerAddress,
    string OperatorAddress,
    bool Approved);

public record Erc1155UriData(
    long BlockNumber,
    long Timestamp,
    long TransactionIndex,
    long LogIndex,
    string TransactionHash,
    UInt256 TokenId,
    string Uri);
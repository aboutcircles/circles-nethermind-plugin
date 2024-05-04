using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.V2;

public record CrcV2RegisterOrganizationData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OrgAddress, string OrgName) : IIndexEvent;
public record CrcV2RegisterGroupData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string GroupAddress, string MintPolicy, string Treasury, string GroupName, string GroupSymbol) : IIndexEvent;
public record CrcV2RegisterHumanData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string HumanAddress) : IIndexEvent;
public record CrcV2PersonalMintData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string ToAddress, UInt256 Amount, UInt256 StartPeriod, UInt256 EndPeriod) : IIndexEvent;
public record CrcV2InviteHumanData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string InviterAddress, string InviteeAddress) : IIndexEvent;
public record CrcV2ConvertInflationData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, UInt256 InflationValue, UInt256 DemurrageValue, ulong Day) : IIndexEvent;
public record CrcV2TrustData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string UserAddress, string CanSendToAddress, UInt256 Limit) : IIndexEvent;
public record CrcV2StoppedData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string Address) : IIndexEvent;
public record Erc1155ApprovalForAllData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OperatorAddress, string ApprovedAddress, bool Approved) : IIndexEvent;
public record Erc1155TransferSingleData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OperatorAddress, string FromAddress, string ToAddress, UInt256 Id, UInt256 Value) : IIndexEvent;
public record Erc1155TransferBatchData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, int BatchIndex, string OperatorAddress, string FromAddress, string ToAddress, UInt256 Id, UInt256 Value) : IIndexEvent;
public record Erc1155UriData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string TokenAddress, UInt256 Id, string Uri) : IIndexEvent;
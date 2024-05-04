using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.V1;

public record CirclesSignupData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string CirclesAddress, string? TokenAddress) : IIndexEvent;
public record CirclesTrustData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string UserAddress, string CanSendToAddress, int Limit) : IIndexEvent;
public record CirclesHubTransferData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string FromAddress, string ToAddress, UInt256 Amount) : IIndexEvent;
public record Erc20TransferData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string TokenAddress, string From, string To, UInt256 Value) : IIndexEvent;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Int256;

namespace Circles.Index.Metri;

public record ProxyCreation(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Proxy,
    string Singleton) : IIndexEvent;

public record ModuleProxyCreation(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Proxy,
    string MasterCopy) : IIndexEvent;

public record OwnershipTransferred(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string From,
    string To) : IIndexEvent;

public record GnosisPayOGNftTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string From,
    string To,
    UInt256 TokenId) : IIndexEvent;

public record Erc20Transfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string From,
    string To,
    UInt256 Value) : IIndexEvent;

public record XDaiTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string From,
    string To,
    UInt256 Value) : IIndexEvent;

public record ExecutionSuccess(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    byte[] TxHash,
    UInt256 Payment) : IIndexEvent;

public record ExecutionFailure(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    byte[] TxHash,
    UInt256 Payment) : IIndexEvent;

public record SafeMultiSigTransaction(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string To,
    UInt256 Value,
    byte[] Data,
    byte Operation,
    UInt256 SafeTxGas,
    UInt256 BaseGas,
    UInt256 GasPrice,
    string GasToken,
    string RefundReceiver,
    byte[] Signatures,
    byte[] AdditionalInfo) : IIndexEvent;

public record SafeReceived(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Sender,
    UInt256 Value) : IIndexEvent;

public record SafeSetup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Initiator,
    string[] Owners,
    UInt256 Threshold,
    string Initializer,
    string FallbackHandler) : IIndexEvent;

public record RemovedOwner(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Owner) : IIndexEvent;

public record GPv2Settlement(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Owner,
    string SellToken,
    string BuyToken,
    UInt256 SellAmount,
    UInt256 BuyAmount,
    UInt256 FeeAmount,
    byte[] OrderUid) : IIndexEvent;

// public record CoWSwapEthFlow(
//     long BlockNumber,
//     long Timestamp,
//     int TransactionIndex,
//     int LogIndex,
//     string TransactionHash,
//     string Sender,
//     string Order,
//     string Signature,
//     string Data) : IIndexEvent;
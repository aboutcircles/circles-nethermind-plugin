using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.StandardTreasury;

public record CreateVault(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Group,
    string Vault) : IIndexEvent;

public record CollateralLockedSingle(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] UserData) : IIndexEvent;

public record CollateralLockedBatch(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    int BatchIndex,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] UserData) : IIndexEvent;

public record GroupRedeem(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] Data) : IIndexEvent;

public record GroupRedeemCollateralReturn(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    int BatchIndex,
    string Group,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexEvent;

public record GroupRedeemCollateralBurn(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    int BatchIndex,
    string Group,
    UInt256 Id,
    UInt256 Value) : IIndexEvent;
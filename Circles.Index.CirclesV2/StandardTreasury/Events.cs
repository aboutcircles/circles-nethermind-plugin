using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.StandardTreasury;

public record CreateVault(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string Vault) : IIndexedEventV2;

public record CollateralLockedSingle(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] UserData) : IIndexedEventV2;

public record CollateralLockedBatch(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    int BatchIndex,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] UserData) : IIndexedEventV2;

public record GroupRedeem(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    UInt256 Id,
    UInt256 Value,
    byte[] Data) : IIndexedEventV2;

public record GroupRedeemCollateralReturn(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    int BatchIndex,
    string Group,
    string To,
    UInt256 Id,
    UInt256 Value) : IIndexedEventV2;

public record GroupRedeemCollateralBurn(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    int BatchIndex,
    string Group,
    UInt256 Id,
    UInt256 Value) : IIndexedEventV2;
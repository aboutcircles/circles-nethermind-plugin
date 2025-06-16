using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.Balancer;

// ──────────────────────────────────────────────────────────────────────────
// Admin
public record AuthorizerChangedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter, string NewAuthorizer) : IIndexEvent;

public record PausedStateChangedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter, bool Paused) : IIndexEvent;

public record RelayerApprovalChangedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    string Relayer, string Sender, bool Approved) : IIndexEvent;

// ──────────────────────────────────────────────────────────────────────────
// Balances
public record InternalBalanceChangedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    string User, string Token, UInt256 Delta) : IIndexEvent;

public record ExternalBalanceTransferEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    string Token, string Sender, string Recipient, UInt256 Amount) : IIndexEvent;

// ──────────────────────────────────────────────────────────────────────────
// Flash & Swap
public record FlashLoanEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    string Recipient, string Token, UInt256 Amount, UInt256 FeeAmount) : IIndexEvent;

public record SwapEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, string TokenIn, string TokenOut, UInt256 AmountIn, UInt256 AmountOut) : IIndexEvent;

// ──────────────────────────────────────────────────────────────────────────
// Pools & Tokens
public record PoolRegisteredEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, string PoolAddress, int Specialization) : IIndexEvent;

public record TokensRegisteredEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, int BatchIndex, string Token, string AssetManager) : IIndexEvent;

public record TokensDeregisteredEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, int BatchIndex, string Token) : IIndexEvent;

// ──────────────────────────────────────────────────────────────────────────
// Pool balances
public record PoolBalanceChangedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, string LiquidityProvider, int BatchIndex,
    string Token, UInt256 Delta, UInt256 ProtocolFeeAmount) : IIndexEvent;

public record PoolBalanceManagedEvt(
    long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex,
    string TransactionHash, string Emitter,
    byte[] PoolId, string AssetManager, string Token,
    UInt256 CashDelta, UInt256 ManagedDelta) : IIndexEvent;

using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.Balancer;

public class DatabaseSchema : BaseDatabaseSchema
{
    private static readonly EventFieldSchema[] StdCols =
    [
        new("blockNumber", ValueTypes.Int, true, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true, true),
        new("logIndex", ValueTypes.Int, true, true),
        new("transactionHash", ValueTypes.String, true)
    ];

    private static List<EventFieldSchema> Combine(params EventFieldSchema[] extra) =>
        StdCols.Concat(extra).ToList();

    // ------------------------------------------------------------------
    // 1) Authorizer / Pause / Relayer
    public static readonly EventSchema AuthorizerChanged = new(
        "BalancerV2", "VaultAuthorizerChanged",
        Keccak.Compute("AuthorizerChanged(address)").BytesToArray(),
        Combine(
            [new("newAuthorizer", ValueTypes.Address, true)]
        ));

    public static readonly EventSchema PausedStateChanged = new(
        "BalancerV2", "VaultPausedStateChanged",
        Keccak.Compute("PausedStateChanged(bool)").BytesToArray(),
        Combine(
            [new("paused", ValueTypes.Boolean, true)]
        ));

    public static readonly EventSchema RelayerApprovalChanged = new(
        "BalancerV2", "VaultRelayerApprovalChanged",
        Keccak.Compute("RelayerApprovalChanged(address,address,bool)").BytesToArray(),
        Combine(
            new("relayer", ValueTypes.Address, true),
            new("sender", ValueTypes.Address, true),
            new("approved", ValueTypes.Boolean, true)
        ));

    // ------------------------------------------------------------------
    // 2) Internal / External balances
    public static readonly EventSchema InternalBalanceChanged = new(
        "BalancerV2", "VaultInternalBalanceChanged",
        Keccak.Compute("InternalBalanceChanged(address,address,int256)").BytesToArray(),
        Combine(
            new("user", ValueTypes.Address, true),
            new("token", ValueTypes.Address, true),
            new("delta", ValueTypes.BigInt, false)
        ));

    public static readonly EventSchema ExternalBalanceTransfer = new(
        "BalancerV2", "VaultExternalBalanceTransfer",
        Keccak.Compute("ExternalBalanceTransfer(address,address,address,uint256)").BytesToArray(),
        Combine(
            new("token", ValueTypes.Address, true),
            new("sender", ValueTypes.Address, true),
            new("recipient", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false)
        ));

    // ------------------------------------------------------------------
    // 3) Flash-loan & Swap
    public static readonly EventSchema FlashLoan = new(
        "BalancerV2", "VaultFlashLoan",
        Keccak.Compute("FlashLoan(address,address,uint256,uint256)").BytesToArray(),
        Combine(
            new("recipient", ValueTypes.Address, true),
            new("token", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false),
            new("feeAmount", ValueTypes.BigInt, false)
        ));

    public static readonly EventSchema Swap = new(
        "BalancerV2", "VaultSwap",
        Keccak.Compute("Swap(bytes32,address,address,uint256,uint256)").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("tokenIn", ValueTypes.Address, true),
            new("tokenOut", ValueTypes.Address, true),
            new("amountIn", ValueTypes.BigInt, false),
            new("amountOut", ValueTypes.BigInt, false)
        ));

    // ------------------------------------------------------------------
    // 4) Pools & Tokens
    public static readonly EventSchema PoolRegistered = new(
        "BalancerV2", "VaultPoolRegistered",
        Keccak.Compute("PoolRegistered(bytes32,address,uint8)").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("poolAddress", ValueTypes.Address, true),
            new("specialization", ValueTypes.Int, true)
        ));

    public static readonly EventSchema TokensRegistered = new(
        "BalancerV2", "VaultTokensRegistered",
        Keccak.Compute("TokensRegistered(bytes32,address[],address[])").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("token", ValueTypes.Address, true),
            new("assetManager", ValueTypes.Address, true)
        ));

    public static readonly EventSchema TokensDeregistered = new(
        "BalancerV2", "VaultTokensDeregistered",
        Keccak.Compute("TokensDeregistered(bytes32,address[])").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("token", ValueTypes.Address, true)
        ));

    // ------------------------------------------------------------------
    // 5) Pool balances
    public static readonly EventSchema PoolBalanceChanged = new(
        "BalancerV2", "VaultPoolBalanceChanged",
        Keccak.Compute("PoolBalanceChanged(bytes32,address,address[],int256[],uint256[])").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("liquidityProvider", ValueTypes.Address, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("token", ValueTypes.Address, true),
            new("delta", ValueTypes.BigInt, false),
            new("protocolFeeAmount", ValueTypes.BigInt, false)
        ));

    public static readonly EventSchema PoolBalanceManaged = new(
        "BalancerV2", "VaultPoolBalanceManaged",
        Keccak.Compute("PoolBalanceManaged(bytes32,address,address,int256,int256)").BytesToArray(),
        Combine(
            new("poolId", ValueTypes.Bytes, true),
            new("assetManager", ValueTypes.Address, true),
            new("token", ValueTypes.Address, true),
            new("cashDelta", ValueTypes.BigInt, false),
            new("managedDelta", ValueTypes.BigInt, false)
        ));

    // ─────────────────────────────────────────────────────────────────────
    public DatabaseSchema()
    {
        // Authorizer / Pause / Relayer
        AddMappings<AuthorizerChangedEvt>("BalancerV2", "VaultAuthorizerChanged", AuthorizerChanged,
            [("newAuthorizer", e => e.NewAuthorizer)]);

        AddMappings<PausedStateChangedEvt>("BalancerV2", "VaultPausedStateChanged", PausedStateChanged,
            [("paused", e => e.Paused)]);

        AddMappings<RelayerApprovalChangedEvt>("BalancerV2", "VaultRelayerApprovalChanged", RelayerApprovalChanged,
        [
            ("relayer", e => e.Relayer),
            ("sender", e => e.Sender),
            ("approved", e => e.Approved)
        ]);

        // Internal / External balances
        AddMappings<InternalBalanceChangedEvt>("BalancerV2", "VaultInternalBalanceChanged", InternalBalanceChanged,
        [
            ("user", e => e.User),
            ("token", e => e.Token),
            ("delta", e => (BigInteger)e.Delta)
        ]);

        AddMappings<ExternalBalanceTransferEvt>("BalancerV2", "VaultExternalBalanceTransfer", ExternalBalanceTransfer,
        [
            ("token", e => e.Token),
            ("sender", e => e.Sender),
            ("recipient", e => e.Recipient),
            ("amount", e => (BigInteger)e.Amount)
        ]);

        // Flash-loan & Swap
        AddMappings<FlashLoanEvt>("BalancerV2", "VaultFlashLoan", FlashLoan,
        [
            ("recipient", e => e.Recipient),
            ("token", e => e.Token),
            ("amount", e => (BigInteger)e.Amount),
            ("feeAmount", e => (BigInteger)e.FeeAmount)
        ]);

        AddMappings<SwapEvt>("BalancerV2", "VaultSwap", Swap,
        [
            ("poolId", e => e.PoolId),
            ("tokenIn", e => e.TokenIn),
            ("tokenOut", e => e.TokenOut),
            ("amountIn", e => (BigInteger)e.AmountIn),
            ("amountOut", e => (BigInteger)e.AmountOut)
        ]);

        // Pools & tokens
        AddMappings<PoolRegisteredEvt>("BalancerV2", "VaultPoolRegistered", PoolRegistered,
        [
            ("poolId", e => e.PoolId),
            ("poolAddress", e => e.PoolAddress),
            ("specialization", e => e.Specialization)
        ]);

        AddMappings<TokensRegisteredEvt>("BalancerV2", "VaultTokensRegistered", TokensRegistered,
        [
            ("poolId", e => e.PoolId),
            ("batchIndex", e => e.BatchIndex),
            ("token", e => e.Token),
            ("assetManager", e => e.AssetManager)
        ]);

        AddMappings<TokensDeregisteredEvt>("BalancerV2", "VaultTokensDeregistered", TokensDeregistered,
        [
            ("poolId", e => e.PoolId),
            ("batchIndex", e => e.BatchIndex),
            ("token", e => e.Token)
        ]);

        // Pool balances
        AddMappings<PoolBalanceChangedEvt>("BalancerV2", "VaultPoolBalanceChanged", PoolBalanceChanged,
        [
            ("poolId", e => e.PoolId),
            ("liquidityProvider", e => e.LiquidityProvider),
            ("batchIndex", e => e.BatchIndex),
            ("token", e => e.Token),
            ("delta", e => (BigInteger)e.Delta),
            ("protocolFeeAmount", e => (BigInteger)e.ProtocolFeeAmount)
        ]);

        AddMappings<PoolBalanceManagedEvt>("BalancerV2", "VaultPoolBalanceManaged", PoolBalanceManaged,
        [
            ("poolId", e => e.PoolId),
            ("assetManager", e => e.AssetManager),
            ("token", e => e.Token),
            ("cashDelta", e => (BigInteger)e.CashDelta),
            ("managedDelta", e => (BigInteger)e.ManagedDelta)
        ]);
    }
}
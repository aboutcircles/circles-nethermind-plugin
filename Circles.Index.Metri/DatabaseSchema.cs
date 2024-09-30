using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.Metri;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema ModuleProxyCreation = EventSchema.FromSolidity("Metri",
        "event ModuleProxyCreation(address indexed proxy, address indexed masterCopy)");

    public static readonly EventSchema ProxyCreation = EventSchema.FromSolidity("Metri",
        "event ProxyCreation(address proxy, address singleton)");

    public static readonly EventSchema OwnershipTransferred = EventSchema.FromSolidity("Metri",
        "event OwnershipTransferred(address indexed from, address indexed to)");

    public static readonly EventSchema GnosisPayOGNftTransfer = EventSchema.FromSolidity("Metri",
        "event Transfer(address indexed from, address indexed to, uint256 indexed tokenId)");

    public static readonly EventSchema Erc20Transfer = EventSchema.FromSolidity("Metri",
        "event Transfer(address indexed from, address indexed to, uint256 value)");

    public static readonly EventSchema ExecutionSuccess =
        EventSchema.FromSolidity("Metri", "event ExecutionSuccess(bytes32 txHash, uint256 payment)");

    public static readonly EventSchema ExecutionFailure =
        EventSchema.FromSolidity("Metri", "event ExecutionFailure(bytes32 txHash, uint256 payment)");

    public static readonly EventSchema SafeMultiSigTransaction = EventSchema.FromSolidity("Metri",
        "event SafeMultiSigTransaction(address to, uint256 value, bytes data, uint8 operation, uint256 safeTxGas, uint256 baseGas, uint256 gasPrice, address gasToken, address refundReceiver, bytes signatures, bytes additionalInfo)");

    public static readonly EventSchema SafeReceived =
        EventSchema.FromSolidity("Metri", "event SafeReceived(address indexed sender, uint256 value)");

    public static readonly EventSchema SafeSetup = EventSchema.FromSolidity("Metri",
        "event SafeSetup(address indexed initiator, address[] owners, uint256 threshold, address initializer, address fallbackHandler)");

    public static readonly EventSchema RemovedOwner =
        EventSchema.FromSolidity("Metri", "event RemovedOwner(address owner)");

    public static readonly EventSchema GPv2Settlement = EventSchema.FromSolidity("Metri",
        "event Trade(address indexed owner, address sellToken, address buyToken, uint256 sellAmount, uint256 buyAmount, uint256 feeAmount, bytes orderUid)");

    // public static readonly EventSchema CoWSwapEthFlow = EventSchema.FromSolidity("Metri",
    //     "event OrderPlacement(address indexed sender, (address,address,address,uint256,uint256,uint32,bytes32,uint256,bytes32,bool,bytes32,bytes32) order, (uint8,bytes) signature, bytes data)");

    public static readonly EventSchema XDaiTransfer = new EventSchema("Metri", "XdaiTransfer", new byte[32],
    [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("from", ValueTypes.Address, true),
        new("to", ValueTypes.Address, true),
        new("value", ValueTypes.BigInt, false)
    ]);

    static DatabaseSchema()
    {
        GnosisPayOGNftTransfer.Table = "GnosisPayOGNftTransfer";
        Erc20Transfer.Table = "Erc20Transfer";
        GPv2Settlement.Table = "GPv2Settlement";
    }

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("Metri", "ProxyCreation"),
                ProxyCreation
            },
            {
                ("Metri", "ModuleProxyCreation"),
                ModuleProxyCreation
            },
            {
                ("Metri", "OwnershipTransferred"),
                OwnershipTransferred
            },
            {
                ("Metri", "GnosisPayOGNftTransfer"),
                GnosisPayOGNftTransfer
            },
            {
                ("Metri", "Erc20Transfer"),
                Erc20Transfer
            },
            {
                ("Metri", "XdaiTransfer"),
                XDaiTransfer
            },
            {
                ("Metri", "ExecutionSuccess"),
                ExecutionSuccess
            },
            {
                ("Metri", "ExecutionFailure"),
                ExecutionFailure
            },
            {
                ("Metri", "SafeMultiSigTransaction"),
                SafeMultiSigTransaction
            },
            {
                ("Metri", "SafeReceived"),
                SafeReceived
            },
            {
                ("Metri", "SafeSetup"),
                SafeSetup
            },
            {
                ("Metri", "RemovedOwner"),
                RemovedOwner
            },
            {
                ("Metri", "GPv2Settlement"),
                GPv2Settlement
            },
            // {
            //     ("Metri", "CoWSwapEthFlow"),
            //     CoWSwapEthFlow
            // }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<ProxyCreation>(("Metri", "ProxyCreation"));
        SchemaPropertyMap.Add(("Metri", "ProxyCreation"),
            new Dictionary<string, Func<ProxyCreation, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "proxy", e => e.Proxy },
                { "singleton", e => e.Singleton }
            });

        EventDtoTableMap.Add<ModuleProxyCreation>(("Metri", "ModuleProxyCreation"));
        SchemaPropertyMap.Add(("Metri", "ModuleProxyCreation"),
            new Dictionary<string, Func<ModuleProxyCreation, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "proxy", e => e.Proxy },
                { "masterCopy", e => e.MasterCopy }
            });

        EventDtoTableMap.Add<OwnershipTransferred>(("Metri", "OwnershipTransferred"));
        SchemaPropertyMap.Add(("Metri", "OwnershipTransferred"),
            new Dictionary<string, Func<OwnershipTransferred, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "from", e => e.From },
                { "to", e => e.To }
            });

        EventDtoTableMap.Add<GnosisPayOGNftTransfer>(("Metri", "GnosisPayOGNftTransfer"));
        SchemaPropertyMap.Add(("Metri", "GnosisPayOGNftTransfer"),
            new Dictionary<string, Func<GnosisPayOGNftTransfer, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "from", e => e.From },
                { "to", e => e.To },
                { "tokenId", e => (BigInteger)e.TokenId }
            });

        EventDtoTableMap.Add<Erc20Transfer>(("Metri", "Erc20Transfer"));
        SchemaPropertyMap.Add(("Metri", "Erc20Transfer"),
            new Dictionary<string, Func<Erc20Transfer, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "from", e => e.From },
                { "to", e => e.To },
                { "value", e => (BigInteger)e.Value }
            });

        EventDtoTableMap.Add<XDaiTransfer>(("Metri", "XdaiTransfer"));
        SchemaPropertyMap.Add(("Metri", "XdaiTransfer"),
            new Dictionary<string, Func<XDaiTransfer, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "from", e => e.From },
                { "to", e => e.To },
                { "value", e => (BigInteger)e.Value }
            });

        EventDtoTableMap.Add<ExecutionSuccess>(("Metri", "ExecutionSuccess"));
        SchemaPropertyMap.Add(("Metri", "ExecutionSuccess"),
            new Dictionary<string, Func<ExecutionSuccess, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "txHash", e => e.TxHash },
                { "payment", e => (BigInteger)e.Payment }
            });

        EventDtoTableMap.Add<ExecutionFailure>(("Metri", "ExecutionFailure"));
        SchemaPropertyMap.Add(("Metri", "ExecutionFailure"),
            new Dictionary<string, Func<ExecutionFailure, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "txHash", e => e.TxHash },
                { "payment", e => (BigInteger)e.Payment }
            });

        EventDtoTableMap.Add<SafeMultiSigTransaction>(("Metri", "SafeMultiSigTransaction"));
        SchemaPropertyMap.Add(("Metri", "SafeMultiSigTransaction"),
            new Dictionary<string, Func<SafeMultiSigTransaction, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "to", e => e.To },
                { "value", e => (BigInteger)e.Value },
                { "data", e => e.Data },
                { "operation", e => (long)e.Operation },
                { "safeTxGas", e => (BigInteger)e.SafeTxGas },
                { "baseGas", e => (BigInteger)e.BaseGas },
                { "gasPrice", e => (BigInteger)e.GasPrice },
                { "gasToken", e => e.GasToken },
                { "refundReceiver", e => e.RefundReceiver },
                { "signatures", e => e.Signatures },
                { "additionalInfo", e => e.AdditionalInfo }
            });

        EventDtoTableMap.Add<SafeReceived>(("Metri", "SafeReceived"));
        SchemaPropertyMap.Add(("Metri", "SafeReceived"),
            new Dictionary<string, Func<SafeReceived, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "sender", e => e.Sender },
                { "value", e => (BigInteger)e.Value }
            });

        EventDtoTableMap.Add<SafeSetup>(("Metri", "SafeSetup"));
        SchemaPropertyMap.Add(("Metri", "SafeSetup"),
            new Dictionary<string, Func<SafeSetup, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "initiator", e => e.Initiator },
                { "owners", e => e.Owners },
                { "threshold", e => (BigInteger)e.Threshold },
                { "initializer", e => e.Initializer },
                { "fallbackHandler", e => e.FallbackHandler }
            });

        EventDtoTableMap.Add<RemovedOwner>(("Metri", "RemovedOwner"));
        SchemaPropertyMap.Add(("Metri", "RemovedOwner"),
            new Dictionary<string, Func<RemovedOwner, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "owner", e => e.Owner }
            });

        EventDtoTableMap.Add<GPv2Settlement>(("Metri", "GPv2Settlement"));
        SchemaPropertyMap.Add(("Metri", "GPv2Settlement"),
            new Dictionary<string, Func<GPv2Settlement, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "owner", e => e.Owner },
                { "sellToken", e => e.SellToken },
                { "buyToken", e => e.BuyToken },
                { "sellAmount", e => (BigInteger)e.SellAmount },
                { "buyAmount", e => (BigInteger)e.BuyAmount },
                { "feeAmount", e => (BigInteger)e.FeeAmount },
                { "orderUid", e => e.OrderUid }
            });

        // EventDtoTableMap.Add<CoWSwapEthFlow>(("Metri", "CoWSwapEthFlow"));
        // SchemaPropertyMap.Add(("Metri", "CoWSwapEthFlow"),
        //     new Dictionary<string, Func<CoWSwapEthFlow, object?>>
        //     {
        //         { "blockNumber", e => e.BlockNumber },
        //         { "timestamp", e => e.Timestamp },
        //         { "transactionIndex", e => e.TransactionIndex },
        //         { "logIndex", e => e.LogIndex },
        //         { "transactionHash", e => e.TransactionHash },
        //         { "sender", e => e.Sender },
        //         { "order", e => e.Order },
        //         { "signature", e => e.Signature },
        //         { "data", e => e.Data }
        //     });
    }
}
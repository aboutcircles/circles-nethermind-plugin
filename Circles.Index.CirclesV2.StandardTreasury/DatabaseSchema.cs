using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV2.StandardTreasury;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema CreateVault =
        EventSchema.FromSolidity("CrcV2",
            "event CreateVault(address indexed group, address indexed vault)");

    public static readonly EventSchema CollateralLockedSingle =
        EventSchema.FromSolidity("CrcV2",
            "event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData)");

    public static readonly EventSchema CollateralLockedBatch =
        new EventSchema("CrcV2", "CollateralLockedBatch",
            Keccak.Compute("CollateralLockedBatch(address,uint256[],uint256[],bytes)").BytesToArray(),
            new List<EventFieldSchema>()
            {
                new("blockNumber", ValueTypes.Int, true),
                new("timestamp", ValueTypes.Int, true),
                new("transactionIndex", ValueTypes.Int, true),
                new("logIndex", ValueTypes.Int, true),
                new("batchIndex", ValueTypes.Int, true, true),
                new("transactionHash", ValueTypes.String, true),
                new("group", ValueTypes.String, true),
                new("id", ValueTypes.BigInt, true),
                new("value", ValueTypes.BigInt, true),
                new("userData", ValueTypes.Bytes, true)
            });

    public static readonly EventSchema GroupRedeem =
        EventSchema.FromSolidity("CrcV2",
            "event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data)");

    public static readonly EventSchema GroupRedeemCollateralReturn =
        new EventSchema("CrcV2", "GroupRedeemCollateralReturn",
            Keccak.Compute("GroupRedeemCollateralReturn(address,address,uint256[],uint256[])").BytesToArray(),
            new List<EventFieldSchema>()
            {
                new("blockNumber", ValueTypes.Int, true),
                new("timestamp", ValueTypes.Int, true),
                new("transactionIndex", ValueTypes.Int, true),
                new("logIndex", ValueTypes.Int, true),
                new("batchIndex", ValueTypes.Int, true, true),
                new("transactionHash", ValueTypes.String, true),
                new("group", ValueTypes.String, true),
                new("to", ValueTypes.String, true),
                new("id", ValueTypes.BigInt, true),
                new("value", ValueTypes.BigInt, true)
            });

    public static readonly EventSchema GroupRedeemCollateralBurn =
        new EventSchema("CrcV2", "GroupRedeemCollateralBurn",
            Keccak.Compute("GroupRedeemCollateralBurn(address,uint256[],uint256[])").BytesToArray(),
            new List<EventFieldSchema>()
            {
                new("blockNumber", ValueTypes.Int, true),
                new("timestamp", ValueTypes.Int, true),
                new("transactionIndex", ValueTypes.Int, true),
                new("logIndex", ValueTypes.Int, true),
                new("batchIndex", ValueTypes.Int, true, true),
                new("transactionHash", ValueTypes.String, true),
                new("group", ValueTypes.String, true),
                new("id", ValueTypes.BigInt, true),
                new("value", ValueTypes.BigInt, true)
            });

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("CrcV2", "CreateVault"),
                CreateVault
            },
            {
                ("CrcV2", "CollateralLockedSingle"),
                CollateralLockedSingle
            },
            {
                ("CrcV2", "CollateralLockedBatch"),
                CollateralLockedBatch
            },
            {
                ("CrcV2", "GroupRedeem"),
                GroupRedeem
            },
            {
                ("CrcV2", "GroupRedeemCollateralReturn"),
                GroupRedeemCollateralReturn
            },
            {
                ("CrcV2", "GroupRedeemCollateralBurn"),
                GroupRedeemCollateralBurn
            }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CreateVault>(("CrcV2", "CreateVault"));
        SchemaPropertyMap.Add(("CrcV2", "CreateVault"),
            new Dictionary<string, Func<CreateVault, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "group", e => e.Group },
                { "vault", e => e.Vault }
            });

        EventDtoTableMap.Add<CollateralLockedSingle>(("CrcV2", "CollateralLockedSingle"));
        SchemaPropertyMap.Add(("CrcV2", "CollateralLockedSingle"),
            new Dictionary<string, Func<CollateralLockedSingle, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "group", e => e.Group },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value },
                { "userData", e => e.UserData }
            });

        EventDtoTableMap.Add<CollateralLockedBatch>(("CrcV2", "CollateralLockedBatch"));
        SchemaPropertyMap.Add(("CrcV2", "CollateralLockedBatch"),
            new Dictionary<string, Func<CollateralLockedBatch, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "batchIndex", e => e.BatchIndex },
                { "group", e => e.Group },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value },
                { "userData", e => e.UserData }
            });

        EventDtoTableMap.Add<GroupRedeem>(("CrcV2", "GroupRedeem"));
        SchemaPropertyMap.Add(("CrcV2", "GroupRedeem"),
            new Dictionary<string, Func<GroupRedeem, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "group", e => e.Group },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value },
                { "data", e => e.Data }
            });

        EventDtoTableMap.Add<GroupRedeemCollateralReturn>(("CrcV2", "GroupRedeemCollateralReturn"));
        SchemaPropertyMap.Add(("CrcV2", "GroupRedeemCollateralReturn"),
            new Dictionary<string, Func<GroupRedeemCollateralReturn, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "batchIndex", e => e.BatchIndex },
                { "group", e => e.Group },
                { "to", e => e.To },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value }
            });

        EventDtoTableMap.Add<GroupRedeemCollateralBurn>(("CrcV2", "GroupRedeemCollateralBurn"));
        SchemaPropertyMap.Add(("CrcV2", "GroupRedeemCollateralBurn"),
            new Dictionary<string, Func<GroupRedeemCollateralBurn, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "batchIndex", e => e.BatchIndex },
                { "group", e => e.Group },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value }
            });
    }
}
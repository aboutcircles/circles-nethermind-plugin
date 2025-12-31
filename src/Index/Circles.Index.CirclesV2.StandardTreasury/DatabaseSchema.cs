using System.Numerics;
using Circles.Common;

namespace Circles.Index.CirclesV2.StandardTreasury;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema CreateVault = EventSchema.FromSolidity(
        "CrcV2",
        "event CreateVault(address indexed group, address indexed vault)"
    );

    public static readonly EventSchema CollateralLockedSingle = EventSchema.FromSolidity(
        "CrcV2",
        "event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData)"
    );

    public static readonly EventSchema CollateralLockedBatch = new(
        "CrcV2",
        "CollateralLockedBatch",
        KeccakHelper.ComputeHash("CollateralLockedBatch(address,uint256[],uint256[],bytes)"),
        new List<EventFieldSchema>
        {
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("group", ValueTypes.String, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("userData", ValueTypes.Bytes, false)
        }
    );

    public static readonly EventSchema GroupRedeem = EventSchema.FromSolidity(
        "CrcV2",
        "event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data)"
    );

    public static readonly EventSchema GroupRedeemCollateralReturn = new(
        "CrcV2",
        "GroupRedeemCollateralReturn",
        KeccakHelper.ComputeHash("GroupRedeemCollateralReturn(address,address,uint256[],uint256[])"),
        new List<EventFieldSchema>
        {
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("group", ValueTypes.String, true),
            new("to", ValueTypes.String, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false)
        }
    );

    public static readonly EventSchema GroupRedeemCollateralBurn = new(
        "CrcV2",
        "GroupRedeemCollateralBurn",
        KeccakHelper.ComputeHash("GroupRedeemCollateralBurn(address,uint256[],uint256[])"),
        new List<EventFieldSchema>
        {
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("group", ValueTypes.String, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false)
        }
    );

    public DatabaseSchema()
    {
        AddMappings<CreateVault>(
            ns: "CrcV2",
            table: "CreateVault",
            eventSchema: CreateVault,
            databaseFieldMap:
            [
                ("group", e => e.Group),
                ("vault", e => e.Vault)
            ]
        );

        AddMappings<CollateralLockedSingle>(
            ns: "CrcV2",
            table: "CollateralLockedSingle",
            eventSchema: CollateralLockedSingle,
            databaseFieldMap:
            [
                ("group", e => e.Group),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value),
                ("userData", e => e.UserData)
            ]
        );

        AddMappings<CollateralLockedBatch>(
            ns: "CrcV2",
            table: "CollateralLockedBatch",
            eventSchema: CollateralLockedBatch,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("group", e => e.Group),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value),
                ("userData", e => e.UserData)
            ]
        );

        AddMappings<GroupRedeem>(
            ns: "CrcV2",
            table: "GroupRedeem",
            eventSchema: GroupRedeem,
            databaseFieldMap:
            [
                ("group", e => e.Group),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value),
                ("data", e => e.Data)
            ]
        );

        AddMappings<GroupRedeemCollateralReturn>(
            ns: "CrcV2",
            table: "GroupRedeemCollateralReturn",
            eventSchema: GroupRedeemCollateralReturn,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("group", e => e.Group),
                ("to", e => e.To),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value)
            ]
        );

        AddMappings<GroupRedeemCollateralBurn>(
            ns: "CrcV2",
            table: "GroupRedeemCollateralBurn",
            eventSchema: GroupRedeemCollateralBurn,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("group", e => e.Group),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value)
            ]
        );
    }
}
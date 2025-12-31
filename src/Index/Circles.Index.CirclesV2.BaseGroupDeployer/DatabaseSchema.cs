using Circles.Common;

namespace Circles.Index.CirclesV2.BaseGroupDeployer;

/// @notice Emitted when a new BaseGroup is created.
/// @param group BaseGroup address.
/// @param owner Owner of the new BaseGroup.
/// @param mintHandler Address of the group mint handler contract.
/// @param treasury Address of the group treasury contract.
// event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury);
public record BaseGroupCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string Owner,
    string MintHandler,
    string Treasury) : IIndexEvent;

public record OwnerUpdated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Owner) : IIndexEvent;

public record ServiceUpdated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string NewService
) : IIndexEvent;

public record FeeCollectionUpdated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string FeeCollection
) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema BaseGroupCreated = EventSchema.FromSolidity("CrcV2",
        "event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury)");

    // public static readonly EventSchema OwnerUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event OwnerUpdated(address indexed owner)", "BaseGroupOwnerUpdated");

    public static readonly EventSchema OwnerUpdated = new("CrcV2", "BaseGroupOwnerUpdated",
        KeccakHelper.ComputeHash("OwnerUpdated(address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("owner", ValueTypes.String, true)
        ]);

    // public static readonly EventSchema ServiceUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event ServiceUpdated(address indexed newService)", "BaseGroupServiceUpdated");

    public static readonly EventSchema ServiceUpdated = new("CrcV2", "BaseGroupServiceUpdated",
        KeccakHelper.ComputeHash("ServiceUpdated(address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true, true),
            new("emitter", ValueTypes.String, true),
            new("newService", ValueTypes.String, true),
        ]);

    // public static readonly EventSchema FeeCollectionUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event FeeCollectionUpdated(address indexed feeCollection)", "BaseGroupFeeCollectionUpdated");

    public static readonly EventSchema FeeCollectionUpdated = new("CrcV2", "BaseGroupFeeCollectionUpdated",
        KeccakHelper.ComputeHash("FeeCollectionUpdated(address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("feeCollection", ValueTypes.String, true)
        ]);

    public DatabaseSchema()
    {
        AddMappings<BaseGroupCreated>(
            ns: "CrcV2",
            table: "BaseGroupCreated",
            eventSchema: BaseGroupCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("owner", e => e.Owner),
                ("mintHandler", e => e.MintHandler),
                ("treasury", e => e.Treasury)
            ]
        );

        AddMappings<OwnerUpdated>(
            ns: "CrcV2",
            table: "BaseGroupOwnerUpdated",
            eventSchema: OwnerUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("owner", e => e.Owner),
            ]
        );

        AddMappings<ServiceUpdated>(
            ns: "CrcV2",
            table: "BaseGroupServiceUpdated",
            eventSchema: ServiceUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("newService", e => e.NewService),
            ]
        );

        AddMappings<FeeCollectionUpdated>(
            ns: "CrcV2",
            table: "BaseGroupFeeCollectionUpdated",
            eventSchema: FeeCollectionUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("feeCollection", e => e.FeeCollection),
            ]
        );
    }
}
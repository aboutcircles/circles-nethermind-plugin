using System.Collections.Concurrent;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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
        Keccak.Compute("OwnerUpdated(address)").BytesToArray(), [
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
        Keccak.Compute("ServiceUpdated(address)").BytesToArray(), [
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
        Keccak.Compute("FeeCollectionUpdated(address)").BytesToArray(), [
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

public class LogParser(Address deployerAddress) : ILogParser
{
    public static readonly Hash256 BaseGroupCreatedTopic = new(DatabaseSchema.BaseGroupCreated.Topic);
    public static readonly Hash256 OwnerUpdatedTopic = new(DatabaseSchema.OwnerUpdated.Topic);
    public static readonly Hash256 ServiceUpdatedTopic = new(DatabaseSchema.ServiceUpdated.Topic);
    public static readonly Hash256 FeeCollectionUpdatedTopic = new(DatabaseSchema.FeeCollectionUpdated.Topic);

    public static readonly ConcurrentDictionary<Address, object?> BaseGroupsCreated = new();

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        var groupsCreatedInTx = events.OfType<BaseGroupCreated>().ToDictionary(o => o.Group);

        if (groupsCreatedInTx.Count == 0)
        {
            yield break;
        }

        // Find the associated initial OwnerUpdated, ServiceUpdated and FeeCollectionUpdated events.
        // For new groups, these are emitted before the BaseGroupCreated event and thus they aren't
        // picked up in ParseLog().
        for (var index = 0; index < receipt.Logs.Length; index++)
        {
            var log = receipt.Logs[index];
            if (!groupsCreatedInTx.ContainsKey(log.Address.ToString(true, false)))
            {
                // Skip for all events that weren't emitted by newly created groups.
                continue;
            }

            if (log.Topics[0] == OwnerUpdatedTopic)
            {
                yield return OwnerUpdated(block, receipt, log, index);
            }
            else if (log.Topics[0] == ServiceUpdatedTopic)
            {
                yield return ServiceUpdated(block, receipt, log, index);
            }
            else if (log.Topics[0] == FeeCollectionUpdatedTopic)
            {
                yield return FeeCollectionUpdated(block, receipt, log, index);
            }
        }
    }

    public IEnumerable<IIndexEvent> ParseLog(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0 || deployerAddress != log.Address)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == BaseGroupCreatedTopic)
        {
            yield return BaseGroupCreated(block, receipt, log, logIndex);
        }

        if (BaseGroupsCreated.ContainsKey(log.Address))
        {
            if (topic == OwnerUpdatedTopic)
            {
                yield return OwnerUpdated(block, receipt, log, logIndex);
            }
            else if (topic == ServiceUpdatedTopic)
            {
                yield return ServiceUpdated(block, receipt, log, logIndex);
            }
            else if (topic == FeeCollectionUpdatedTopic)
            {
                yield return FeeCollectionUpdated(block, receipt, log, logIndex);
            }
        }
    }


    // public static readonly EventSchema OwnerUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event OwnerUpdated(address indexed owner)");
    private OwnerUpdated OwnerUpdated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new OwnerUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            owner);
    }

    //
    // public static readonly EventSchema ServiceUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event ServiceUpdated(address indexed newService)");
    private ServiceUpdated ServiceUpdated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string service = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new ServiceUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            service);
    }

    // public static readonly EventSchema FeeCollectionUpdated = EventSchema.FromSolidity("CrcV2",
    //     "event FeeCollectionUpdated(address indexed feeCollection)");
    private FeeCollectionUpdated FeeCollectionUpdated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string fees = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new FeeCollectionUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            fees);
    }

    // event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury);
    private BaseGroupCreated BaseGroupCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string mintHandler = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        string treasury = new Address(log.Data.Slice(12, 20)).ToString(true, false);

        BaseGroupsCreated.TryAdd(new Address(group), null);

        return new BaseGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            group,
            owner,
            mintHandler,
            treasury
        );
    }
}
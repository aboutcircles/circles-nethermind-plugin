using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.BaseGroupDeployer;

public class LogParser(Address deployerAddress) : ILogParser
{
    public static readonly Hash256 BaseGroupCreatedTopic = new(DatabaseSchema.BaseGroupCreated.Topic);
    public static readonly Hash256 OwnerUpdatedTopic = new(DatabaseSchema.OwnerUpdated.Topic);
    public static readonly Hash256 ServiceUpdatedTopic = new(DatabaseSchema.ServiceUpdated.Topic);
    public static readonly Hash256 FeeCollectionUpdatedTopic = new(DatabaseSchema.FeeCollectionUpdated.Topic);

    public static readonly RollbackCache<Address, object?> BaseGroupsCreated = new("BaseGroupsCreated");
    public IRollbackCache[] Caches { get; } = [BaseGroupsCreated];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        if (settings.BaseGroupDeployer != null)
        {
            var baseGroupsQuery = new Select("CrcV2", "BaseGroupCreated", ["group"], [], [], int.MaxValue, false,
                int.MaxValue);
            var sql = baseGroupsQuery.ToSql(database);
            var result = database.Select(sql);
            var rows = result.Rows.ToArray();

            var seed = new Dictionary<Address, object?>(rows.Length + 25_000);
            foreach (var row in rows)
            {
                var group = new Address(row[0]?.ToString() ?? throw new InvalidOperationException("Group address is null"));
                seed[group] = null;
            }

            BaseGroupsCreated.Seed(seed);

            logger.Info($" * Cached {seed.Count} BaseGroupCreated events");
        }

        return Task.CompletedTask;
    }


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
        for (var index = 0; index < receipt.Logs!.Length; index++)
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
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (log.Address == deployerAddress && topic == BaseGroupCreatedTopic)
        {
            yield return BaseGroupCreated(block, receipt, log, logIndex);
        }
        else if (BaseGroupsCreated.ContainsKey(log.Address))
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

        BaseGroupsCreated.Add(block.Number, new Address(group), null);

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

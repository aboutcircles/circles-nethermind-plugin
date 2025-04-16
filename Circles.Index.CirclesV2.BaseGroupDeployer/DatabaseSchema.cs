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

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema BaseGroupCreated = EventSchema.FromSolidity("CrcV2",
        "event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury)");

    // public static readonly EventSchema BaseGroupCreated = new("CrcV2", "BaseGroupCreated",
    //     new byte[32], [
    //         new("blockNumber", ValueTypes.Int, true, true),
    //         new("timestamp", ValueTypes.Int, true),
    //         new("transactionIndex", ValueTypes.Int, true, true),
    //         new("logIndex", ValueTypes.Int, true, true),
    //         new("transactionHash", ValueTypes.String, true),
    //         new("emitter", ValueTypes.String, true),
    //         new("group", ValueTypes.String, true),
    //         new("owner", ValueTypes.String, true),
    //         new("mintHandler", ValueTypes.String, true),
    //         new("treasury", ValueTypes.String, true)
    //     ]);

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
    }
}

public class LogParser(Address deployerAddress) : ILogParser
{
    public static readonly Hash256 BaseGroupCreatedTopic = new(DatabaseSchema.BaseGroupCreated.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
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
    }

    // event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury);
    private BaseGroupCreated BaseGroupCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string mintHandler = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        string treasury = new Address(log.Data.Slice(12, 20)).ToString(true, false);

        return new BaseGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            proxy,
            owner,
            mintHandler,
            treasury
        );
    }
}
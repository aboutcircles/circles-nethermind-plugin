using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Circles.Index.CirclesV2.CMGroupDeployer;

public record CMGroupCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Proxy,
    string Owner,
    string MintHandler,
    string RedemptionHandler) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema CMGroupCreated = EventSchema.FromSolidity("CrcV2",
        "event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler)");

    public DatabaseSchema()
    {
        AddMappings<CMGroupCreated>(
            ns: "CrcV2",
            table: "CMGroupCreated",
            eventSchema: CMGroupCreated,
            databaseFieldMap:
            [
                ("proxy", e => e.Proxy),
                ("owner", e => e.Owner),
                ("mintHandler", e => e.MintHandler),
                ("redemptionHandler", e => e.RedemptionHandler)
            ]
        );
    }
}

public class LogParser(Address deployerAddress) : ILogParser
{
    private readonly Hash256 _cmGroupCreatedTopic = new(DatabaseSchema.CMGroupCreated.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
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
        if (log.Topics.Length == 0
            || log.Address != deployerAddress)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == _cmGroupCreatedTopic)
        {
            yield return CMGroupCreated(block, receipt, log, logIndex);
        }
    }

    // event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler);
    private CMGroupCreated CMGroupCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string mintHandler = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        string redemptionHandler = new Address(log.Data.Slice(12)).ToString(true, false);

        return new CMGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            proxy,
            owner,
            mintHandler,
            redemptionHandler
        );
    }
}
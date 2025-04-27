using System.Collections.Immutable;
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
    string RedemptionHandler,
    string LiquidityProvider) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    // public static readonly EventSchema CMGroupCreated = EventSchema.FromSolidity("CrcV2",
    //     "event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler)");
    public static readonly EventSchema CmgRoupCreated = new("CrcV2", "CMGroupCreated",
        new byte[32], [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("proxy", ValueTypes.String, true),
            new("owner", ValueTypes.String, true),
            new("mintHandler", ValueTypes.String, true),
            new("redemptionHandler", ValueTypes.String, true),
            new("liquidityProvider", ValueTypes.String, true)
        ]);

    public DatabaseSchema()
    {
        AddMappings<CMGroupCreated>(
            ns: "CrcV2",
            table: "CMGroupCreated",
            eventSchema: CmgRoupCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("proxy", e => e.Proxy),
                ("owner", e => e.Owner),
                ("mintHandler", e => e.MintHandler),
                ("redemptionHandler", e => e.RedemptionHandler),
                ("liquidityProvider", e => e.LiquidityProvider)
            ]
        );
    }
}

public class LogParser(ImmutableHashSet<Address> deployerAddress) : ILogParser
{
    private readonly Hash256 _cmGroupCreatedTopicNew =
        Keccak.Compute("CMGroupCreated(address,address,address,address,address)");

    private readonly Hash256 _cmGroupCreatedTopicOld =
        Keccak.Compute("CMGroupCreated(address,address,address,address)");

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
        if (log.Topics.Length == 0 || !deployerAddress.Contains(log.Address))
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == _cmGroupCreatedTopicNew || topic == _cmGroupCreatedTopicOld)
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
        string redemptionHandler = new Address(log.Data.Slice(12, 20)).ToString(true, false);
        string liquidityProvider = log.Data.Length == 64 ? new Address(log.Data.Slice(44, 20)).ToString(true, false) : "";

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
            redemptionHandler,
            liquidityProvider
        );
    }
}
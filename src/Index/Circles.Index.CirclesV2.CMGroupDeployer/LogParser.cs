using System.Collections.Immutable;
using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.CMGroupDeployer;

public class LogParser(ImmutableHashSet<Address> deployerAddress) : ILogParser
{
    private readonly byte[] _cmGroupCreatedTopicNew =
        KeccakHelper.ComputeHash("CMGroupCreated(address,address,address,address,address)");

    private readonly byte[] _cmGroupCreatedTopicOld =
        KeccakHelper.ComputeHash("CMGroupCreated(address,address,address,address)");

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        return Task.CompletedTask;
    }

    public IRollbackCache[] Caches { get; } = [];

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

        if (topic.Bytes.SequenceEqual(_cmGroupCreatedTopicNew) || topic.Bytes.SequenceEqual(_cmGroupCreatedTopicOld))
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
        string redemptionHandler = new Address(log.Data.Slice(12, 20)).ToLowerHex();
        string liquidityProvider =
            log.Data.Length == 64 ? new Address(log.Data.Slice(44, 20)).ToLowerHex() : "";

        return new CMGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            proxy,
            owner,
            mintHandler,
            redemptionHandler,
            liquidityProvider
        );
    }
}

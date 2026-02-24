using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.AffiliateGroupRegistry;

/// <summary>
/// Parses logs emitted by the AffiliateGroupRegistry contract.
/// </summary>
public class LogParser(Address registryAddress) : ILogParser
{
    private readonly Hash256 _affiliateGroupChangedTopic = new(DatabaseSchema.AffiliateGroupChanged.Topic);
    private readonly Hash256 _notificationFailedTopic = new(DatabaseSchema.NotificationFailed.Topic);
    private readonly Hash256 _notificationSuccessfulTopic = new(DatabaseSchema.NotificationSuccessful.Topic);

    public IRollbackCache[] Caches { get; } = [];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        return Task.CompletedTask;
    }

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
        bool hasTopics = log.Topics.Length > 0;
        bool isFromRegistry = log.Address == registryAddress;

        if (!hasTopics || !isFromRegistry)
        {
            yield break;
        }

        var topic = log.Topics[0];
        bool isChangedTopic = topic == _affiliateGroupChangedTopic;
        bool isFailedTopic = topic == _notificationFailedTopic;
        bool isSuccessfulTopic = topic == _notificationSuccessfulTopic;

        if (isChangedTopic)
        {
            yield return ParseAffiliateGroupChanged(block, receipt, log, logIndex);
        }
        else if (isFailedTopic)
        {
            yield return ParseNotificationFailed(block, receipt, log, logIndex);
        }
        else if (isSuccessfulTopic)
        {
            yield return ParseNotificationSuccessful(block, receipt, log, logIndex);
        }
    }

    private AffiliateGroupChanged ParseAffiliateGroupChanged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string human = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string oldGroup = new Address(log.Data.Slice(12, 20)).ToLowerHex();
        string newGroup = new Address(log.Data.Slice(44, 20)).ToLowerHex();

        return new AffiliateGroupChanged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            human,
            oldGroup,
            newGroup
        );
    }

    private NotificationFailed ParseNotificationFailed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string human = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new NotificationFailed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            human
        );
    }

    private NotificationSuccessful ParseNotificationSuccessful(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string human = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new NotificationSuccessful(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            human
        );
    }
}

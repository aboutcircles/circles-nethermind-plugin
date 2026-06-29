using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.MultiAffiliateGroupRegistry;

/// <summary>
/// Parses logs emitted by the MultiAffiliateGroupRegistry contract.
/// Both <c>AffiliateGroupAdded(affiliateGroup, avatar)</c> and
/// <c>AffiliateGroupRemoved(affiliateGroup, avatar)</c> declare their parameters as
/// NON-indexed, so both addresses live in <see cref="LogEntry.Data"/> (two 32-byte words) —
/// in contrast to the legacy single-affiliate registry which indexes <c>human</c> in a topic.
/// </summary>
public class LogParser(Address registryAddress) : ILogParser
{
    private readonly Hash256 _affiliateGroupAddedTopic = new(DatabaseSchema.AffiliateGroupAdded.Topic);
    private readonly Hash256 _affiliateGroupRemovedTopic = new(DatabaseSchema.AffiliateGroupRemoved.Topic);

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

        if (topic == _affiliateGroupAddedTopic)
        {
            yield return ParseAffiliateGroupAdded(block, transaction, receipt, log, logIndex);
        }
        else if (topic == _affiliateGroupRemovedTopic)
        {
            yield return ParseAffiliateGroupRemoved(block, receipt, log, logIndex);
        }
    }

    // Seed detection delegates to the Nethermind-free AffiliateGroupSeedDetector (unit-testable).
    private static bool IsInitializeCall(Transaction transaction) =>
        AffiliateGroupSeedDetector.IsInitializeCalldata(transaction.Data);

    private AffiliateGroupAdded ParseAffiliateGroupAdded(Block block, Transaction transaction, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        // Data layout: word 0 = affiliateGroup (address in low 20 bytes), word 1 = avatar.
        string affiliateGroup = new Address(log.Data.Slice(12, 20)).ToLowerHex();
        string avatar = new Address(log.Data.Slice(44, 20)).ToLowerHex();

        return new AffiliateGroupAdded(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            affiliateGroup,
            avatar,
            IsInitializeCall(transaction)
        );
    }

    private AffiliateGroupRemoved ParseAffiliateGroupRemoved(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string affiliateGroup = new Address(log.Data.Slice(12, 20)).ToLowerHex();
        string avatar = new Address(log.Data.Slice(44, 20)).ToLowerHex();

        return new AffiliateGroupRemoved(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            affiliateGroup,
            avatar
        );
    }
}

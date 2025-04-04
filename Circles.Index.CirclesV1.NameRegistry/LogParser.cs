using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV1.NameRegistry;

public class LogParser(Address v1NameRegistryAddress) : ILogParser
{
    private readonly Hash256 _updateMetadataDigestTopic = new(DatabaseSchema.UpdateMetadataDigest.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        if (log.Address != v1NameRegistryAddress)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == _updateMetadataDigestTopic)
        {
            yield return UpdateMetadataDigest(block, receipt, log, logIndex);
        }
    }

    private UpdateMetadataDigest UpdateMetadataDigest(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        byte[] metadataDigest = log.Data;

        return new UpdateMetadataDigest(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            metadataDigest);
    }
}
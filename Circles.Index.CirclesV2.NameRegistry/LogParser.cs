using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.NameRegistry;

public class LogParser(Address nameRegistryAddress) : ILogParser
{
    private readonly Hash256 _registerShortNameTopic = new(DatabaseSchema.RegisterShortName.Topic);
    private readonly Hash256 _updateMetadataDigestTopic = new(DatabaseSchema.UpdateMetadataDigest.Topic);
    private readonly Hash256 _cidV0Topic = new(DatabaseSchema.CidV0.Topic);

    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        if (log.LoggersAddress != nameRegistryAddress)
        {
            yield break;
        }

        Console.WriteLine($"Event from NameRegistry: {log.Topics[0]}");

        var topic = log.Topics[0];

        if (topic == _registerShortNameTopic)
        {
            yield return RegisterShortName(block, receipt, log, logIndex);
        }

        if (topic == _updateMetadataDigestTopic)
        {
            yield return UpdateMetadataDigest(block, receipt, log, logIndex);
        }

        if (topic == _cidV0Topic)
        {
            yield return CidV0(block, receipt, log, logIndex);
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
            avatar,
            metadataDigest);
    }

    private RegisterShortName RegisterShortName(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 shortName = new UInt256(log.Topics[2].Bytes, true);
        UInt256 nonce = new UInt256(log.Topics[3].Bytes, true);

        return new RegisterShortName(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            avatar,
            shortName,
            nonce);
    }

    private CidV0 CidV0(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new CidV0(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            avatar,
            log.Data);
    }
}
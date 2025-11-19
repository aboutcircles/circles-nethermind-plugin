using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Index.CirclesV1.NameRegistry;

public class LogParser(Address v1NameRegistryAddress) : ILogParser
{
    private readonly Hash256 _updateMetadataDigestTopic = new(DatabaseSchema.UpdateMetadataDigest.Topic);

    /// <summary>
    /// Contains always the latest cidV0 for each avatar.
    /// </summary>
    public static readonly RollbackCache<Address, string> V1AvatarToCidMap = new("V1AvatarToCidMap");

    public IRollbackCache[] Caches { get; } = [V1AvatarToCidMap];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        var selectSignups = new Select(
            "CrcV1",
            "UpdateMetadataDigest",
            ["avatar", "metadataDigest"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectSignups.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        var seed = new Dictionary<Address, string>(rows.Length + 25_000);
        foreach (var row in rows)
        {
            var avatar = new Address(row[0]!.ToString()!);
            seed[avatar] = CidHelper.MetadataDigestToCidV0((byte[])row[1]!);
        }

        V1AvatarToCidMap.Seed(seed);

        logger.Info($" * Cached {seed.Count} avatar -> cidV0 mappings from V1 UpdateMetadataDigest");

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

        var cidV0 = CidHelper.MetadataDigestToCidV0(metadataDigest);
        V1AvatarToCidMap.Add(block.Number, new Address(avatar), cidV0);

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
using System.Collections.Immutable;
using Circles.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.ScoreGroup;

public class LogParser(ImmutableHashSet<Address> policyAddresses) : ILogParser
{
    private static readonly Hash256 GroupInitializedTopic = new(DatabaseSchema.GroupInitialized.Topic);
    private static readonly Hash256 MerkleRootUpdatedTopic = new(DatabaseSchema.MerkleRootUpdated.Topic);
    private static readonly Hash256 HistoricalSupplyTopic = new(DatabaseSchema.HistoricalSupply.Topic);
    private static readonly Hash256 PersonalMintedTopic = new(DatabaseSchema.PersonalMinted.Topic);
    private static readonly Hash256 RouterMintedTopic = new(DatabaseSchema.RouterMinted.Topic);

    public static readonly RollbackCache<Address, object?> ScoreGroupPolicies = new("ScoreGroupPolicies");

    public IRollbackCache[] Caches { get; } = [ScoreGroupPolicies];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        var seed = policyAddresses.ToDictionary(a => a, _ => (object?)null);

        var query = new Select("CrcV2_ScoreGroup", "GroupInitialized", ["emitter"], [], [], int.MaxValue, false,
            int.MaxValue);
        foreach (var row in database.Select(query.ToSql(database)).Rows)
        {
            seed[new Address(row[0]?.ToString() ?? throw new InvalidOperationException("Score group policy address is null"))] = null;
        }

        ScoreGroupPolicies.Seed(seed);
        logger.Info($" * Cached {seed.Count} score group mint policy contracts");

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
        if (log.Topics.Length == 0 || !ScoreGroupPolicies.ContainsKey(log.Address))
        {
            yield break;
        }

        var topic = log.Topics[0];
        if (topic == GroupInitializedTopic)
        {
            yield return ParseGroupInitialized(block, receipt, log, logIndex);
        }
        else if (topic == MerkleRootUpdatedTopic)
        {
            yield return ParseMerkleRootUpdated(block, receipt, log, logIndex);
        }
        else if (topic == HistoricalSupplyTopic)
        {
            yield return ParseHistoricalSupply(block, receipt, log, logIndex);
        }
        else if (topic == PersonalMintedTopic)
        {
            yield return ParsePersonalMinted(block, receipt, log, logIndex);
        }
        else if (topic == RouterMintedTopic)
        {
            yield return ParseRouterMinted(block, receipt, log, logIndex);
        }
    }

    private static GroupInitialized ParseGroupInitialized(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var manager = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var router = new Address(log.Data.Slice(12, 20)).ToLowerHex();

        return new GroupInitialized(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            manager,
            router);
    }

    private static MerkleRootUpdated ParseMerkleRootUpdated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new MerkleRootUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            log.Data.ToArray());
    }

    private static HistoricalSupply ParseHistoricalSupply(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var data = log.Data.AsSpan();

        return new HistoricalSupply(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            Uint(log.Topics[1].Bytes),
            Uint(data.Slice(0, 32)),
            Uint(data.Slice(32, 32)));
    }

    private static PersonalMinted ParsePersonalMinted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data.AsSpan();

        return new PersonalMinted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            Uint(log.Topics[2].Bytes),
            Uint(data.Slice(0, 32)),
            Uint(data.Slice(32, 32)),
            Uint(data.Slice(64, 32)),
            Uint(data.Slice(96, 32)));
    }

    private static RouterMinted ParseRouterMinted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data.AsSpan();

        return new RouterMinted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            group,
            Uint(log.Topics[2].Bytes),
            Uint(data.Slice(0, 32)),
            Uint(data.Slice(32, 32)));
    }

    private static UInt256 Uint(ReadOnlySpan<byte> bytes) => new(bytes, true);
}

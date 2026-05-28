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
    private static readonly Hash256 ScoreGroupInitializedTopic = new(DatabaseSchema.ScoreGroupInitialized.Topic);
    private static readonly Hash256 OptOutStatusChangedTopic = new(DatabaseSchema.OptOutStatusChanged.Topic);

    public static readonly RollbackCache<Address, object?> ScoreGroupPolicies = new("ScoreGroupPolicies");

    // Tracks ScoreGroup contract addresses (the groups themselves, not the
    // policies). Populated from `GroupInitialized.group` rows at startup and
    // extended at runtime when a new GroupInitialized event is parsed.
    // ScoreGroup contracts emit ScoreGroupInitialized + OptOutStatusChanged
    // themselves, so we need this address set in addition to the policy set.
    public static readonly RollbackCache<Address, object?> ScoreGroupContracts = new("ScoreGroupContracts");

    public IRollbackCache[] Caches { get; } = [ScoreGroupPolicies, ScoreGroupContracts];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        var policySeed = policyAddresses.ToDictionary(a => a, _ => (object?)null);
        var groupSeed = new Dictionary<Address, object?>();

        var query = new Select("CrcV2_ScoreGroup", "GroupInitialized", ["emitter", "group"], [], [], int.MaxValue, false,
            int.MaxValue);
        foreach (var row in database.Select(query.ToSql(database)).Rows)
        {
            policySeed[new Address(row[0]?.ToString() ?? throw new InvalidOperationException("Score group policy address is null"))] = null;
            groupSeed[new Address(row[1]?.ToString() ?? throw new InvalidOperationException("Score group address is null"))] = null;
        }

        ScoreGroupPolicies.Seed(policySeed);
        ScoreGroupContracts.Seed(groupSeed);
        logger.Info($" * Cached {policySeed.Count} score group mint policy contracts and {groupSeed.Count} score group contracts");

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
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        // Policy contracts emit: GroupInitialized / MerkleRootUpdated /
        // HistoricalSupply / PersonalMinted / RouterMinted.
        if (ScoreGroupPolicies.ContainsKey(log.Address))
        {
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
            yield break;
        }

        // ScoreGroup contracts emit: ScoreGroupInitialized / OptOutStatusChanged.
        if (ScoreGroupContracts.ContainsKey(log.Address))
        {
            if (topic == ScoreGroupInitializedTopic)
            {
                yield return ParseScoreGroupInitialized(block, receipt, log, logIndex);
            }
            else if (topic == OptOutStatusChangedTopic)
            {
                yield return ParseOptOutStatusChanged(block, receipt, log, logIndex);
            }
        }
    }

    private static GroupInitialized ParseGroupInitialized(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var manager = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var router = new Address(log.Data.Slice(12, 20)).ToLowerHex();

        // Track this score group so a subsequent ScoreGroupInitialized or
        // OptOutStatusChanged log emitted by it gets indexed.
        ScoreGroupContracts.Add(block.Number, new Address(group), null);

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
        // score-refactor: MerkleRootUpdated event moved from policy → merkle-tree contract; Topic[1] is now manager, not group.
        // event MerkleRootUpdated(address indexed merkleTreeManager, bytes32 newMerkleRoot,
        //                         bytes32 previousRoot, uint256 updateBlockNumber)
        // 96 unindexed bytes: newMerkleRoot | previousRoot | updateBlockNumber.
        var merkleTreeManager = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data.AsSpan();

        return new MerkleRootUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            merkleTreeManager,
            data.Slice(0, 32).ToArray(),
            data.Slice(32, 32).ToArray(),
            Uint(data.Slice(64, 32)));
    }

    private static HistoricalSupply ParseHistoricalSupply(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event HistoricalSupply(address indexed group, uint256 indexed collateral,
        //                        uint256 supply, uint256 day)
        // Topic shift: topic[1] was collateral, now group; topic[2] is collateral.
        var group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data.AsSpan();

        return new HistoricalSupply(
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

    private static ScoreGroupInitialized ParseScoreGroupInitialized(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event ScoreGroupInitialized(
        //     address indexed metadataManager,
        //     address indexed mintRouter,
        //     address indexed treasury,
        //     address stableERC20,
        //     address demurrageERC20
        // );
        var metadataManager = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var mintRouter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var treasury = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        var data = log.Data.AsSpan();
        // Each address is right-padded to 32 bytes in event data — last 20 bytes are the address.
        var stableErc20 = new Address(data.Slice(12, 20).ToArray()).ToLowerHex();
        var demurrageErc20 = new Address(data.Slice(44, 20).ToArray()).ToLowerHex();

        return new ScoreGroupInitialized(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            metadataManager,
            mintRouter,
            treasury,
            stableErc20,
            demurrageErc20);
    }

    private static OptOutStatusChanged ParseOptOutStatusChanged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event OptOutStatusChanged(address indexed member, bool optedOut);
        var member = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data.AsSpan();
        // bool is encoded as a 32-byte word; non-zero last byte → true.
        var optedOut = data.Length >= 32 && data[31] != 0;

        return new OptOutStatusChanged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            member,
            optedOut);
    }

    private static UInt256 Uint(ReadOnlySpan<byte> bytes) => new(bytes, true);
}

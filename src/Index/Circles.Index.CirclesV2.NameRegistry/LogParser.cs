using System.Numerics;
using Circles.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.NameRegistry;

public class LogParser(Address nameRegistryAddress) : ILogParser
{
    private readonly Hash256 _registerShortNameTopic = new(DatabaseSchema.RegisterShortName.Topic);
    private readonly Hash256 _updateMetadataDigestTopic = new(DatabaseSchema.UpdateMetadataDigest.Topic);
    private readonly Hash256 _cidV0Topic = new(DatabaseSchema.CidV0.Topic);

    /// <summary>
    /// Contains always the latest cidV0 for each avatar.
    /// </summary>
    public static readonly RollbackCache<Address, string> V2AvatarToCidMap = new("V2AvatarToCidMap");

    /// <summary>
    /// Contains the shortname for each avatar.
    /// </summary>
    public static readonly RollbackCache<Address, string> V2AvatarToShortNameMap = new("V2AvatarToShortNameMap");

    public static readonly RollbackCache<string, Address> V2AShortNameToAvatarMap = new("V2AShortNameToAvatarMap");

    public IRollbackCache[] Caches { get; } = [V2AvatarToCidMap, V2AvatarToShortNameMap, V2AShortNameToAvatarMap];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        var selectSignups = new Select(
            "CrcV2",
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
            if (row[0] is not string avatarStr || row[1] is not byte[] metadataDigest)
            {
                logger.Warn($"Skipping row with null or invalid data: avatar={row[0]}, metadataDigest={row[1]}");
                continue;
            }
            var avatar = new Address(avatarStr);
            seed[avatar] = CidHelper.MetadataDigestToCidV0(metadataDigest);
        }

        V2AvatarToCidMap.Seed(seed);

        logger.Info($" * Cached {seed.Count} avatar -> cidV0 mappings from V2 UpdateMetadataDigest");


        var selectShortNames = new Select(
            "CrcV2",
            "RegisterShortName",
            ["avatar", "shortName"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        sql = selectShortNames.ToSql(database);
        result = database.Select(sql);
        rows = result.Rows.ToArray();

        var seed1 = new Dictionary<Address, string>(rows.Length + 1000);
        var seed2 = new Dictionary<string, Address>(rows.Length + 1000);

        foreach (var row in rows)
        {
            var avatar = new Address(row[0]!.ToString()!);
            var shortNameUInt256 = BigInteger.Parse(row[1]!.ToString()!);
            var shortNameBase58Btc = shortNameUInt256.ToBase58Btc();
            seed1[avatar] = shortNameBase58Btc;
            seed2[shortNameBase58Btc] = avatar;
        }

        V2AvatarToShortNameMap.Seed(seed1);
        V2AShortNameToAvatarMap.Seed(seed2);

        logger.Info($" * Cached {seed1.Count} avatar -> short name (and reverse) mappings from V2 RegsiterShortName");

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

        if (log.Address != nameRegistryAddress)
        {
            yield break;
        }

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

        var cidV0 = CidHelper.MetadataDigestToCidV0(metadataDigest);
        V2AvatarToCidMap.Add(block.Number, new Address(avatar), cidV0);

        return new UpdateMetadataDigest(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            avatar,
            metadataDigest);
    }

    private RegisterShortName RegisterShortName(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        Address avatarAddress = new(avatar);

        UInt256 shortName = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 nonce = new UInt256(log.Data.Slice(32, 32), true);

        V2AvatarToShortNameMap.Add(block.Number, avatarAddress, shortName.ToBase58Btc());
        V2AShortNameToAvatarMap.Add(block.Number, shortName.ToBase58Btc(), avatarAddress);

        return new RegisterShortName(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            avatar,
            shortName,
            nonce);
    }

    private CidV0 CidV0(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new CidV0(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            avatar,
            log.Data);
    }
}

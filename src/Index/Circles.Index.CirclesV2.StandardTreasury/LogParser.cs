using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.StandardTreasury;

/// <summary>
/// Parses logs from the StandardTreasury contract address,
/// matching the events defined above.
/// </summary>
public class LogParser(Address standardTreasuryAddress) : ILogParser
{
    private readonly Hash256 _createVaultTopic = new(DatabaseSchema.CreateVault.Topic);
    private readonly Hash256 _groupMintSingleTopic = new(DatabaseSchema.CollateralLockedSingle.Topic);
    private readonly Hash256 _groupMintBatchTopic = new(DatabaseSchema.CollateralLockedBatch.Topic);
    private readonly Hash256 _groupRedeemTopic = new(DatabaseSchema.GroupRedeem.Topic);
    private readonly Hash256 _groupRedeemCollateralReturnTopic = new(DatabaseSchema.GroupRedeemCollateralReturn.Topic);
    private readonly Hash256 _groupRedeemCollateralBurnTopic = new(DatabaseSchema.GroupRedeemCollateralBurn.Topic);

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

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        if (log.Address != standardTreasuryAddress)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (topic == _createVaultTopic)
        {
            yield return CreateVault(block, receipt, log, logIndex);
        }
        else if (topic == _groupMintSingleTopic)
        {
            yield return CollateralLockedSingle(block, receipt, log, logIndex);
        }
        else if (topic == _groupMintBatchTopic)
        {
            foreach (var batchEvent in CollateralLockedBatch(block, receipt, log, logIndex))
            {
                yield return batchEvent;
            }
        }
        else if (topic == _groupRedeemTopic)
        {
            yield return GroupRedeem(block, receipt, log, logIndex);
        }
        else if (topic == _groupRedeemCollateralReturnTopic)
        {
            foreach (var returnEvent in GroupRedeemCollateralReturn(block, receipt, log, logIndex))
            {
                yield return returnEvent;
            }
        }
        else if (topic == _groupRedeemCollateralBurnTopic)
        {
            foreach (var burnEvent in GroupRedeemCollateralBurn(block, receipt, log, logIndex))
            {
                yield return burnEvent;
            }
        }
    }

    private CreateVault CreateVault(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event CreateVault(address indexed group, address indexed vault);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string vaultAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new CreateVault(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            groupAddress,
            vaultAddress
        );
    }

    private CollateralLockedSingle CollateralLockedSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        // Non-indexed params are in log.Data
        //  - first 32 bytes => value
        //  - remainder => userData
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] userData = log.Data.Slice(32).ToArray();

        return new CollateralLockedSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            groupAddress,
            id,
            value,
            userData
        );
    }

    private IEnumerable<CollateralLockedBatch> CollateralLockedBatch(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event CollateralLockedBatch(address indexed group, uint256[] ids, uint256[] values, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data;

        // We expect at least 3 offsets (one each for ids, values, userData):
        //  - [0..31] => offset to ids
        //  - [32..63] => offset to values
        //  - [64..95] => offset to userData
        if (data.Length < 96)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // Parse each offset
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);
        int userDataOffset = LogDataParsingHelper.ParseOffset(data, 64);

        if (idsOffset < 0 || valuesOffset < 0 || userDataOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        // Parse arrays/bytes from each offset
        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);
        byte[] userDataBytes = LogDataParsingHelper.ParseBytes(data, userDataOffset);

        // Typically, you can yield a single event that has all arrays,
        // but your existing pattern yields one event per (id, value).
        for (int i = 0; i < ids.Length; i++)
        {
            // If there are fewer values than ids, default to zero
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new CollateralLockedBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToLowerHex(),
                i,
                groupAddress,
                ids[i],
                val,
                userDataBytes
            );
        }
    }

    private GroupRedeem GroupRedeem(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] dataBytes = log.Data.Slice(32).ToArray();

        return new GroupRedeem(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            groupAddress,
            id,
            value,
            dataBytes
        );
    }

    private IEnumerable<GroupRedeemCollateralReturn> GroupRedeemCollateralReturn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralReturn(address indexed group, address indexed to, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // offsets to ids and values
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralReturn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToLowerHex(),
                i,
                groupAddress,
                toAddress,
                ids[i],
                val
            );
        }
    }

    private IEnumerable<GroupRedeemCollateralBurn> GroupRedeemCollateralBurn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralBurn(address indexed group, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            yield break;
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralBurn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToLowerHex(),
                i,
                groupAddress,
                ids[i],
                val
            );
        }
    }
}
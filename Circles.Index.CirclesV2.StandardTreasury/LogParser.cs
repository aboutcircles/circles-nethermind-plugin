using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.StandardTreasury;

public class LogParser(Address standardTreasuryAddress) : ILogParser
{
    private readonly Hash256 _createVaultTopic = new(DatabaseSchema.CreateVault.Topic);
    private readonly Hash256 _groupMintSingleTopic = new(DatabaseSchema.CollateralLockedSingle.Topic);
    private readonly Hash256 _groupMintBatchTopic = new(DatabaseSchema.CollateralLockedBatch.Topic);
    private readonly Hash256 _groupRedeemTopic = new(DatabaseSchema.GroupRedeem.Topic);
    private readonly Hash256 _groupRedeemCollateralReturnTopic = new(DatabaseSchema.GroupRedeemCollateralReturn.Topic);
    private readonly Hash256 _groupRedeemCollateralBurnTopic = new(DatabaseSchema.GroupRedeemCollateralBurn.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(Block block, int transactionIndex, Transaction transaction)
    {
        return Enumerable.Empty<IIndexEvent>();
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (log.Address == standardTreasuryAddress)
        {
            if (topic == _createVaultTopic)
            {
                yield return CreateVault(block, receipt, log, logIndex);
            }

            if (topic == _groupMintSingleTopic)
            {
                yield return CollateralLockedSingle(block, receipt, log, logIndex);
            }

            if (topic == _groupMintBatchTopic)
            {
                foreach (var batchEvent in CollateralLockedBatch(block, receipt, log, logIndex))
                {
                    yield return batchEvent;
                }
            }

            if (topic == _groupRedeemTopic)
            {
                yield return GroupRedeem(block, receipt, log, logIndex);
            }

            if (topic == _groupRedeemCollateralReturnTopic)
            {
                foreach (var returnEvent in GroupRedeemCollateralReturn(block, receipt, log, logIndex))
                {
                    yield return returnEvent;
                }
            }

            if (topic == _groupRedeemCollateralBurnTopic)
            {
                foreach (var burnEvent in GroupRedeemCollateralBurn(block, receipt, log, logIndex))
                {
                    yield return burnEvent;
                }
            }
        }
    }

    private CreateVault CreateVault(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string vaultAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new CreateVault(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            groupAddress,
            vaultAddress);
    }

    private CollateralLockedSingle CollateralLockedSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] userData = log.Data.Slice(32);

        return new CollateralLockedSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            groupAddress,
            id,
            value,
            userData);
    }

    private IEnumerable<CollateralLockedBatch> CollateralLockedBatch(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int offset = 0;
        int idsLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray(), true, true);
        offset += 32;

        List<UInt256> ids = new List<UInt256>();
        for (int i = 0; i < idsLength; i++)
        {
            ids.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

        int valuesLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray(), true, true);
        offset += 32;

        List<UInt256> values = new List<UInt256>();
        for (int i = 0; i < valuesLength; i++)
        {
            values.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

        byte[] userData = log.Data.Slice(offset);

        for (int i = 0; i < idsLength; i++)
        {
            yield return new CollateralLockedBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                i,
                groupAddress,
                ids[i],
                values[i],
                userData);
        }
    }

    private GroupRedeem GroupRedeem(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] data = log.Data.Slice(32);

        return new GroupRedeem(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            groupAddress,
            id,
            value,
            data);
    }

    private IEnumerable<GroupRedeemCollateralReturn> GroupRedeemCollateralReturn(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        // event GroupRedeemCollateralReturn(address indexed group, address indexed to, uint256[] ids, uint256[] values)

        // Extract addresses from topics:
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        var data = log.Data;

        // The first 32 bytes is the offset to the `ids` array
        // The second 32 bytes is the offset to the `values` array
        // Offsets are relative to the start of `log.Data`.
        var idsOffset = (int)new BigInteger(data.Slice(0, 32).ToArray(), true, true);
        var valuesOffset = (int)new BigInteger(data.Slice(32, 32).ToArray(), true, true); ;

        // Read ids array length and elements
        int idsLength = (int)new BigInteger(data.Slice(idsOffset, 32).ToArray(), true, true);
        int idsDataStart = idsOffset + 32;

        UInt256[] ids = new UInt256[idsLength];
        for (int i = 0; i < idsLength; i++)
        {
            ids[i] = new UInt256(data.Slice(idsDataStart + i * 32, 32), true);
        }

        // Read values array length and elements
        int valuesLength = (int)new BigInteger(data.Slice(valuesOffset, 32).ToArray(), true, true);
        int valuesDataStart = valuesOffset + 32;

        UInt256[] values = new UInt256[valuesLength];
        for (int i = 0; i < valuesLength; i++)
        {
            values[i] = new UInt256(data.Slice(valuesDataStart + i * 32, 32), true);
        }

        // Yield events
        for (int i = 0; i < idsLength; i++)
        {
            yield return new GroupRedeemCollateralReturn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                i,
                groupAddress,
                toAddress,
                ids[i],
                values[i]);
        }
    }

    private IEnumerable<GroupRedeemCollateralBurn> GroupRedeemCollateralBurn(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        // event GroupRedeemCollateralBurn(address indexed group, uint256[] ids, uint256[] values)

        // Extract address from topics:
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        var data = log.Data;

        // The first 32 bytes is the offset to the `ids` array
        // The second 32 bytes is the offset to the `values` array
        var idsOffset = (int)new BigInteger(data.Slice(0, 32).ToArray(), true, true);
        var valuesOffset = (int)new BigInteger(data.Slice(32, 32).ToArray(), true, true);

        // Read ids array
        int idsLength = (int)new BigInteger(data.Slice(idsOffset, 32).ToArray(), true, true);
        int idsDataStart = idsOffset + 32;

        UInt256[] ids = new UInt256[idsLength];
        for (int i = 0; i < idsLength; i++)
        {
            ids[i] = new UInt256(data.Slice(idsDataStart + i * 32, 32), true);
        }

        // Read values array
        int valuesLength = (int)new BigInteger(data.Slice(valuesOffset, 32).ToArray(), true, true);
        int valuesDataStart = valuesOffset + 32;

        UInt256[] values = new UInt256[valuesLength];
        for (int i = 0; i < valuesLength; i++)
        {
            values[i] = new UInt256(data.Slice(valuesDataStart + i * 32, 32), true);
        }

        // Yield events
        for (int i = 0; i < idsLength; i++)
        {
            yield return new GroupRedeemCollateralBurn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                i,
                groupAddress,
                ids[i],
                values[i]);
        }
    }
}
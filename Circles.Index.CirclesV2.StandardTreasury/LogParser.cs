using System.Numerics;
using Circles.Index.CirclesV2.StandardTreasury;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using DatabaseSchema = Circles.Index.CirclesV2.StandardTreasury.DatabaseSchema;

namespace Circles.Index.CirclesV2.StandardTreasury;

public class LogParser(Address standardTreasuryAddress) : ILogParser
{
    private readonly Hash256 _createVaultTopic = new(DatabaseSchema.CreateVault.Topic);
    private readonly Hash256 _groupMintSingleTopic = new(DatabaseSchema.GroupMintSingle.Topic);
    private readonly Hash256 _groupMintBatchTopic = new(DatabaseSchema.GroupMintBatch.Topic);
    private readonly Hash256 _groupRedeemTopic = new(DatabaseSchema.GroupRedeem.Topic);
    private readonly Hash256 _groupRedeemCollateralReturnTopic = new(DatabaseSchema.GroupRedeemCollateralReturn.Topic);
    private readonly Hash256 _groupRedeemCollateralBurnTopic = new(DatabaseSchema.GroupRedeemCollateralBurn.Topic);

    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (log.LoggersAddress == standardTreasuryAddress)
        {
            if (topic == _createVaultTopic)
            {
                yield return CreateVault(block, receipt, log, logIndex);
            }

            if (topic == _groupMintSingleTopic)
            {
                yield return GroupMintSingle(block, receipt, log, logIndex);
            }

            if (topic == _groupMintBatchTopic)
            {
                foreach (var batchEvent in GroupMintBatch(block, receipt, log, logIndex))
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

    private GroupMintSingle GroupMintSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] userData = log.Data.Slice(32);

        return new GroupMintSingle(
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

    private IEnumerable<GroupMintBatch> GroupMintBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int offset = 0;
        int idsLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
        offset += 32;

        List<UInt256> ids = new List<UInt256>();
        for (int i = 0; i < idsLength; i++)
        {
            ids.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

        int valuesLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
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
            yield return new GroupMintBatch(
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
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int offset = 0;
        int idsLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
        offset += 32;

        List<UInt256> ids = new List<UInt256>();
        for (int i = 0; i < idsLength; i++)
        {
            ids.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

        int valuesLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
        offset += 32;

        List<UInt256> values = new List<UInt256>();
        for (int i = 0; i < valuesLength; i++)
        {
            values.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

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
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int offset = 0;
        int idsLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
        offset += 32;

        List<UInt256> ids = new List<UInt256>();
        for (int i = 0; i < idsLength; i++)
        {
            ids.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

        int valuesLength = (int)new BigInteger(log.Data.Slice(offset, 32).ToArray());
        offset += 32;

        List<UInt256> values = new List<UInt256>();
        for (int i = 0; i < valuesLength; i++)
        {
            values.Add(new UInt256(log.Data.Slice(offset, 32), true));
            offset += 32;
        }

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
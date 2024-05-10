using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.V2;

public class LogParser(Address v2HubAddress) : ILogParser
{
    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];
        if (log.LoggersAddress != v2HubAddress)
        {
            yield break;
        }

        if (topic == DatabaseSchema.Stopped.Topic)
        {
            yield return CrcV2Stopped(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.Trust.Topic)
        {
            yield return CrcV2Trust(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.InviteHuman.Topic)
        {
            yield return CrcV2InviteHuman(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.PersonalMint.Topic)
        {
            yield return CrcV2PersonalMint(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.RegisterHuman.Topic)
        {
            yield return CrcV2RegisterHuman(block, receipt, log, logIndex);
        }

        {
            yield return CrcV2RegisterHuman(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.RegisterGroup.Topic)
        {
            yield return CrcV2RegisterGroup(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.RegisterOrganization.Topic)
        {
            yield return CrcV2RegisterOrganization(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.TransferBatch.Topic)
        {
            foreach (var batchEvent in Erc1155TransferBatch(block, receipt, log, logIndex))
            {
                yield return batchEvent;
            }
        }

        if (topic == DatabaseSchema.TransferSingle.Topic)
        {
            yield return Erc1155TransferSingle(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.ApprovalForAll.Topic)
        {
            yield return Erc1155ApprovalForAll(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.URI.Topic)
        {
            yield return Erc1155Uri(block, receipt, log, logIndex);
        }

        if (topic == DatabaseSchema.DiscountCost.Topic)
        {
            yield return DiscountCost(block, receipt, log, logIndex);
        }
    }

    private URI Erc1155Uri(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var tokenId = new UInt256(log.Topics[1].Bytes, true);
        var uri = Encoding.UTF8.GetString(log.Data);

        return new URI(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            tokenId,
            uri);
    }

    private ApprovalForAll Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private TransferSingle Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private IEnumerable<TransferBatch> Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private RegisterOrganization CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string orgName = Encoding.UTF8.GetString(log.Data);

        return new RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            orgAddress,
            orgName);
    }

    private RegisterGroup CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string mintPolicy = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string treasury = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int nameOffset = (int)new BigInteger(log.Data.Slice(0, 32).ToArray());
        int nameLength = (int)new BigInteger(log.Data.Slice(nameOffset, 32).ToArray());
        string groupName = Encoding.UTF8.GetString(log.Data.Slice(nameOffset + 32, nameLength));

        int symbolOffset = (int)new BigInteger(log.Data.Slice(32, 32).ToArray());
        int symbolLength = (int)new BigInteger(log.Data.Slice(symbolOffset, 32).ToArray());
        string groupSymbol = Encoding.UTF8.GetString(log.Data.Slice(symbolOffset + 32, symbolLength));

        return new RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol);
    }


    private RegisterHuman CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            humanAddress);
    }

    private PersonalMint CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string toAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        return new PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            toAddress,
            amount,
            startPeriod,
            endPeriod);
    }

    private InviteHuman CrcV2InviteHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string inviterAddress =
            "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string inviteeAddress =
            "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new InviteHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            inviterAddress,
            inviteeAddress);
    }

    private Trust CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string userAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string canSendToAddress =
            "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        return new Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            userAddress,
            canSendToAddress,
            limit);
    }

    private Stopped CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string address = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            address);
    }

    private DiscountCost DiscountCost(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 cost = new UInt256(log.Data, true);

        return new DiscountCost(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            id,
            cost);
    }
}
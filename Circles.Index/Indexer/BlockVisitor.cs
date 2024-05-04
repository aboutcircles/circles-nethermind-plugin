using System.Globalization;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Circles.Index.Utils;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Indexer;

public class IndexerVisitor(Settings settings) : IIndexerVisitor
{
    #region Visitor

    public void VisitBlock(Block block)
    {
    }

    public bool VisitReceipt(Block block, TxReceipt receipt) => receipt.Logs != null;

    public bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex) =>
        DispatchByTopic(block, receipt, log, logIndex);

    public void LeaveReceipt(Block block, TxReceipt receipt, bool logIndexed)
    {
    }

    public void LeaveBlock(Block block, bool receiptIndexed)
    {
        // sink.AddBlock(block.Number, (long)block.Timestamp,
        //     block.Hash?.ToString() ?? throw new Exception("Block hash is null"));
    }

    #endregion

    #region Implementation

    private bool DispatchByTopic(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            return false;
        }

        var topic = log.Topics[0];
        if (log.LoggersAddress == settings.CirclesV2HubAddress)
        {
            if (topic == StaticResources.CrcV2StoppedTopic)
            {
                return CrcV2Stopped(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2TrustTopic)
            {
                return CrcV2Trust(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2ConvertInflationTopic)
            {
                return CrcV2ConvertInflation(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2InviteHumanTopic)
            {
                return CrcV2InviteHuman(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2PersonalMintTopic)
            {
                return CrcV2PersonalMint(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2RegisterHumanTopic)
            {
                return CrcV2RegisterHuman(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2RegisterGroupTopic)
            {
                return CrcV2RegisterGroup(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcV2RegisterOrganizationTopic)
            {
                return CrcV2RegisterOrganization(block, receipt, log, logIndex);
            }
            
            if (topic == StaticResources.Erc1155TransferBatchTopic)
            {
                return Erc1155TransferBatch(block, receipt, log, logIndex);
            }
            
            if (topic == StaticResources.Erc1155TransferSingleTopic)
            {
                return Erc1155TransferSingle(block, receipt, log, logIndex);
            }
            
            if (topic == StaticResources.Erc1155ApprovalForAllTopic)
            {
                return Erc1155ApprovalForAll(block, receipt, log, logIndex);
            }
            
            if (topic == StaticResources.Erc1155UriTopic)
            {
                return Erc1155Uri(block, receipt, log, logIndex);
            }
        }

        return false;
    }

    private bool Erc1155Uri(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private bool Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private bool Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private bool Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    private bool CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string orgName = Encoding.UTF8.GetString(log.Data);

        // sink.AddCrcV2RegisterOrganization(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     orgAddress,
        //     orgName);

        return true;
    }

    private bool CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string mintPolicy = "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string treasury = "0x" + log.Topics[3].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        int nameOffset = (int)new BigInteger(log.Data.Slice(0, 32).ToArray());
        int nameLength = (int)new BigInteger(log.Data.Slice(nameOffset, 32).ToArray());
        string groupName = Encoding.UTF8.GetString(log.Data.Slice(nameOffset + 32, nameLength));

        int symbolOffset = (int)new BigInteger(log.Data.Slice(32, 32).ToArray());
        int symbolLength = (int)new BigInteger(log.Data.Slice(symbolOffset, 32).ToArray());
        string groupSymbol = Encoding.UTF8.GetString(log.Data.Slice(symbolOffset + 32, symbolLength));

        // sink.AddCrcV2RegisterGroup(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     groupAddress,
        //     mintPolicy,
        //     treasury,
        //     groupName,
        //     groupSymbol);

        return true;
    }


    private bool CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        // sink.AddCrcV2RegisterHuman(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     humanAddress);

        return true;
    }

    private bool CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string toAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        // sink.AddCrcV2PersonalMint(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     toAddress,
        //     amount,
        //     startPeriod,
        //     endPeriod);

        return true;
    }

    private bool CrcV2InviteHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string inviterAddress =
            "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string inviteeAddress =
            "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        // sink.AddCrcV2InviteHuman(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     inviterAddress,
        //     inviteeAddress);

        return true;
    }

    private bool CrcV2ConvertInflation(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        UInt256 inflationValue = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurrageValue = new UInt256(log.Data.Slice(32, 32), true);
        ulong day = new UInt256(log.Data.Slice(64), true).ToUInt64(CultureInfo.InvariantCulture);

        // sink.AddCrcV2ConvertInflation(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     inflationValue,
        //     demurrageValue,
        //     day);

        return true;
    }

    private bool CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string userAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string canSendToAddress =
            "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        // sink.AddCrcV2Trust(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     userAddress,
        //     canSendToAddress,
        //     limit);

        return true;
    }

    private bool CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string address = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        // sink.AddCrcV2Stopped(
        //     block.Number,
        //     (long)block.Timestamp,
        //     receipt.Index,
        //     logIndex,
        //     receipt.TxHash.ToString(),
        //     address);

        return true;
    }

    #endregion
}
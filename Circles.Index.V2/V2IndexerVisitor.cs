using System.Globalization;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.V2;

public record CrcV2RegisterOrganizationData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OrgAddress, string OrgName) : IIndexEvent;
public record CrcV2RegisterGroupData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string GroupAddress, string MintPolicy, string Treasury, string GroupName, string GroupSymbol) : IIndexEvent;
public record CrcV2RegisterHumanData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string HumanAddress) : IIndexEvent;
public record CrcV2PersonalMintData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string ToAddress, UInt256 Amount, UInt256 StartPeriod, UInt256 EndPeriod) : IIndexEvent;
public record CrcV2InviteHumanData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string InviterAddress, string InviteeAddress) : IIndexEvent;
public record CrcV2ConvertInflationData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, UInt256 InflationValue, UInt256 DemurrageValue, ulong Day) : IIndexEvent;
public record CrcV2TrustData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string UserAddress, string CanSendToAddress, UInt256 Limit) : IIndexEvent;
public record CrcV2StoppedData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string Address) : IIndexEvent;
public record Erc1155ApprovalForAllData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OperatorAddress, string ApprovedAddress, bool Approved) : IIndexEvent;
public record Erc1155TransferSingleData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string OperatorAddress, string FromAddress, string ToAddress, UInt256 Id, UInt256 Value) : IIndexEvent;
public record Erc1155TransferBatchData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, int BatchIndex, string OperatorAddress, string FromAddress, string ToAddress, UInt256 Id, UInt256 Value) : IIndexEvent;
public record Erc1155UriData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string TokenAddress, UInt256 Id, string Uri) : IIndexEvent;

public class V2IndexerVisitor(Address v2HubAddress, INewSink sink) : SinkVisitor
{
    public static readonly int AddressEmptyBytesPrefixLength = 26; 
    
    public override bool VisitReceipt(Block block, TxReceipt receipt)
    {
        throw new NotImplementedException();
    }

    public override bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        throw new NotImplementedException();
    }

    public override void LeaveReceipt(Block block, TxReceipt receipt, bool logIndexed)
    {
        throw new NotImplementedException();
    }
    
    private bool DispatchByTopic(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            return false;
        }

        var topic = log.Topics[0];
        if (log.LoggersAddress != v2HubAddress)
        {
            return false;
        }
        
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
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string orgName = Encoding.UTF8.GetString(log.Data);

        sink.AddEvent(new CrcV2RegisterOrganizationData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            orgAddress,
            orgName));

        return true;
    }

    private bool CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string mintPolicy = "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);
        string treasury = "0x" + log.Topics[3].ToString().Substring(AddressEmptyBytesPrefixLength);

        int nameOffset = (int)new BigInteger(log.Data.Slice(0, 32).ToArray());
        int nameLength = (int)new BigInteger(log.Data.Slice(nameOffset, 32).ToArray());
        string groupName = Encoding.UTF8.GetString(log.Data.Slice(nameOffset + 32, nameLength));

        int symbolOffset = (int)new BigInteger(log.Data.Slice(32, 32).ToArray());
        int symbolLength = (int)new BigInteger(log.Data.Slice(symbolOffset, 32).ToArray());
        string groupSymbol = Encoding.UTF8.GetString(log.Data.Slice(symbolOffset + 32, symbolLength));

        sink.AddEvent(new CrcV2RegisterGroupData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol));

        return true;
    }


    private bool CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);

        sink.AddEvent(new CrcV2RegisterHumanData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            humanAddress));

        return true;
    }

    private bool CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string toAddress = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        sink.AddEvent(new CrcV2PersonalMintData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            toAddress,
            amount,
            startPeriod,
            endPeriod));

        return true;
    }

    private bool CrcV2InviteHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string inviterAddress =
            "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string inviteeAddress =
            "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);

        sink.AddEvent(new CrcV2InviteHumanData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            inviterAddress,
            inviteeAddress));

        return true;
    }

    private bool CrcV2ConvertInflation(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        UInt256 inflationValue = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurrageValue = new UInt256(log.Data.Slice(32, 32), true);
        ulong day = new UInt256(log.Data.Slice(64), true).ToUInt64(CultureInfo.InvariantCulture);

        sink.AddEvent(new CrcV2ConvertInflationData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            inflationValue,
            demurrageValue,
            day));

        return true;
    }

    private bool CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string userAddress = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string canSendToAddress =
            "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        sink.AddEvent(new CrcV2TrustData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            userAddress,
            canSendToAddress,
            limit));

        return true;
    }

    private bool CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string address = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);

        sink.AddEvent(new CrcV2StoppedData(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            address));

        return true;
    }
}

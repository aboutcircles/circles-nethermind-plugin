using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.V2;

public class V2IndexerVisitor(Address v2HubAddress) : SinkVisitor
{
    public static Hash256 CrcV2RegisterHumanTopic { get; } = Keccak.Compute("RegisterHuman(address)");
    public static Hash256 CrcV2InviteHumanTopic { get; } = Keccak.Compute("InviteHuman(address,address)");

    public static Hash256 CrcV2RegisterOrganizationTopic { get; } =
        Keccak.Compute("RegisterOrganization(address,string)");

    public static Hash256 CrcV2RegisterGroupTopic { get; } =
        Keccak.Compute("RegisterGroup(address,address,address,string,string)");

    public static Hash256 CrcV2TrustTopic { get; } = Keccak.Compute("Trust(address,address,uint256)");
    public static Hash256 CrcV2StoppedTopic { get; } = Keccak.Compute("Stopped(address)");

    public static Hash256 CrcV2PersonalMintTopic { get; } =
        Keccak.Compute("PersonalMint(address,uint256,uint256,uint256)");

    public static Hash256 CrcV2ConvertInflationTopic { get; } =
        Keccak.Compute("ConvertInflation(uint256,uint256,uint64");

    // All ERC1155 events
    public static Hash256 Erc1155TransferSingleTopic { get; } =
        Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)");

    public static Hash256 Erc1155TransferBatchTopic { get; } =
        Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");

    public static Hash256 Erc1155ApprovalForAllTopic { get; } = Keccak.Compute("ApprovalForAll(address,address,bool)");
    public static Hash256 Erc1155UriTopic { get; } = Keccak.Compute("URI(uint256,string)");
   
    public override IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex) 
    {
        if (log.Topics.Length == 0)
        {
            return ArraySegment<IIndexEvent>.Empty;
        }

        var topic = log.Topics[0];
        if (log.LoggersAddress != v2HubAddress)
        {
            return ArraySegment<IIndexEvent>.Empty;
        }
        
        // if (topic == CrcV2StoppedTopic)
        // {
        //     await CrcV2Stopped(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2TrustTopic)
        // {
        //     await CrcV2Trust(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2ConvertInflationTopic)
        // {
        //     await CrcV2ConvertInflation(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2InviteHumanTopic)
        // {
        //     await CrcV2InviteHuman(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2PersonalMintTopic)
        // {
        //     await CrcV2PersonalMint(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2RegisterHumanTopic)
        // {
        //     await CrcV2RegisterHuman(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2RegisterGroupTopic)
        // {
        //     await CrcV2RegisterGroup(block, receipt, log, logIndex);
        // }
        //
        // if (topic == CrcV2RegisterOrganizationTopic)
        // {
        //     await CrcV2RegisterOrganization(block, receipt, log, logIndex);
        // }
        //     
        // if (topic == Erc1155TransferBatchTopic)
        // {
        //     await Erc1155TransferBatch(block, receipt, log, logIndex);
        // }
        //     
        // if (topic == Erc1155TransferSingleTopic)
        // {
        //     await Erc1155TransferSingle(block, receipt, log, logIndex);
        // }
        //     
        // if (topic == Erc1155ApprovalForAllTopic)
        // {
        //     await Erc1155ApprovalForAll(block, receipt, log, logIndex);
        // }
        //     
        // if (topic == Erc1155UriTopic)
        // {
        //     await Erc1155Uri(block, receipt, log, logIndex);
        // }
        
        return ArraySegment<IIndexEvent>.Empty;
    }

    // private Task Erc1155Uri(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private Task Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private Task Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private Task Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     throw new NotImplementedException();
    // }
    //
    // private Task CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string orgAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     string orgName = Encoding.UTF8.GetString(log.Data);
    //
    //     return sink.AddEvent(new CrcV2RegisterOrganizationData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         orgAddress,
    //         orgName));
    // }
    //
    // private Task CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     string mintPolicy = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     string treasury = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //
    //     int nameOffset = (int)new BigInteger(log.Data.Slice(0, 32).ToArray());
    //     int nameLength = (int)new BigInteger(log.Data.Slice(nameOffset, 32).ToArray());
    //     string groupName = Encoding.UTF8.GetString(log.Data.Slice(nameOffset + 32, nameLength));
    //
    //     int symbolOffset = (int)new BigInteger(log.Data.Slice(32, 32).ToArray());
    //     int symbolLength = (int)new BigInteger(log.Data.Slice(symbolOffset, 32).ToArray());
    //     string groupSymbol = Encoding.UTF8.GetString(log.Data.Slice(symbolOffset + 32, symbolLength));
    //
    //     return sink.AddEvent(new CrcV2RegisterGroupData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         groupAddress,
    //         mintPolicy,
    //         treasury,
    //         groupName,
    //         groupSymbol));
    // }
    //
    //
    // private Task CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string humanAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //
    //     return sink.AddEvent(new CrcV2RegisterHumanData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         humanAddress));
    // }
    //
    // private Task CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string toAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
    //     UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
    //     UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);
    //
    //     return sink.AddEvent(new CrcV2PersonalMintData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         toAddress,
    //         amount,
    //         startPeriod,
    //         endPeriod));
    // }
    //
    // private Task CrcV2InviteHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string inviterAddress =
    //         "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     string inviteeAddress =
    //         "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //
    //     return sink.AddEvent(new CrcV2InviteHumanData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         inviterAddress,
    //         inviteeAddress));
    // }
    //
    // private Task CrcV2ConvertInflation(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     UInt256 inflationValue = new UInt256(log.Data.Slice(0, 32), true);
    //     UInt256 demurrageValue = new UInt256(log.Data.Slice(32, 32), true);
    //     ulong day = new UInt256(log.Data.Slice(64), true).ToUInt64(CultureInfo.InvariantCulture);
    //
    //     return sink.AddEvent(new CrcV2ConvertInflationData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         inflationValue,
    //         demurrageValue,
    //         day));
    // }
    //
    // private Task CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string userAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     string canSendToAddress =
    //         "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //     UInt256 limit = new UInt256(log.Data, true);
    //
    //     return sink.AddEvent(new CrcV2TrustData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         userAddress,
    //         canSendToAddress,
    //         limit));
    // }
    //
    // private Task CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    // {
    //     string address = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
    //
    //     return sink.AddEvent(new CrcV2StoppedData(
    //         block.Number,
    //         (long)block.Timestamp,
    //         receipt.Index,
    //         logIndex,
    //         receipt.TxHash.ToString(),
    //         address));
    // }
}

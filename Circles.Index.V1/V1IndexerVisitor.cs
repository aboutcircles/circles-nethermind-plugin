using System.Collections.Concurrent;
using System.Globalization;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.V1;

public record CirclesSignupData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string CirclesAddress, string? TokenAddress) : IIndexEvent;
public record CirclesTrustData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string UserAddress, string CanSendToAddress, int Limit) : IIndexEvent;
public record CirclesHubTransferData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string FromAddress, string ToAddress, UInt256 Amount) : IIndexEvent;
public record Erc20TransferData(long BlockNumber, long Timestamp, int TransactionIndex, int LogIndex, string TransactionHash, string TokenAddress, string From, string To, UInt256 Value) : IIndexEvent;


public class V1IndexerVisitor(Address v1HubAddress, INewSink sink) : SinkVisitor
{
    public static readonly ConcurrentDictionary<Address, object?> CirclesTokenAddresses = new();
    public static readonly int AddressEmptyBytesPrefixLength = 26; 
    
    public override bool VisitReceipt(Block block, TxReceipt receipt)
    {
        throw new NotImplementedException();
    }

    public override bool VisitLog(Block block, TxReceipt receipt, LogEntry log, int logIndex) =>
        DispatchByTopic(block, receipt, log, logIndex);

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
        if (topic == StaticResources.Erc20TransferTopic &&
            CirclesTokenAddresses.ContainsKey(log.LoggersAddress))
        {
            return Erc20Transfer(block, receipt, log, logIndex);
        }

        if (log.LoggersAddress == v1HubAddress)
        {
            if (topic == StaticResources.CrcSignupEventTopic)
            {
                return CrcSignup(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcHubTransferEventTopic)
            {
                return CrcHubTransfer(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcTrustEventTopic)
            {
                return CrcTrust(block, receipt, log, logIndex);
            }

            if (topic == StaticResources.CrcOrganisationSignupEventTopic)
            {
                return CrcOrgSignup(block, receipt, log, logIndex);
            }
        }
        
        return false;
    }

    private bool Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);
        UInt256 value = new(log.Data, true);

        sink.AddEvent(new Erc20TransferData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , log.LoggersAddress.ToString(true, false)
            , from
            , to
            , value));

        return true;
    }

    private bool CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);

        sink.AddEvent(new CirclesSignupData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , null));

        return true;
    }

    private bool CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string canSendTo = "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);
        int limit = new UInt256(log.Data, true).ToInt32(CultureInfo.InvariantCulture);

        sink.AddEvent(new CirclesTrustData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , canSendTo
            , limit));

        return true;
    }

    private bool CrcHubTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(AddressEmptyBytesPrefixLength);
        UInt256 amount = new(log.Data, true);

        sink.AddEvent(new CirclesHubTransferData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , from
            , to
            , amount));

        return true;
    }

    private bool CrcSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(AddressEmptyBytesPrefixLength);
        Address tokenAddress = new Address(log.Data.Slice(12));

        sink.AddEvent(new CirclesSignupData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , tokenAddress.ToString(true, false)));

        CirclesTokenAddresses.TryAdd(tokenAddress, null);

        // Every signup comes together with an Erc20 transfer (the signup bonus).
        // Since the signup event is emitted after the transfer, the token wasn't known yet when we encountered the transfer.
        // Look for the transfer again and process it.
        foreach (var repeatedLogEntry in receipt.Logs!)
        {
            if (repeatedLogEntry.LoggersAddress != tokenAddress)
            {
                continue;
            }

            if (repeatedLogEntry.Topics[0] == StaticResources.Erc20TransferTopic)
            {
                Erc20Transfer(block, receipt, repeatedLogEntry, logIndex);
            }
        }

        return true;
    }
}
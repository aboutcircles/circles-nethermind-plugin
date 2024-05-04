using System.Collections.Concurrent;
using System.Globalization;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.V1;

public class V1IndexerVisitor(Address v1HubAddress) : SinkVisitor
{
    public static Hash256 CrcHubTransferEventTopic { get; } = Keccak.Compute("HubTransfer(address,address,uint256)");
    public static Hash256 CrcTrustEventTopic { get; } = Keccak.Compute("Trust(address,address,uint256)");
    public static Hash256 CrcSignupEventTopic { get; } = Keccak.Compute("Signup(address,address)");
    public static Hash256 CrcOrganisationSignupEventTopic { get; } = Keccak.Compute("OrganisationSignup(address)");
    public static Hash256 Erc20TransferTopic { get; } = Keccak.Compute("Transfer(address,address,uint256)");
    
    public static readonly ConcurrentDictionary<Address, object?> CirclesTokenAddresses = new();
    
    public override IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        List<IIndexEvent> events = new();
        if (log.Topics.Length == 0)
        {
            return events;
        }

        var topic = log.Topics[0];
        if (topic == Erc20TransferTopic &&
            CirclesTokenAddresses.ContainsKey(log.LoggersAddress))
        {
            Console.WriteLine("Found ERC20 transfer for {0}", log.LoggersAddress);
            events.Add(Erc20Transfer(block, receipt, log, logIndex));
        }

        if (log.LoggersAddress == v1HubAddress)
        {
            if (topic == CrcSignupEventTopic)
            {
                var signupEvents = CrcSignup(block, receipt, log, logIndex);
                foreach (var signupEvent in signupEvents)
                {
                    events.Add(signupEvent);
                }
            }

            if (topic == CrcHubTransferEventTopic)
            {
                events.Add(CrcHubTransfer(block, receipt, log, logIndex));
            }

            if (topic == CrcTrustEventTopic)
            {
                events.Add(CrcTrust(block, receipt, log, logIndex));
            }

            if (topic == CrcOrganisationSignupEventTopic)
            {
                events.Add(CrcOrgSignup(block, receipt, log, logIndex));
            }
        };

        return events;
    }

    private IIndexEvent Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 value = new(log.Data, true);

        return new Erc20TransferData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , log.LoggersAddress.ToString(true, false)
            , from
            , to
            , value);
    }

    private IIndexEvent CrcOrgSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new CirclesSignupData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , null);
    }

    private IIndexEvent CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string canSendTo = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        int limit = new UInt256(log.Data, true).ToInt32(CultureInfo.InvariantCulture);

        return new CirclesTrustData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , canSendTo
            , limit);
    }

    private IIndexEvent CrcHubTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new(log.Data, true);

        return new CirclesHubTransferData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , from
            , to
            , amount);
    }

    private IEnumerable<IIndexEvent> CrcSignup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        Address tokenAddress = new Address(log.Data.Slice(12));

        IIndexEvent signupEvent = new CirclesSignupData(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , tokenAddress.ToString(true, false));
        
        CirclesTokenAddresses.TryAdd(tokenAddress, null);

        IIndexEvent? signupBonusEvent = null;
        
        // Every signup comes together with an Erc20 transfer (the signup bonus).
        // Since the signup event is emitted after the transfer, the token wasn't known yet when we encountered the transfer.
        // Look for the transfer again and process it.
        foreach (var repeatedLogEntry in receipt.Logs!)
        {
            if (repeatedLogEntry.LoggersAddress != tokenAddress)
            {
                continue;
            }

            if (repeatedLogEntry.Topics[0] == Erc20TransferTopic)
            {
                Console.WriteLine("Found signup bonus for {0}", user);
                signupBonusEvent = Erc20Transfer(block, receipt, repeatedLogEntry, logIndex);
                break;
            }
        }
        
        return signupBonusEvent == null
            ? new[] {signupEvent}
            : new[] {signupEvent, signupBonusEvent};
    }
}
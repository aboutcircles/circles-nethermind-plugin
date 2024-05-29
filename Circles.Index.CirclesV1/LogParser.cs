using System.Collections.Concurrent;
using System.Globalization;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV1;

public class LogParser(Address v1HubAddress) : ILogParser
{
    public static readonly ConcurrentDictionary<Address, object?> CirclesTokenAddresses = new();

    private readonly Hash256 _transferTopic = new(DatabaseSchema.Transfer.Topic);
    private readonly Hash256 _signupTopic = new(DatabaseSchema.Signup.Topic);
    private readonly Hash256 _organizationSignupTopic = new(DatabaseSchema.OrganizationSignup.Topic);
    private readonly Hash256 _hubTransferTopic = new(DatabaseSchema.HubTransfer.Topic);
    private readonly Hash256 _trustTopic = new(DatabaseSchema.Trust.Topic);

    public IEnumerable<IIndexEvent> ParseLog(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        List<IIndexEvent> events = new();
        if (log.Topics.Length == 0)
        {
            return events;
        }

        var topic = log.Topics[0];
        if (topic == _transferTopic &&
            CirclesTokenAddresses.ContainsKey(log.LoggersAddress))
        {
            events.Add(Erc20Transfer(block, receipt, log, logIndex));
        }

        if (log.LoggersAddress == v1HubAddress)
        {
            if (topic == _signupTopic)
            {
                var signupEvents = CrcSignup(block, receipt, log, logIndex);
                foreach (var signupEvent in signupEvents)
                {
                    events.Add(signupEvent);
                }
            }

            if (topic == _organizationSignupTopic)
            {
                events.Add(CrcOrgSignup(block, receipt, log, logIndex));
            }

            if (topic == _hubTransferTopic)
            {
                events.Add(CrcHubTransfer(block, receipt, log, logIndex));
            }

            if (topic == _trustTopic)
            {
                events.Add(CrcTrust(block, receipt, log, logIndex));
            }
        }

        return events;
    }

    private IIndexEvent Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 value = new(log.Data, true);

        return new Transfer(
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
        string org = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new OrganizationSignup(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , org);
    }

    private IIndexEvent CrcTrust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string canSendTo = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string user = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        int limit = new UInt256(log.Data, true).ToInt32(CultureInfo.InvariantCulture);

        return new Trust(
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

        return new HubTransfer(
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

        IIndexEvent signupEvent = new Signup(
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
        for (int i = 0; i < receipt.Logs!.Length; i++)
        {
            var repeatedLogEntry = receipt.Logs[i];
            if (repeatedLogEntry.LoggersAddress != tokenAddress)
            {
                continue;
            }

            if (repeatedLogEntry.Topics[0] == _transferTopic)
            {
                signupBonusEvent = Erc20Transfer(block, receipt, repeatedLogEntry, i);
                break;
            }
        }

        return signupBonusEvent == null
            ? new[] { signupEvent }
            : new[] { signupEvent, signupBonusEvent };
    }
}
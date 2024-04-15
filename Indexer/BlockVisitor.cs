using System.Globalization;
using System.Numerics;
using System.Text;
using Circles.Index.Data;
using Circles.Index.Utils;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.Indexer;

public class IndexerVisitor(ISink sink, Settings settings) : IIndexerVisitor
{
    #region Visitor

    public void VisitBlock(Nethermind.Core.Block block)
    {
    }

    public bool VisitReceipt(Nethermind.Core.Block block, TxReceipt receipt) => receipt.Logs != null;

    public bool VisitLog(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex) =>
        DispatchByTopic(block, receipt, log, logIndex);

    public void LeaveReceipt(Nethermind.Core.Block block, TxReceipt receipt, bool logIndexed)
    {
    }

    public void LeaveBlock(Nethermind.Core.Block block, bool receiptIndexed)
    {
        sink.AddBlock(block.Number, (long)block.Timestamp,
            block.Hash?.ToString() ?? throw new Exception("Block hash is null"));
    }

    #endregion

    #region Implementation

    private bool DispatchByTopic(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            return false;
        }

        var topic = log.Topics[0];
        if (topic == StaticResources.Erc20TransferTopic &&
            Caches.CirclesTokenAddresses.ContainsKey(log.LoggersAddress))
        {
            return Erc20Transfer(block, receipt, log, logIndex);
        }

        if (log.LoggersAddress == settings.CirclesV1HubAddress)
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
        }

        return false;
    }

    private bool CrcV2RegisterOrganization(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string orgName = Encoding.UTF8.GetString(log.Data);

        sink.AddCrcV2RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            orgAddress,
            orgName);

        return true;
    }

    private bool CrcV2RegisterGroup(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
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

        sink.AddCrcV2RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol);

        return true;
    }


    private bool CrcV2RegisterHuman(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        sink.AddCrcV2RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            humanAddress);

        return true;
    }

    private bool CrcV2PersonalMint(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string toAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        sink.AddCrcV2PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            toAddress,
            amount,
            startPeriod,
            endPeriod);

        return true;
    }

    private bool CrcV2InviteHuman(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string inviterAddress =
            "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string inviteeAddress =
            "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        sink.AddCrcV2InviteHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            inviterAddress,
            inviteeAddress);

        return true;
    }

    private bool CrcV2ConvertInflation(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        UInt256 inflationValue = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurrageValue = new UInt256(log.Data.Slice(32, 32), true);
        ulong day = new UInt256(log.Data.Slice(64), true).ToUInt64(CultureInfo.InvariantCulture);

        sink.AddCrcV2ConvertInflation(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            inflationValue,
            demurrageValue,
            day);

        return true;
    }

    private bool CrcV2Trust(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string userAddress = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string canSendToAddress =
            "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        sink.AddCrcV2Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            userAddress,
            canSendToAddress,
            limit);

        return true;
    }

    private bool CrcV2Stopped(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string address = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        sink.AddCrcV2Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash.ToString(),
            address);

        return true;
    }

    private bool Erc20Transfer(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 value = new(log.Data, true);

        sink.AddErc20Transfer(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , log.LoggersAddress.ToString(true, false)
            , from
            , to
            , value);

        return true;
    }

    private bool CrcOrgSignup(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);

        sink.AddCirclesSignup(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , null);

        return true;
    }

    private bool CrcTrust(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string canSendTo = "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        int limit = new UInt256(log.Data, true).ToInt32(CultureInfo.InvariantCulture);

        sink.AddCirclesTrust(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , canSendTo
            , limit);

        return true;
    }

    private bool CrcHubTransfer(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        UInt256 amount = new(log.Data, true);

        sink.AddCirclesHubTransfer(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , from
            , to
            , amount);

        return true;
    }

    private bool CrcSignup(Nethermind.Core.Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string user = "0x" + log.Topics[1].ToString().Substring(StaticResources.AddressEmptyBytesPrefixLength);
        Address tokenAddress = new Address(log.Data.Slice(12));

        sink.AddCirclesSignup(
            receipt.BlockNumber
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , user
            , tokenAddress.ToString(true, false));

        Caches.CirclesTokenAddresses.TryAdd(tokenAddress, null);

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

    #endregion
}
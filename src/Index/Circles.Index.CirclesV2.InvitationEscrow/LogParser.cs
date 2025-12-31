using System.Collections.Immutable;
using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.InvitationEscrow;

/// <summary>
/// Parses logs emitted by the InvitationEscrow contract.
/// </summary>
public class LogParser(ImmutableHashSet<Address> escrowAddresses) : ILogParser
{
    private readonly Hash256 _escrowedTopic = new(DatabaseSchema.InvitationEscrowed.Topic);
    private readonly Hash256 _redeemedTopic = new(DatabaseSchema.InvitationRedeemed.Topic);
    private readonly Hash256 _refundedTopic = new(DatabaseSchema.InvitationRefunded.Topic);
    private readonly Hash256 _revokedTopic = new(DatabaseSchema.InvitationRevoked.Topic);

    public IRollbackCache[] Caches { get; } = [];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        yield break;
    }

    public IEnumerable<IIndexEvent> ParseLog(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        bool hasTopics = log.Topics.Length > 0;
        bool isFromEscrow = escrowAddresses.Contains(log.Address);

        if (!hasTopics || !isFromEscrow)
        {
            yield break;
        }

        var topic = log.Topics[0];

        bool isEscrowed = topic == _escrowedTopic;
        bool isRedeemed = topic == _redeemedTopic;
        bool isRefunded = topic == _refundedTopic;
        bool isRevoked = topic == _revokedTopic;

        if (isEscrowed)
        {
            yield return ParseInvitationEscrowed(block, receipt, log, logIndex);
            yield break;
        }

        if (isRedeemed)
        {
            yield return ParseInvitationRedeemed(block, receipt, log, logIndex);
            yield break;
        }

        if (isRefunded)
        {
            yield return ParseInvitationRefunded(block, receipt, log, logIndex);
            yield break;
        }

        if (isRevoked)
        {
            yield return ParseInvitationRevoked(block, receipt, log, logIndex);
            yield break;
        }
    }

    private Events.InvitationEscrowed ParseInvitationEscrowed(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string invitee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);

        return new Events.InvitationEscrowed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            inviter,
            invitee,
            amount
        );
    }

    private Events.InvitationRedeemed ParseInvitationRedeemed(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string invitee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);

        return new Events.InvitationRedeemed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            inviter,
            invitee,
            amount
        );
    }

    private Events.InvitationRefunded ParseInvitationRefunded(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string invitee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);

        return new Events.InvitationRefunded(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            inviter,
            invitee,
            amount
        );
    }

    private Events.InvitationRevoked ParseInvitationRevoked(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string invitee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);

        return new Events.InvitationRevoked(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            inviter,
            invitee,
            amount
        );
    }
}
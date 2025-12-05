using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.InvitationsAtScale;

/// <summary>
/// Parses logs emitted by the Invitations at Scale contracts:
/// - InvitationModule
/// - ReferralsModule
/// - InvitationFarm
/// </summary>
public class LogParser(
    Address invitationModuleAddress,
    Address referralsModuleAddress,
    Address invitationFarmAddress) : ILogParser
{
    // InvitationModule topics
    private readonly Hash256 _registerHumanTopic = new(DatabaseSchema.RegisterHuman.Topic);

    // ReferralsModule topics
    private readonly Hash256 _accountCreatedTopic = new(DatabaseSchema.AccountCreated.Topic);
    private readonly Hash256 _accountClaimedTopic = new(DatabaseSchema.AccountClaimed.Topic);

    // InvitationFarm topics
    private readonly Hash256 _adminSetTopic = new(DatabaseSchema.AdminSet.Topic);
    private readonly Hash256 _maintainerSetTopic = new(DatabaseSchema.MaintainerSet.Topic);
    private readonly Hash256 _seederSetTopic = new(DatabaseSchema.SeederSet.Topic);
    private readonly Hash256 _inviterQuotaUpdatedTopic = new(DatabaseSchema.InviterQuotaUpdated.Topic);
    private readonly Hash256 _invitationModuleUpdatedTopic = new(DatabaseSchema.InvitationModuleUpdated.Topic);
    private readonly Hash256 _botCreatedTopic = new(DatabaseSchema.BotCreated.Topic);
    private readonly Hash256 _invitesClaimedTopic = new(DatabaseSchema.InvitesClaimed.Topic);
    private readonly Hash256 _farmGrownTopic = new(DatabaseSchema.FarmGrown.Topic);

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
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        bool isFromInvitationModule = log.Address == invitationModuleAddress;
        bool isFromReferralsModule = log.Address == referralsModuleAddress;
        bool isFromInvitationFarm = log.Address == invitationFarmAddress;

        if (!isFromInvitationModule && !isFromReferralsModule && !isFromInvitationFarm)
        {
            yield break;
        }

        var topic = log.Topics[0];

        // InvitationModule events
        if (isFromInvitationModule)
        {
            if (topic == _registerHumanTopic)
            {
                yield return ParseRegisterHuman(block, receipt, log, logIndex);
                yield break;
            }
        }

        // ReferralsModule events
        if (isFromReferralsModule)
        {
            if (topic == _accountCreatedTopic)
            {
                yield return ParseAccountCreated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _accountClaimedTopic)
            {
                yield return ParseAccountClaimed(block, receipt, log, logIndex);
                yield break;
            }
        }

        // InvitationFarm events
        if (isFromInvitationFarm)
        {
            if (topic == _adminSetTopic)
            {
                yield return ParseAdminSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _maintainerSetTopic)
            {
                yield return ParseMaintainerSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _seederSetTopic)
            {
                yield return ParseSeederSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _inviterQuotaUpdatedTopic)
            {
                yield return ParseInviterQuotaUpdated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _invitationModuleUpdatedTopic)
            {
                yield return ParseInvitationModuleUpdated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _botCreatedTopic)
            {
                yield return ParseBotCreated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _invitesClaimedTopic)
            {
                yield return ParseInvitesClaimed(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _farmGrownTopic)
            {
                yield return ParseFarmGrown(block, receipt, log, logIndex);
                yield break;
            }
        }
    }

    // ============================================
    // InvitationModule Parsers
    // ============================================

    private Events.RegisterHuman ParseRegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterHuman(address indexed human, address indexed originInviter, address indexed proxyInviter)
        string human = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string originInviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string proxyInviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        return new Events.RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            human,
            originInviter,
            proxyInviter
        );
    }

    // ============================================
    // ReferralsModule Parsers
    // ============================================

    private Events.AccountCreated ParseAccountCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event AccountCreated(address indexed account)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.AccountCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account
        );
    }

    private Events.AccountClaimed ParseAccountClaimed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event AccountClaimed(address indexed account)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.AccountClaimed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account
        );
    }

    // ============================================
    // InvitationFarm Parsers
    // ============================================

    private Events.AdminSet ParseAdminSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event AdminSet(address indexed newAdmin)
        string newAdmin = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.AdminSet(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            newAdmin
        );
    }

    private Events.MaintainerSet ParseMaintainerSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event MaintainerSet(address indexed maintainer)
        string maintainer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.MaintainerSet(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            maintainer
        );
    }

    private Events.SeederSet ParseSeederSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event SeederSet(address indexed seeder)
        string seeder = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.SeederSet(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            seeder
        );
    }

    private Events.InviterQuotaUpdated ParseInviterQuotaUpdated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event InviterQuotaUpdated(address indexed inviter, uint256 indexed quota)
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var quota = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);

        return new Events.InviterQuotaUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            inviter,
            quota
        );
    }

    private Events.InvitationModuleUpdated ParseInvitationModuleUpdated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event InvitationModuleUpdated(address indexed module, address indexed genericCallProxy)
        string module = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string genericCallProxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        return new Events.InvitationModuleUpdated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            module,
            genericCallProxy
        );
    }

    private Events.BotCreated ParseBotCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event BotCreated(address indexed createdBot)
        string createdBot = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        return new Events.BotCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            createdBot
        );
    }

    private Events.InvitesClaimed ParseInvitesClaimed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event InvitesClaimed(address indexed inviter, uint256 indexed count)
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var count = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);

        return new Events.InvitesClaimed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            inviter,
            count
        );
    }

    private Events.FarmGrown ParseFarmGrown(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event FarmGrown(address indexed maintainer, uint256 indexed numberOfBots, uint256 indexed totalNumberOfBots)
        string maintainer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var numberOfBots = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        var totalNumberOfBots = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);

        return new Events.FarmGrown(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            maintainer,
            numberOfBots,
            totalNumberOfBots
        );
    }
}

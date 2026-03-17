using System.Collections.Immutable;
using Circles.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2.InvitationsAtScale;

/// <summary>
/// Parses logs emitted by the Invitations at Scale contracts:
/// - InvitationFarm (admin, maintainer, seeder, quota, module updates, bots, invites, farm growth)
/// - InvitationModule (human registration)
/// - ReferralsModule (account creation/claiming)
/// - InvitationQuotaGrantModule (quota permissions, quota setting)
/// </summary>
public class LogParser(
    ImmutableHashSet<Address> farmAddresses,
    ImmutableHashSet<Address> invitationModuleAddresses,
    ImmutableHashSet<Address> referralsModuleAddresses,
    ImmutableHashSet<Address> quotaGrantModuleAddresses) : ILogParser
{
    // InvitationFarm topics
    private static readonly Hash256 _adminSet = new(DatabaseSchema.AdminSet.Topic);
    private static readonly Hash256 _maintainerSet = new(DatabaseSchema.MaintainerSet.Topic);
    private static readonly Hash256 _seederSet = new(DatabaseSchema.SeederSet.Topic);
    private static readonly Hash256 _inviterQuotaUpdated = new(DatabaseSchema.InviterQuotaUpdated.Topic);
    private static readonly Hash256 _invitationModuleUpdated = new(DatabaseSchema.InvitationModuleUpdated.Topic);
    private static readonly Hash256 _botCreated = new(DatabaseSchema.BotCreated.Topic);
    private static readonly Hash256 _invitesClaimed = new(DatabaseSchema.InvitesClaimed.Topic);
    private static readonly Hash256 _farmGrown = new(DatabaseSchema.FarmGrown.Topic);

    // InvitationModule topic
    private static readonly Hash256 _registerHuman = new(DatabaseSchema.RegisterHuman.Topic);

    // ReferralsModule topics
    private static readonly Hash256 _accountCreated = new(DatabaseSchema.AccountCreated.Topic);
    private static readonly Hash256 _accountClaimed = new(DatabaseSchema.AccountClaimed.Topic);

    // InvitationQuotaGrantModule topics
    private static readonly Hash256 _quotaPermissionGranted = new(DatabaseSchema.QuotaPermissionGranted.Topic);
    private static readonly Hash256 _quotaPermissionRevoked = new(DatabaseSchema.QuotaPermissionRevoked.Topic);
    private static readonly Hash256 _inviterQuotaSet = new(DatabaseSchema.InviterQuotaSet.Topic);
    private static readonly Hash256 _inviterExtraQuotaAdded = new(DatabaseSchema.InviterExtraQuotaAdded.Topic);

    // Caches for dynamic address discovery
    public static readonly RollbackCache<Address, object?> Farms = new("InvitationsAtScale_Farms");
    public static readonly RollbackCache<Address, object?> InvitationModules = new("InvitationsAtScale_InvitationModules");
    public static readonly RollbackCache<Address, object?> ReferralsModules = new("InvitationsAtScale_ReferralsModules");
    public static readonly RollbackCache<Address, object?> QuotaGrantModules = new("InvitationsAtScale_QuotaGrantModules");

    public IRollbackCache[] Caches { get; } = [Farms, InvitationModules, ReferralsModules, QuotaGrantModules];

    public IReadOnlySet<Hash256> CacheWritingTopics { get; } = new HashSet<Hash256>
    {
        new(DatabaseSchema.AdminSet.Topic),
        new(DatabaseSchema.InvitationModuleUpdated.Topic)
    };

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        // Farm emitters
        var farmSeeds = new Dictionary<Address, object?>();
        foreach (var address in farmAddresses) farmSeeds[address] = null;
        SeedEmitterAddresses(database, farmSeeds, nameof(Events.AdminSet));
        SeedEmitterAddresses(database, farmSeeds, nameof(Events.InvitationModuleUpdated));
        Farms.Seed(farmSeeds);

        // Invitation module emitters
        var moduleSeeds = new Dictionary<Address, object?>();
        foreach (var address in invitationModuleAddresses) moduleSeeds[address] = null;
        SeedEmitterAddresses(database, moduleSeeds, nameof(Events.RegisterHuman));
        SeedAddressesFromField(database, moduleSeeds, nameof(Events.InvitationModuleUpdated), "module");
        InvitationModules.Seed(moduleSeeds);

        // Referrals module emitters
        var referralsSeeds = new Dictionary<Address, object?>();
        foreach (var address in referralsModuleAddresses) referralsSeeds[address] = null;
        SeedEmitterAddresses(database, referralsSeeds, nameof(Events.AccountCreated));
        ReferralsModules.Seed(referralsSeeds);

        // Quota grant module emitters
        var quotaSeeds = new Dictionary<Address, object?>();
        foreach (var address in quotaGrantModuleAddresses) quotaSeeds[address] = null;
        SeedEmitterAddresses(database, quotaSeeds, nameof(Events.QuotaPermissionGranted));
        SeedEmitterAddresses(database, quotaSeeds, nameof(Events.InviterQuotaSet));
        QuotaGrantModules.Seed(quotaSeeds);

        logger.Info($" * Cached {farmSeeds.Count} InvitationsAtScale farms");
        logger.Info($" * Cached {moduleSeeds.Count} InvitationsAtScale invitation modules");
        logger.Info($" * Cached {referralsSeeds.Count} InvitationsAtScale referrals modules");
        logger.Info($" * Cached {quotaSeeds.Count} InvitationsAtScale quota grant modules");

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

        var topic = log.Topics[0];

        if (Farms.ContainsKey(log.Address))
        {
            if (topic == _adminSet)
            {
                yield return ParseAdminSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _maintainerSet)
            {
                yield return ParseMaintainerSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _seederSet)
            {
                yield return ParseSeederSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _inviterQuotaUpdated)
            {
                yield return ParseInviterQuotaUpdated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _invitationModuleUpdated)
            {
                var evt = ParseInvitationModuleUpdated(block, receipt, log, logIndex);
                InvitationModules.Add(block.Number, new Address(evt.Module), null);
                yield return evt;
                yield break;
            }

            if (topic == _botCreated)
            {
                yield return ParseBotCreated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _invitesClaimed)
            {
                yield return ParseInvitesClaimed(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _farmGrown)
            {
                yield return ParseFarmGrown(block, receipt, log, logIndex);
                yield break;
            }
        }

        if (InvitationModules.ContainsKey(log.Address) && topic == _registerHuman)
        {
            yield return ParseRegisterHuman(block, receipt, log, logIndex);
            yield break;
        }

        if (ReferralsModules.ContainsKey(log.Address))
        {
            if (topic == _accountCreated)
            {
                yield return ParseAccountCreated(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _accountClaimed)
            {
                yield return ParseAccountClaimed(block, receipt, log, logIndex);
                yield break;
            }
        }

        if (QuotaGrantModules.ContainsKey(log.Address))
        {
            if (topic == _quotaPermissionGranted)
            {
                yield return ParseQuotaPermissionGranted(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _quotaPermissionRevoked)
            {
                yield return ParseQuotaPermissionRevoked(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _inviterQuotaSet)
            {
                yield return ParseInviterQuotaSet(block, receipt, log, logIndex);
                yield break;
            }

            if (topic == _inviterExtraQuotaAdded)
            {
                yield return ParseInviterExtraQuotaAdded(block, receipt, log, logIndex);
            }
        }
    }

    private static void SeedEmitterAddresses(IDatabase database, IDictionary<Address, object?> sink, string table)
    {
        var select = new Query.Select(DatabaseSchema.Namespace, table, ["emitter"], [], [], int.MaxValue, false,
            int.MaxValue);

        ParameterizedSql sql;
        try
        {
            sql = select.ToSql(database);
        }
        catch (InvalidOperationException)
        {
            // Backward compatibility: if the table doesn't exist yet, skip DB seeding
            return;
        }

        foreach (var row in database.Select(sql).Rows)
        {
            if (row[0] is null) continue;
            sink[new Address(row[0].ToString()!)] = null;
        }
    }

    private static void SeedAddressesFromField(IDatabase database, IDictionary<Address, object?> sink, string table,
        string field)
    {
        var select = new Query.Select(DatabaseSchema.Namespace, table, [field], [], [], int.MaxValue, false,
            int.MaxValue);

        ParameterizedSql sql;
        try
        {
            sql = select.ToSql(database);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var row in database.Select(sql).Rows)
        {
            if (row[0] is null) continue;
            sink[new Address(row[0].ToString()!)] = null;
        }
    }

    // ============================================
    // InvitationFarm Parsers
    // ============================================

    private static Events.AdminSet ParseAdminSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string newAdmin = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.AdminSet(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), newAdmin);
    }

    private static Events.MaintainerSet ParseMaintainerSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string maintainer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.MaintainerSet(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), maintainer);
    }

    private static Events.SeederSet ParseSeederSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string seeder = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.SeederSet(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), seeder);
    }

    private static Events.InviterQuotaUpdated ParseInviterQuotaUpdated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var quota = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        return new Events.InviterQuotaUpdated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), inviter, quota);
    }

    private static Events.InvitationModuleUpdated ParseInvitationModuleUpdated(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        string module = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string genericCallProxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        return new Events.InvitationModuleUpdated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), module, genericCallProxy);
    }

    private static Events.BotCreated ParseBotCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string createdBot = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.BotCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), createdBot);
    }

    private static Events.InvitesClaimed ParseInvitesClaimed(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var count = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        return new Events.InvitesClaimed(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), inviter, count);
    }

    private static Events.FarmGrown ParseFarmGrown(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string maintainer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var numberOfBots = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        var totalNumberOfBots = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        return new Events.FarmGrown(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), maintainer, numberOfBots, totalNumberOfBots);
    }

    // ============================================
    // InvitationModule Parsers
    // ============================================

    private static Events.RegisterHuman ParseRegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string human = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string originInviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string proxyInviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        return new Events.RegisterHuman(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), human, originInviter, proxyInviter);
    }

    // ============================================
    // ReferralsModule Parsers
    // ============================================

    private static Events.AccountCreated ParseAccountCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.AccountCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), account);
    }

    private static Events.AccountClaimed ParseAccountClaimed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.AccountClaimed(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), account);
    }

    // ============================================
    // InvitationQuotaGrantModule Parsers
    // ============================================

    private static Events.QuotaPermissionGranted ParseQuotaPermissionGranted(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        string grantee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.QuotaPermissionGranted(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), grantee);
    }

    private static Events.QuotaPermissionRevoked ParseQuotaPermissionRevoked(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        string grantee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new Events.QuotaPermissionRevoked(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), grantee);
    }

    private static Events.InviterQuotaSet ParseInviterQuotaSet(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string grantee = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var quota = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        return new Events.InviterQuotaSet(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), grantee, inviter, quota);
    }

    private static Events.InviterExtraQuotaAdded ParseInviterExtraQuotaAdded(Block block, TxReceipt receipt,
        LogEntry log, int logIndex)
    {
        string inviter = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var extraQuota = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        return new Events.InviterExtraQuotaAdded(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToLowerHex(), inviter, extraQuota);
    }
}

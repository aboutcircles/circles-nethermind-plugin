using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.CirclesV2.InvitationsAtScale;

public class DatabaseSchema : BaseDatabaseSchema
{
    public const string Namespace = "CrcV2_InvitationsAtScale";

    // ============================================
    // InvitationModule Events
    // ============================================

    public static readonly EventSchema RegisterHuman = EventSchema.FromSolidity(
        Namespace,
        "event RegisterHuman(address indexed human, address indexed originInviter, address indexed proxyInviter)"
    );

    // ============================================
    // ReferralsModule Events
    // ============================================

    public static readonly EventSchema AccountCreated = EventSchema.FromSolidity(
        Namespace,
        "event AccountCreated(address indexed account)"
    );

    public static readonly EventSchema AccountClaimed = EventSchema.FromSolidity(
        Namespace,
        "event AccountClaimed(address indexed account)"
    );

    // ============================================
    // InvitationFarm Events
    // ============================================

    public static readonly EventSchema AdminSet = EventSchema.FromSolidity(
        Namespace,
        "event AdminSet(address indexed newAdmin)"
    );

    public static readonly EventSchema MaintainerSet = EventSchema.FromSolidity(
        Namespace,
        "event MaintainerSet(address indexed maintainer)"
    );

    public static readonly EventSchema SeederSet = EventSchema.FromSolidity(
        Namespace,
        "event SeederSet(address indexed seeder)"
    );

    public static readonly EventSchema InviterQuotaUpdated = EventSchema.FromSolidity(
        Namespace,
        "event InviterQuotaUpdated(address indexed inviter, uint256 indexed quota)"
    );

    public static readonly EventSchema InvitationModuleUpdated = EventSchema.FromSolidity(
        Namespace,
        "event InvitationModuleUpdated(address indexed module, address indexed genericCallProxy)"
    );

    public static readonly EventSchema BotCreated = EventSchema.FromSolidity(
        Namespace,
        "event BotCreated(address indexed createdBot)"
    );

    public static readonly EventSchema InvitesClaimed = EventSchema.FromSolidity(
        Namespace,
        "event InvitesClaimed(address indexed inviter, uint256 indexed count)"
    );

    public static readonly EventSchema FarmGrown = EventSchema.FromSolidity(
        Namespace,
        "event FarmGrown(address indexed maintainer, uint256 indexed numberOfBots, uint256 indexed totalNumberOfBots)"
    );

    public DatabaseSchema()
    {
        // ============================================
        // InvitationModule Mappings
        // ============================================

        AddMappings<Events.RegisterHuman>(
            ns: Namespace,
            table: "RegisterHuman",
            eventSchema: RegisterHuman,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("human", e => e.Human),
                ("originInviter", e => e.OriginInviter),
                ("proxyInviter", e => e.ProxyInviter)
            ]
        );

        // ============================================
        // ReferralsModule Mappings
        // ============================================

        AddMappings<Events.AccountCreated>(
            ns: Namespace,
            table: "AccountCreated",
            eventSchema: AccountCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("account", e => e.Account)
            ]
        );

        AddMappings<Events.AccountClaimed>(
            ns: Namespace,
            table: "AccountClaimed",
            eventSchema: AccountClaimed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("account", e => e.Account)
            ]
        );

        // ============================================
        // InvitationFarm Mappings
        // ============================================

        AddMappings<Events.AdminSet>(
            ns: Namespace,
            table: "AdminSet",
            eventSchema: AdminSet,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("newAdmin", e => e.NewAdmin)
            ]
        );

        AddMappings<Events.MaintainerSet>(
            ns: Namespace,
            table: "MaintainerSet",
            eventSchema: MaintainerSet,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("maintainer", e => e.Maintainer)
            ]
        );

        AddMappings<Events.SeederSet>(
            ns: Namespace,
            table: "SeederSet",
            eventSchema: SeederSet,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("seeder", e => e.Seeder)
            ]
        );

        AddMappings<Events.InviterQuotaUpdated>(
            ns: Namespace,
            table: "InviterQuotaUpdated",
            eventSchema: InviterQuotaUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("quota", e => (BigInteger)e.Quota)
            ]
        );

        AddMappings<Events.InvitationModuleUpdated>(
            ns: Namespace,
            table: "InvitationModuleUpdated",
            eventSchema: InvitationModuleUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("module", e => e.Module),
                ("genericCallProxy", e => e.GenericCallProxy)
            ]
        );

        AddMappings<Events.BotCreated>(
            ns: Namespace,
            table: "BotCreated",
            eventSchema: BotCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("createdBot", e => e.CreatedBot)
            ]
        );

        AddMappings<Events.InvitesClaimed>(
            ns: Namespace,
            table: "InvitesClaimed",
            eventSchema: InvitesClaimed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("count", e => (BigInteger)e.Count)
            ]
        );

        AddMappings<Events.FarmGrown>(
            ns: Namespace,
            table: "FarmGrown",
            eventSchema: FarmGrown,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("maintainer", e => e.Maintainer),
                ("numberOfBots", e => (BigInteger)e.NumberOfBots),
                ("totalNumberOfBots", e => (BigInteger)e.TotalNumberOfBots)
            ]
        );
    }
}

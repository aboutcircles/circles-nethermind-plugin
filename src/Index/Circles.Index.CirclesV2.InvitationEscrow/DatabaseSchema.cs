using System.Numerics;
using Circles.Common;

namespace Circles.Index.CirclesV2.InvitationEscrow;

public class DatabaseSchema : BaseDatabaseSchema
{
    public const string Namespace = "CrcV2_InvitationEscrow";

    public static readonly EventSchema InvitationEscrowed = EventSchema.FromSolidity(
        Namespace,
        "event InvitationEscrowed(address indexed inviter, address indexed invitee, uint256 indexed amount)"
    );

    public static readonly EventSchema InvitationRedeemed = EventSchema.FromSolidity(
        Namespace,
        "event InvitationRedeemed(address indexed inviter, address indexed invitee, uint256 indexed amount)"
    );

    public static readonly EventSchema InvitationRefunded = EventSchema.FromSolidity(
        Namespace,
        "event InvitationRefunded(address indexed inviter, address indexed invitee, uint256 indexed amount)"
    );

    public static readonly EventSchema InvitationRevoked = EventSchema.FromSolidity(
        Namespace,
        "event InvitationRevoked(address indexed inviter, address indexed invitee, uint256 indexed amount)"
    );

    public DatabaseSchema()
    {
        AddMappings<Events.InvitationEscrowed>(
            ns: Namespace,
            table: "InvitationEscrowed",
            eventSchema: InvitationEscrowed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("invitee", e => e.Invitee),
                ("amount",  e => (BigInteger)e.Amount)
            ]
        );

        AddMappings<Events.InvitationRedeemed>(
            ns: Namespace,
            table: "InvitationRedeemed",
            eventSchema: InvitationRedeemed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("invitee", e => e.Invitee),
                ("amount",  e => (BigInteger)e.Amount)
            ]
        );

        AddMappings<Events.InvitationRefunded>(
            ns: Namespace,
            table: "InvitationRefunded",
            eventSchema: InvitationRefunded,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("invitee", e => e.Invitee),
                ("amount",  e => (BigInteger)e.Amount)
            ]
        );

        AddMappings<Events.InvitationRevoked>(
            ns: Namespace,
            table: "InvitationRevoked",
            eventSchema: InvitationRevoked,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("inviter", e => e.Inviter),
                ("invitee", e => e.Invitee),
                ("amount",  e => (BigInteger)e.Amount)
            ]
        );
    }
}

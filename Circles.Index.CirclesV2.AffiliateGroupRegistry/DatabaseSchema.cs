using Circles.Index.Common;

namespace Circles.Index.CirclesV2.AffiliateGroupRegistry;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema AffiliateGroupChanged = EventSchema.FromSolidity(
        "CrcV2",
        "event AffiliateGroupChanged(address indexed human, address oldGroup, address newGroup)"
    );

    public static readonly EventSchema NotificationFailed = EventSchema.FromSolidity(
        "CrcV2",
        "event NotificationFailed(address indexed group, address indexed human)"
    );

    public static readonly EventSchema NotificationSuccessful = EventSchema.FromSolidity(
        "CrcV2",
        "event NotificationSuccessful(address indexed group, address indexed human)"
    );

    public DatabaseSchema()
    {
        AddMappings<AffiliateGroupChanged>(
            ns: "CrcV2",
            table: "AffiliateGroupChanged",
            eventSchema: AffiliateGroupChanged,
            databaseFieldMap:
            [
                ("emitter",  e => e.Emitter),
                ("human",    e => e.Human),
                ("oldGroup", e => e.OldGroup),
                ("newGroup", e => e.NewGroup)
            ]
        );

        AddMappings<NotificationFailed>(
            ns: "CrcV2",
            table: "AffiliateGroupNotificationFailed",
            eventSchema: NotificationFailed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group",   e => e.Group),
                ("human",   e => e.Human)
            ]
        );

        AddMappings<NotificationSuccessful>(
            ns: "CrcV2",
            table: "AffiliateGroupNotificationSuccessful",
            eventSchema: NotificationSuccessful,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group",   e => e.Group),
                ("human",   e => e.Human)
            ]
        );
    }
}
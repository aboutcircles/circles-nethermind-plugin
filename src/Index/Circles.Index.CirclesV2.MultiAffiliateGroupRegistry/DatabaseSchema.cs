using Circles.Common;

namespace Circles.Index.CirclesV2.MultiAffiliateGroupRegistry;

public class DatabaseSchema : BaseDatabaseSchema
{
    // The MultiAffiliateGroupRegistry emits both event parameters as NON-indexed
    // (affiliateGroup, avatar). FromSolidity derives the topic0 signature hash;
    // the LogParser reads both addresses out of log.Data.
    //
    // We append a synthetic `isSeed` boolean column (NOT an event param, so it does not affect the
    // topic) to record whether the Added came from initialize() seeding — the LogParser sets it from
    // the calling tx selector. The migration creates a column for every entry in EventSchema.Columns,
    // so the extra column is materialised and written via the field map below.
    public static readonly EventSchema AffiliateGroupAdded = BuildAffiliateGroupAddedSchema();

    private static EventSchema BuildAffiliateGroupAddedSchema()
    {
        var schema = EventSchema.FromSolidity(
            "CrcV2",
            "event AffiliateGroupAdded(address affiliateGroup, address avatar)"
        );
        schema.Columns.Add(new EventFieldSchema("isSeed", ValueTypes.Boolean, false));
        return schema;
    }

    public static readonly EventSchema AffiliateGroupRemoved = EventSchema.FromSolidity(
        "CrcV2",
        "event AffiliateGroupRemoved(address affiliateGroup, address avatar)"
    );

    public DatabaseSchema()
    {
        AddMappings<AffiliateGroupAdded>(
            ns: "CrcV2",
            table: "AffiliateGroupAdded",
            eventSchema: AffiliateGroupAdded,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("affiliateGroup", e => e.AffiliateGroup),
                ("avatar", e => e.Avatar),
                ("isSeed", e => e.IsSeed)
            ]
        );

        AddMappings<AffiliateGroupRemoved>(
            ns: "CrcV2",
            table: "AffiliateGroupRemoved",
            eventSchema: AffiliateGroupRemoved,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("affiliateGroup", e => e.AffiliateGroup),
                ("avatar", e => e.Avatar)
            ]
        );
    }
}

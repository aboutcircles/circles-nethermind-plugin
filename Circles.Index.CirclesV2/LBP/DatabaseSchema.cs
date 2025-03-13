using Circles.Index.Common;

namespace Circles.Index.CirclesV2.LBP;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema CirclesBackingDeployed = EventSchema.FromSolidity("CrcV2",
        "event CirclesBackingDeployed(address indexed backer, address indexed circlesBackingInstance)");

    public static readonly EventSchema LBPDeployed = EventSchema.FromSolidity("CrcV2",
        "event LBPDeployed(address indexed circlesBackingInstance, address indexed lbp)");

    public static readonly EventSchema CirclesBackingInitiated = EventSchema.FromSolidity("CrcV2",
        "event CirclesBackingInitiated(address indexed backer, address indexed circlesBackingInstance, address indexed backingAsset, address personalCirclesAddress)");

    public static readonly EventSchema CirclesBackingCompleted = EventSchema.FromSolidity("CrcV2",
        "event CirclesBackingCompleted(address indexed backer, address indexed circlesBackingInstance, address indexed lbp)");

    public static readonly EventSchema Released = EventSchema.FromSolidity("CrcV2",
        "event Released(address indexed backer, address indexed circlesBackingInstance, address indexed lbp)");

    public DatabaseSchema()
    {
        AddMappings<CirclesBackingDeployed>(
            ns: "CrcV2",
            table: "CirclesBackingDeployed",
            eventSchema: CirclesBackingDeployed,
            databaseFieldMap:
            [
                ("backer", e => e.Backer),
                ("circlesBackingInstance", e => e.CirclesBackingInstance)
            ]
        );

        AddMappings<LbpDeployed>(
            ns: "CrcV2",
            table: "LBPDeployed",
            eventSchema: LBPDeployed,
            databaseFieldMap:
            [
                ("circlesBackingInstance", e => e.CirclesBackingInstance),
                ("lbp", e => e.Lbp)
            ]
        );

        AddMappings<CirclesBackingInitiated>(
            ns: "CrcV2",
            table: "CirclesBackingInitiated",
            eventSchema: CirclesBackingInitiated,
            databaseFieldMap:
            [
                ("backer", e => e.Backer),
                ("circlesBackingInstance", e => e.CirclesBackingInstance),
                ("backingAsset", e => e.BackingAsset),
                ("personalCirclesAddress", e => e.PersonalCirclesAddress)
            ]
        );

        AddMappings<CirclesBackingCompleted>(
            ns: "CrcV2",
            table: "CirclesBackingCompleted",
            eventSchema: CirclesBackingCompleted,
            databaseFieldMap:
            [
                ("backer", e => e.Backer),
                ("circlesBackingInstance", e => e.CirclesBackingInstance),
                ("lbp", e => e.Lbp)
            ]
        );

        AddMappings<Released>(
            ns: "CrcV2",
            table: "Released",
            eventSchema: Released,
            databaseFieldMap:
            [
                ("backer", e => e.Backer),
                ("circlesBackingInstance", e => e.CirclesBackingInstance),
                ("lbp", e => e.Lbp)
            ]
        );
    }
}
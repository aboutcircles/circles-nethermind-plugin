using Circles.Common;

namespace Circles.Index.CirclesV2.LBP;

public class DatabaseSchema : BaseDatabaseSchema
{
    // public static readonly EventSchema CirclesBackingDeployed = EventSchema.FromSolidity("CrcV2",
    //     "event CirclesBackingDeployed(address indexed backer, address indexed circlesBackingInstance)");
    public static readonly EventSchema CirclesBackingDeployed = new("CrcV2", "CirclesBackingDeployed",
        KeccakHelper.ComputeHash("CirclesBackingDeployed(address,address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("backer", ValueTypes.Address, true),
            new("circlesBackingInstance", ValueTypes.Address, true),
        ]);

    // public static readonly EventSchema LBPDeployed = EventSchema.FromSolidity("CrcV2",
    //     "event LBPDeployed(address indexed circlesBackingInstance, address indexed lbp)");
    public static readonly EventSchema LBPDeployed = new("CrcV2", "LBPDeployed",
        KeccakHelper.ComputeHash("LBPDeployed(address,address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("circlesBackingInstance", ValueTypes.Address, true),
            new("lbp", ValueTypes.Address, true),
        ]);

    // public static readonly EventSchema CirclesBackingInitiated = EventSchema.FromSolidity("CrcV2",
    //     "event CirclesBackingInitiated(address indexed backer, address indexed circlesBackingInstance, address indexed backingAsset, address personalCirclesAddress)");
    public static readonly EventSchema CirclesBackingInitiated = new("CrcV2", "CirclesBackingInitiated",
        KeccakHelper.ComputeHash("CirclesBackingInitiated(address,address,address,address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("backer", ValueTypes.Address, true),
            new("circlesBackingInstance", ValueTypes.Address, true),
            new("backingAsset", ValueTypes.Address, true),
            new("personalCirclesAddress", ValueTypes.Address, true),
        ]);

    // public static readonly EventSchema CirclesBackingCompleted = EventSchema.FromSolidity("CrcV2",
    //     "event CirclesBackingCompleted(address indexed backer, address indexed circlesBackingInstance, address indexed lbp)");
    public static readonly EventSchema CirclesBackingCompleted = new("CrcV2", "CirclesBackingCompleted",
        KeccakHelper.ComputeHash("CirclesBackingCompleted(address,address,address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("backer", ValueTypes.Address, true),
            new("circlesBackingInstance", ValueTypes.Address, true),
            new("lbp", ValueTypes.Address, true)
        ]);

    // public static readonly EventSchema Released = EventSchema.FromSolidity("CrcV2",
    //     "event Released(address indexed backer, address indexed circlesBackingInstance, address indexed lbp)");
    public static readonly EventSchema Released = new("CrcV2", "Released",
        KeccakHelper.ComputeHash("Released(address,address,address)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("backer", ValueTypes.Address, true),
            new("circlesBackingInstance", ValueTypes.Address, true),
            new("lbp", ValueTypes.Address, true)
        ]);

    public DatabaseSchema()
    {
        AddMappings<CirclesBackingDeployed>(
            ns: "CrcV2",
            table: "CirclesBackingDeployed",
            eventSchema: CirclesBackingDeployed,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
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
                ("emitter", e => e.Emitter),
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
                ("emitter", e => e.Emitter),
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
                ("emitter", e => e.Emitter),
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
                ("emitter", e => e.Emitter),
                ("backer", e => e.Backer),
                ("circlesBackingInstance", e => e.CirclesBackingInstance),
                ("lbp", e => e.Lbp)
            ]
        );
    }
}
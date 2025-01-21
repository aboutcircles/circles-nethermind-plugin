using Circles.Index.Common;

namespace Circles.Index.CirclesV2.LBP;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();


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

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("CrcV2", "CirclesBackingDeployed"),
                CirclesBackingDeployed
            },
            {
                ("CrcV2", "LBPDeployed"),
                LBPDeployed
            },
            {
                ("CrcV2", "CirclesBackingInitiated"),
                CirclesBackingInitiated
            },
            {
                ("CrcV2", "CirclesBackingCompleted"),
                CirclesBackingCompleted
            },
            {
                ("CrcV2", "Released"),
                Released
            }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CirclesBackingDeployed>(("CrcV2", "CirclesBackingDeployed"));
        SchemaPropertyMap.Add(("CrcV2", "CirclesBackingDeployed"),
            new Dictionary<string, Func<CirclesBackingDeployed, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "backer", e => e.Backer },
                { "circlesBackingInstance", e => e.CirclesBackingInstance }
            });

        EventDtoTableMap.Add<LbpDeployed>(("CrcV2", "LBPDeployed"));
        SchemaPropertyMap.Add(("CrcV2", "LBPDeployed"),
            new Dictionary<string, Func<LbpDeployed, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "circlesBackingInstance", e => e.CirclesBackingInstance },
                { "lbp", e => e.Lbp }
            });

        EventDtoTableMap.Add<CirclesBackingInitiated>(("CrcV2", "CirclesBackingInitiated"));
        SchemaPropertyMap.Add(("CrcV2", "CirclesBackingInitiated"),
            new Dictionary<string, Func<CirclesBackingInitiated, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "backer", e => e.Backer },
                { "circlesBackingInstance", e => e.CirclesBackingInstance },
                { "backingAsset", e => e.BackingAsset },
                { "personalCirclesAddress", e => e.PersonalCirclesAddress }
            });

        EventDtoTableMap.Add<CirclesBackingCompleted>(("CrcV2", "CirclesBackingCompleted"));
        SchemaPropertyMap.Add(("CrcV2", "CirclesBackingCompleted"),
            new Dictionary<string, Func<CirclesBackingCompleted, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "backer", e => e.Backer },
                { "circlesBackingInstance", e => e.CirclesBackingInstance },
                { "lbp", e => e.Lbp }
            });

        EventDtoTableMap.Add<Released>(("CrcV2", "Released"));
        SchemaPropertyMap.Add(("CrcV2", "Released"),
            new Dictionary<string, Func<Released, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "backer", e => e.Backer },
                { "circlesBackingInstance", e => e.CirclesBackingInstance },
                { "lbp", e => e.Lbp }
            });
    }
}
using Circles.Index.Common;

namespace Circles.Index.CirclesV2.CMGroupDeployer;

public record CMGroupCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Proxy,
    string Owner,
    string MintHandler,
    string RedemptionHandler) : IIndexedEventV2;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema CMGroupCreated = EventSchema.FromSolidity("CrcV2",
        "event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler)");

    public DatabaseSchema()
    {
        AddMappings<CMGroupCreated>(
            ns: "CrcV2",
            table: "CMGroupCreated",
            eventSchema: CMGroupCreated,
            databaseFieldMap:
            [
                ("proxy", e => e.Proxy),
                ("owner", e => e.Owner),
                ("mintHandler", e => e.MintHandler),
                ("redemptionHandler", e => e.RedemptionHandler)
            ]
        );
    }
}
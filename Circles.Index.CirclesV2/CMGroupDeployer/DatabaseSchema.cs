using Circles.Index.Common;

namespace Circles.Index.CirclesV2.CMGroupDeployer;

public record CMGroupCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Proxy,
    string Owner,
    string MintHandler,
    string RedemptionHandler,
    string LiquidityProvider) : IIndexedEventV2;

public class DatabaseSchema : BaseDatabaseSchema
{
    // public static readonly EventSchema CMGroupCreated = EventSchema.FromSolidity("CrcV2",
    //     "event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler)");
    public static readonly EventSchema CmgRoupCreated = new("CrcV2", "CMGroupCreated",
        new(new byte[32]), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("proxy", ValueTypes.String, true),
            new("owner", ValueTypes.String, true),
            new("mintHandler", ValueTypes.String, true),
            new("redemptionHandler", ValueTypes.String, true),
            new("liquidityProvider", ValueTypes.String, true)
        ]);

    public DatabaseSchema()
    {
        AddMappings<CMGroupCreated>(
            ns: "CrcV2",
            table: "CMGroupCreated",
            eventSchema: CmgRoupCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("proxy", e => e.Proxy),
                ("owner", e => e.Owner),
                ("mintHandler", e => e.MintHandler),
                ("redemptionHandler", e => e.RedemptionHandler),
                ("liquidityProvider", e => e.LiquidityProvider)
            ]
        );
    }
}
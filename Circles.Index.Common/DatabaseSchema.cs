using System.Numerics;
using System.Text.Json;
using Nethermind.Core;

namespace Circles.Index.Common;

public record BlockWithEventCounts(Block Block, IDictionary<string, int> EventCounts);

public record EventTableHead(string TableName, int BlockNumber);

public record PathfinderRequestLog
{
    public long BlockNumber { get; set; }
    public string RequestId { get; set; }
    public long Timestamp { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public BigInteger? TargetFlow { get; set; }
    public bool? WithWrap { get; set; }
    public string[] FromTokens { get; set; }
    public string[] ToTokens { get; set; }
    public string[] ExcludeFromTokens { get; set; }
    public string[] ExcludeToTokens { get; set; }
    public string SimulatedBalances { get; set; }
    public string SimulatedTrusts { get; set; }
    public int? MaxTransfers { get; set; }
}

public record PathfinderResponseLog
{
    public long BlockNumber { get; set; }
    public string RequestId { get; set; }
    public long Timestamp { get; set; }
    public BigInteger? MaxFlow { get; set; }
    public string Transfers { get; set; }
}

public class DatabaseSchema : IDatabaseSchema
{
    public const string SystemNamespace = "System";
    public const string BlockTable = "Block";
    public const string EventTableHead = "EventTableHead";
    public const string PathfinderRequestLog = "PathfinderRequestLog";
    public const string PathfinderResponseLog = "PathfinderResponseLog";

    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("System", BlockTable),
                new EventSchema("System", BlockTable, new byte[32], [
                    new("blockNumber", ValueTypes.Int, false),
                    new("timestamp", ValueTypes.Int, true),
                    new("blockHash", ValueTypes.String, false),
                    new("eventCounts", ValueTypes.String, false)
                ])
            },
            {
                ("System", EventTableHead),
                new EventSchema("System", EventTableHead, new byte[32], [
                    new("tableName", ValueTypes.String, false),
                    new("blockNumber", ValueTypes.Int, false)
                ])
            },
            {
                ("System", PathfinderRequestLog),
                new("System", PathfinderRequestLog, new byte[32],
                [
                    new("blockNumber", ValueTypes.Int, true, true),
                    new("requestId", ValueTypes.String, true, true),
                    new("timestamp", ValueTypes.Int, true),
                    new("from", ValueTypes.String, true),
                    new("to", ValueTypes.String, true),
                    new("targetFlow", ValueTypes.BigInt, false),
                    new("withWrap", ValueTypes.Boolean, false),
                    new("fromTokens", ValueTypes.AddressArray, false),
                    new("toTokens", ValueTypes.AddressArray, false),
                    new("excludeFromTokens", ValueTypes.AddressArray, false),
                    new("excludeToTokens", ValueTypes.AddressArray, false),
                    new("simulatedBalances", ValueTypes.String, false),
                    new("simulatedTrusts", ValueTypes.String, false),
                    new("maxTransfers", ValueTypes.Int, false),
                ])
            },
            {
                ("System", PathfinderResponseLog),
                new("System", PathfinderResponseLog, new byte[32],
                [
                    new("requestId", ValueTypes.String, true, true),
                    new("timestamp", ValueTypes.Int, true),
                    new("maxFlow", ValueTypes.BigInt, false),
                    new("transfers", ValueTypes.String, false),
                ])
            }
        };

    public IDictionary<string, string> Indexes { get; } = new Dictionary<string, string>();

    public DatabaseSchema()
    {
        SchemaPropertyMap.Add(("System", BlockTable), new Dictionary<string, Func<BlockWithEventCounts, object?>>
        {
            { "blockNumber", o => o.Block.Number },
            { "timestamp", o => (long)o.Block.Timestamp },
            { "blockHash", o => o.Block.Hash!.ToString() },
            { "eventCounts", o => JsonSerializer.Serialize(o.EventCounts) }
        });
        SchemaPropertyMap.Add(("System", EventTableHead), new Dictionary<string, Func<EventTableHead, object?>>
        {
            { "tableName", rec => rec.TableName },
            { "blockNumber", rec => rec.BlockNumber }
        });
        SchemaPropertyMap.Add(("System", PathfinderRequestLog),
            new Dictionary<string, Func<PathfinderRequestLog, object?>>
            {
                { "blockNumber", rec => rec.BlockNumber },
                { "requestId", rec => rec.RequestId },
                { "timestamp", rec => rec.Timestamp },
                { "from", rec => rec.From },
                { "to", rec => rec.To },
                { "targetFlow", rec => rec.TargetFlow },
                { "withWrap", rec => rec.WithWrap },
                { "fromTokens", rec => rec.FromTokens },
                { "toTokens", rec => rec.ToTokens },
                { "excludeFromTokens", rec => rec.ExcludeFromTokens },
                { "excludeToTokens", rec => rec.ExcludeToTokens },
                { "simulatedBalances", rec => rec.SimulatedBalances },
                { "simulatedTrusts", rec => rec.SimulatedTrusts },
                { "maxTransfers", rec => rec.MaxTransfers }
            });

        SchemaPropertyMap.Add(("System", PathfinderResponseLog),
            new Dictionary<string, Func<PathfinderResponseLog, object?>>
            {
                { "blockNumber", rec => rec.BlockNumber },
                { "requestId", rec => rec.RequestId },
                { "timestamp", rec => rec.Timestamp },
                { "maxFlow", rec => rec.MaxFlow },
                { "transfers", rec => rec.Transfers }
            });
    }
}
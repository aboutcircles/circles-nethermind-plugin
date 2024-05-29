using System.Text.Json;
using Nethermind.Core;

namespace Circles.Index.Common;

public record BlockWithEventCounts(Block Block, IDictionary<string, int> EventCounts);

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("System", "Block"),
                new EventSchema("System", "Block", new byte[32], [
                    new("blockNumber", ValueTypes.Int, false),
                    new("timestamp", ValueTypes.Int, true),
                    new("blockHash", ValueTypes.String, false),
                    new("eventCounts", ValueTypes.String, false)
                ])
            }
        };

    public DatabaseSchema()
    {
        SchemaPropertyMap.Add(("System", "Block"), new Dictionary<string, Func<BlockWithEventCounts, object?>>
        {
            { "blockNumber", o => o.Block.Number },
            { "timestamp", o => (long)o.Block.Timestamp },
            { "blockHash", o => o.Block.Hash!.ToString() },
            { "eventCounts", o => JsonSerializer.Serialize(o.EventCounts) }
        });
    }
}
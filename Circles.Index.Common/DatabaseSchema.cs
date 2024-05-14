using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

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
                    new("blockHash", ValueTypes.String, false)
                ])
            }
        };
}
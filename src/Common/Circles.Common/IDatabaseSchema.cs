namespace Circles.Index.Common;

public interface IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; }

    public IEventDtoTableMap EventDtoTableMap { get; }

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }

    /// <summary>
    /// A list of indexes that should be created in the database.
    /// Must be written in an idempotent way, so that the same index can be created multiple times without error.
    /// </summary>
    public IDictionary<string, string> Indexes { get; }
}

public abstract class BaseDatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>();

    public IDictionary<string, string> Indexes { get; } =
        new Dictionary<string, string>();

    protected void AddMappings<TEvent>(
        string ns,
        string table,
        EventSchema eventSchema,
        (string name, Func<TEvent, object?> getter)[] databaseFieldMap)
        where TEvent : IIndexEvent
    {
        // 1) Put the EventSchema in the Tables dictionary
        Tables[(ns, table)] = eventSchema;

        // 2) Register TEvent for this table
        EventDtoTableMap.Add<TEvent>((ns, table));

        // 3) Build the property map
        var map = new Dictionary<string, Func<TEvent, object?>>
        {
            // Everyone has the same basic fields:
            ["blockNumber"] = e => e.BlockNumber,
            ["timestamp"] = e => e.Timestamp,
            ["transactionIndex"] = e => e.TransactionIndex,
            ["logIndex"] = e => e.LogIndex,
            ["transactionHash"] = e => e.TransactionHash
        };

        // 4) Add the custom fields for TEvent
        foreach (var (name, getter) in databaseFieldMap)
        {
            map[name] = getter;
        }

        // 5) Attach map to SchemaPropertyMap
        SchemaPropertyMap.Add((ns, table), map);
    }
}
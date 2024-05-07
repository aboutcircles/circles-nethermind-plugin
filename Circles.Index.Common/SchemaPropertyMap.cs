namespace Circles.Index.Common;

public class CompositeDatabaseSchema : IDatabaseSchema
{
    public IDictionary<string, EventSchema> Tables { get; }

    public CompositeDatabaseSchema(IDatabaseSchema[] components)
    {
        Tables = components
            .SelectMany(c => c.Tables)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );
    }
}

public interface ISchemaPropertyMap
{
    Dictionary<string, Dictionary<string, Func<object, object?>>> Map { get; }
}

public class SchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<string, Dictionary<string, Func<object, object?>>> Map { get; } = new();

    public void Add<TEvent>(string table, Dictionary<string, Func<TEvent, object?>> map)
    {
        Map[table] = map.ToDictionary(
            pair => pair.Key,
            pair => new Func<object, object?>(eventArg => pair.Value((TEvent)eventArg))
        );
    }
}

public class CompositeSchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<string, Dictionary<string, Func<object, object?>>> Map { get; }

    public CompositeSchemaPropertyMap(SchemaPropertyMap[] components)
    {
        Map = components
            .SelectMany(c => c.Map)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );
    }
}

public interface IEventDtoTableMap
{
    Dictionary<Type, string> Map { get; }
}

public class EventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, string> Map { get; } = new();

    public void Add<TEvent>(string table)
        where TEvent : IIndexEvent
    {
        Map[typeof(TEvent)] = table;
    }
}

public class CompositeEventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, string> Map { get; }

    public CompositeEventDtoTableMap(EventDtoTableMap[] components)
    {
        Map = components
            .SelectMany(c => c.Map)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );
    }
}
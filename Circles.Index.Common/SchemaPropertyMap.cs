namespace Circles.Index.Common;

public class CompositeDatabaseSchema : IDatabaseSchema
{
    public IDictionary<Tables, TableSchema> Tables { get; }

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
    Dictionary<Tables, Dictionary<Columns, Func<IIndexEvent, object?>>> Map { get; }
}

public class SchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<Tables, Dictionary<Columns, Func<IIndexEvent, object?>>> Map { get; } = new();

    public void Add<TEvent>(Tables table, Dictionary<Columns, Func<TEvent, object?>> map)
        where TEvent : IIndexEvent
    {
        Map[table] = map.ToDictionary(
            pair => pair.Key,
            pair => new Func<IIndexEvent, object?>(eventArg => pair.Value((TEvent)eventArg))
        );
    }
}

public class CompositeSchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<Tables, Dictionary<Columns, Func<IIndexEvent, object?>>> Map { get; }

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
    Dictionary<Type, Tables> Map { get; }
}

public class EventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, Tables> Map { get; } = new();

    public void Add<TEvent>(Tables table)
        where TEvent : IIndexEvent
    {
        Map[typeof(TEvent)] = table;
    }
}

public class CompositeEventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, Tables> Map { get; }

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
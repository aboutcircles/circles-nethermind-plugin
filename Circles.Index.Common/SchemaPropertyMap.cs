namespace Circles.Index.Common;

public class CompositeDatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; }
    public IEventDtoTableMap EventDtoTableMap { get; }

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }

    public CompositeDatabaseSchema(IDatabaseSchema[] components)
    {
        Tables = components
            .SelectMany(c => c.Tables)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );

        SchemaPropertyMap = new CompositeSchemaPropertyMap(components.Select(o => o.SchemaPropertyMap).ToArray());
        EventDtoTableMap = new CompositeEventDtoTableMap(components.Select(o => o.EventDtoTableMap).ToArray());
    }
}

public interface ISchemaPropertyMap
{
    Dictionary<(string Namespace, string Table), Dictionary<string, Func<object, object?>>> Map { get; }

    public void Add<TEvent>((string Namespace, string Table) table, Dictionary<string, Func<TEvent, object?>> map);
}

public class SchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<(string Namespace, string Table), Dictionary<string, Func<object, object?>>> Map { get; } = new();

    public void Add<TEvent>((string Namespace, string Table) table, Dictionary<string, Func<TEvent, object?>> map)
    {
        Map[table] = map.ToDictionary(
            pair => pair.Key,
            pair => new Func<object, object?>(eventArg => pair.Value((TEvent)eventArg))
        );
    }
}

public class CompositeSchemaPropertyMap : ISchemaPropertyMap
{
    public Dictionary<(string Namespace, string Table), Dictionary<string, Func<object, object?>>> Map { get; }
    public void Add<TEvent>((string Namespace, string Table) table, Dictionary<string, Func<TEvent, object?>> map)
    {
        throw new NotImplementedException();
    }

    public CompositeSchemaPropertyMap(ISchemaPropertyMap[] components)
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
    Dictionary<Type, (string Namespace, string Table)> Map { get; }

    public void Add<TEvent>((string Namespace, string Table) table)
        where TEvent : IIndexEvent;
}

public class EventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, (string Namespace, string Table)> Map { get; } = new();

    public void Add<TEvent>((string Namespace, string Table) table)
        where TEvent : IIndexEvent
    {
        Map[typeof(TEvent)] = table;
    }
}

public class CompositeEventDtoTableMap : IEventDtoTableMap
{
    public Dictionary<Type, (string Namespace, string Table)> Map { get; }

    public void Add<TEvent>((string Namespace, string Table) table) where TEvent : IIndexEvent
    {
        throw new NotImplementedException();
    }

    public CompositeEventDtoTableMap(IEventDtoTableMap[] components)
    {
        Map = components
            .SelectMany(c => c.Map)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );
    }
}
namespace Circles.Common;

public class CompositeDatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; }
    public IEventDtoTableMap EventDtoTableMap { get; }

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }
    public IDictionary<string, string> Indexes { get; }
    public IReadOnlyList<string> FunctionSql { get; }

    public CompositeDatabaseSchema(IDatabaseSchema[] components)
    {
        // Fail fast with a clear message when two protocol schemas define the same
        // (namespace, table) — the ToDictionary below would only throw a generic
        // "key already added" without naming the colliding components.
        var duplicateTables = components
            .SelectMany(c => c.Tables.Keys.Select(key => (Key: key, Component: c.GetType().FullName)))
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateTables.Count > 0)
        {
            var details = string.Join("; ", duplicateTables.Select(g =>
                $"\"{g.Key.Namespace}_{g.Key.Table}\" defined by [{string.Join(", ", g.Select(x => x.Component))}]"));
            throw new InvalidOperationException(
                $"Duplicate table definitions across database schemas: {details}");
        }

        // Same fail-fast validation for index names, which must also be unique
        // across all composed schemas.
        var duplicateIndexes = components
            .SelectMany(c => c.Indexes.Keys.Select(key => (Key: key, Component: c.GetType().FullName)))
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateIndexes.Count > 0)
        {
            var details = string.Join("; ", duplicateIndexes.Select(g =>
                $"\"{g.Key}\" defined by [{string.Join(", ", g.Select(x => x.Component))}]"));
            throw new InvalidOperationException(
                $"Duplicate index definitions across database schemas: {details}");
        }

        Tables = components
            .SelectMany(c => c.Tables)
            .ToDictionary(
                kvp => kvp.Key
                , kvp => kvp.Value
            );

        SchemaPropertyMap = new CompositeSchemaPropertyMap(components.Select(o => o.SchemaPropertyMap).ToArray());
        EventDtoTableMap = new CompositeEventDtoTableMap(components.Select(o => o.EventDtoTableMap).ToArray());
        Indexes = new Dictionary<string, string>();
        foreach (var component in components)
        {
            foreach (var kvp in component.Indexes)
            {
                Indexes[kvp.Key] = kvp.Value;
            }
        }

        FunctionSql = components
            .SelectMany(c => c.FunctionSql)
            .ToList();
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

using NpgsqlTypes;

namespace Circles.Index.Common;

public class PropertyMap
{
    public Dictionary<Tables, Dictionary<Columns, (NpgsqlDbType type, Func<IIndexEvent, object?> extractor)>> Map { get; } = new();
    
    public void Add<TEvent>(Tables table, Dictionary<Columns, (NpgsqlDbType type, Func<TEvent, object?> extractor)> map) 
        where TEvent : IIndexEvent
    {
        Map.Add(table, map.ToDictionary(o => o.Key, o => ((NpgsqlDbType, Func<IIndexEvent, object?>))(o.Value.type, e => o.Value.extractor((TEvent)e))));
    }
}
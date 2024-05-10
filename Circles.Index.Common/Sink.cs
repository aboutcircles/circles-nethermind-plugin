using System.Collections.Concurrent;

namespace Circles.Index.Common;

public class Sink(
    IDatabase database,
    ISchemaPropertyMap schemaPropertyMap,
    IEventDtoTableMap eventDtoTableMap,
    int batchSize)
{
    private readonly InsertBuffer<object> _insertBuffer = new();

    public readonly IDatabase Database = database;

    private readonly ConcurrentDictionary<Type, long> _addedEventCounts = new();
    private readonly ConcurrentDictionary<Type, long> _importedEventCounts = new();

    public async Task AddEvent(object indexEvent)
    {
        _insertBuffer.Add(indexEvent);

        _addedEventCounts.AddOrUpdate(
            indexEvent.GetType(),
            1,
            (_, count) => count + 1);

        if (_insertBuffer.Length >= batchSize)
        {
            await Flush();
        }
    }

    public async Task Flush()
    {
        var snapshot = _insertBuffer.TakeSnapshot();

        Dictionary<Type, List<object>> eventsByType = new();

        foreach (var indexEvent in snapshot)
        {
            if (!eventsByType.TryGetValue(indexEvent.GetType(), out var typeEvents))
            {
                typeEvents = new List<object>();
                eventsByType[indexEvent.GetType()] = typeEvents;
            }

            typeEvents.Add(indexEvent);
        }

        IEnumerable<Task> tasks = eventsByType.Select(o =>
        {
            if (!eventDtoTableMap.Map.TryGetValue(o.Key, out var tableId))
            {
                // TODO: Use proper logger
                Console.WriteLine($"Warning: No table mapping for {o.Key}");
                return Task.CompletedTask;
            }

            var task = Database.WriteBatch(tableId.Namespace, tableId.Table, o.Value, schemaPropertyMap);

            return task.ContinueWith(p =>
            {
                if (p.Exception != null)
                {
                    throw p.Exception;
                }

                _importedEventCounts.AddOrUpdate(
                    o.Key,
                    o.Value.Count,
                    (_, count) => count + o.Value.Count);
            });
        });

        await Task.WhenAll(tasks);
    }
}
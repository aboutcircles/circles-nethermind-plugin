using System.Collections.Concurrent;

namespace Circles.Index.Common;

public class Sink
{
    private readonly int _batchSize;
    private readonly ISchemaPropertyMap _schemaPropertyMap;
    private readonly IEventDtoTableMap _eventDtoTableMap;
    private readonly InsertBuffer<object> _insertBuffer = new();

    private readonly MeteredCaller<object?, Task> _flush;
    private readonly MeteredCaller<object, Task> _addEvent;

    public readonly IDatabase Database;

    private readonly ConcurrentDictionary<Type, long> _addedEventCounts = new();
    private readonly ConcurrentDictionary<Type, long> _importedEventCounts = new();

    public Sink(IDatabase database, ISchemaPropertyMap schemaPropertyMap,
        IEventDtoTableMap eventDtoTableMap, int batchSize = 10000)
    {
        Database = database;

        _batchSize = batchSize;
        _schemaPropertyMap = schemaPropertyMap;
        _eventDtoTableMap = eventDtoTableMap;

        _flush = new MeteredCaller<object?, Task>("Sink.Flush", async _ => await PerformFlush());
        _addEvent = new MeteredCaller<object, Task>("Sink.AddEvent", PerformAddEvent);
    }

    public Task AddEvent(object indexEvent)
    {
        return _addEvent.Call(indexEvent);
    }

    private async Task PerformAddEvent(object indexEvent)
    {
        _insertBuffer.Add(indexEvent);

        _addedEventCounts.AddOrUpdate(
            indexEvent.GetType(),
            1,
            (_, count) => count + 1);

        if (_insertBuffer.Length >= _batchSize)
        {
            await Flush();
        }
    }

    public Task Flush()
    {
        return _flush.Call(null);
    }

    private async Task PerformFlush()
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
            if (!_eventDtoTableMap.Map.TryGetValue(o.Key, out var tableId))
            {
                // TODO: Use proper logger
                Console.WriteLine($"Warning: No table mapping for {o.Key}");
                return Task.CompletedTask;
            }

            var task = Database.WriteBatch(tableId.Namespace, tableId.Table, o.Value, _schemaPropertyMap);

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
        
        
        // Log event counts
        var sw = new StringWriter();
        await sw.WriteLineAsync("Sink stats:");
        foreach (var (eventType, count) in _addedEventCounts)
        {
            await sw.WriteLineAsync($" * Added {count} {eventType.Name} events");
        }

        foreach (var (eventType, count) in _importedEventCounts)
        {
            await sw.WriteLineAsync($" * Imported {count} {eventType.Name} events");
        }

        Console.WriteLine(sw.ToString());
    }
}
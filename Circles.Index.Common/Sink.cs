namespace Circles.Index.Common;

public class Sink
{
    private readonly int _batchSize;
    private readonly ISchemaPropertyMap _schemaPropertyMap;
    private readonly IEventDtoTableMap _eventDtoTableMap;
    private readonly InsertBuffer<IIndexEvent> _insertBuffer = new();

    private readonly MeteredCaller<object?, Task> _flush;
    private readonly MeteredCaller<IIndexEvent, Task> _addEvent;

    private readonly IDatabase _database;

    public Sink(IDatabase database, ISchemaPropertyMap schemaPropertyMap,
        IEventDtoTableMap eventDtoTableMap, int batchSize = 100000)
    {
        _database = database;
        _batchSize = batchSize;
        _schemaPropertyMap = schemaPropertyMap;
        _eventDtoTableMap = eventDtoTableMap;

        _flush = new MeteredCaller<object?, Task>("Sink.Flush", async _ => await PerformFlush());
        _addEvent = new MeteredCaller<IIndexEvent, Task>("Sink.AddEvent", PerformAddEvent);
    }

    public Task AddEvent(IIndexEvent indexEvent)
    {
        return _addEvent.Call(indexEvent);
    }

    private async Task PerformAddEvent(IIndexEvent indexEvent)
    {
        _insertBuffer.Add(indexEvent);

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

        Dictionary<Tables, List<IIndexEvent>> eventsByTable = new();

        foreach (var indexEvent in snapshot)
        {
            if (!_eventDtoTableMap.Map.TryGetValue(indexEvent.GetType(), out var table))
            {
                continue;
            }

            if (!eventsByTable.TryGetValue(table, out var tableEvents))
            {
                tableEvents = new List<IIndexEvent>();
                eventsByTable[table] = tableEvents;
            }

            tableEvents.Add(indexEvent);
        }

        List<Task> tasks = new();
        foreach (var tableEvents in eventsByTable)
        {
            var table = tableEvents.Key;
            var events = tableEvents.Value;

            tasks.Add(_database.WriteBatch(table, events, _schemaPropertyMap));
        }

        await Task.WhenAll(tasks);
    }
}
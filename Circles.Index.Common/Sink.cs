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

    public Sink(IDatabase database, ISchemaPropertyMap schemaPropertyMap,
        IEventDtoTableMap eventDtoTableMap, int batchSize = 100000)
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

        Dictionary<string, List<object>> eventsByTable = new();

        foreach (var indexEvent in snapshot)
        {
            if (!_eventDtoTableMap.Map.TryGetValue(indexEvent.GetType(), out var table))
            {
                continue;
            }

            if (!eventsByTable.TryGetValue(table, out var tableEvents))
            {
                tableEvents = new List<object>();
                eventsByTable[table] = tableEvents;
            }

            tableEvents.Add(indexEvent);
        }

        List<Task> tasks = new();
        foreach (var tableEvents in eventsByTable)
        {
            var table = tableEvents.Key;
            var events = tableEvents.Value;

            tasks.Add(Database.WriteBatch(table, events, _schemaPropertyMap));
        }

        await Task.WhenAll(tasks);
    }
}
using System.Text;

namespace Circles.Index.Common;

public class Sink(
    IDatabase database,
    ISchemaPropertyMap schemaPropertyMap,
    IEventDtoTableMap eventDtoTableMap,
    int batchSize)
{
    private readonly InsertBuffer<object> _insertBuffer = new();

    public readonly IDatabase Database = database;

    public async Task AddEvent(object indexEvent)
    {
        _insertBuffer.Add(indexEvent);

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

            return Database.WriteBatch(tableId.Namespace, tableId.Table, o.Value, schemaPropertyMap)
                .ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        var e = t.Exception.Flatten();
                        e.Handle(ex =>
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"Error writing batch to {tableId.Namespace}_{tableId.Table}");
                            sb.AppendLine($"Data: {o.Value.Count} rows:");
                            for (int i = 0; i < o.Value.Count; i++)
                            {
                                sb.AppendLine($"- {i:0000}: {o.Value[i]})");
                            }

                            sb.AppendLine(ex.ToString());
                            Console.WriteLine(sb.ToString());
                            return true;
                        });
                    }
                });
        });

        await Task.WhenAll(tasks);
    }
}
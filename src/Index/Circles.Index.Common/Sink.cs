using System.Text;
using Npgsql;

namespace Circles.Index.Common;

public class Sink(
    IDatabase database,
    ISchemaPropertyMap schemaPropertyMap,
    IEventDtoTableMap eventDtoTableMap,
    int batchSize,
    WriteMode writeMode = WriteMode.Auto)
{
    private readonly InsertBuffer<object> _insertBuffer = new();

    public readonly IDatabase Database = database;

    /// <summary>
    /// PostgreSQL error code for unique_violation (duplicate key)
    /// </summary>
    private const string UniqueViolationSqlState = "23505";

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

        IEnumerable<Task> tasks = eventsByType.Select(async o =>
        {
            if (!eventDtoTableMap.Map.TryGetValue(o.Key, out var tableId))
            {
                Console.WriteLine($"Warning: No table mapping for {o.Key}");
                return;
            }

            await WriteBatchWithMode(tableId.Namespace, tableId.Table, o.Value);
        });

        await Task.WhenAll(tasks);
    }

    private async Task WriteBatchWithMode(string @namespace, string table, List<object> data)
    {
        var tableName = $"{@namespace}_{table}";

        switch (writeMode)
        {
            case WriteMode.Copy:
                await WriteBatchCopy(@namespace, table, data, tableName);
                break;

            case WriteMode.Upsert:
                await WriteBatchUpsert(@namespace, table, data, tableName);
                break;

            case WriteMode.Auto:
            default:
                await WriteBatchAuto(@namespace, table, data, tableName);
                break;
        }
    }

    private async Task WriteBatchCopy(string @namespace, string table, List<object> data, string tableName)
    {
        try
        {
            await Database.WriteBatch(@namespace, table, data, schemaPropertyMap);
        }
        catch (Exception ex)
        {
            LogWriteError(tableName, data, ex);
            throw;
        }
    }

    private async Task WriteBatchUpsert(string @namespace, string table, List<object> data, string tableName)
    {
        try
        {
            await Database.WriteBatchWithUpsert(@namespace, table, data, schemaPropertyMap);
        }
        catch (Exception ex)
        {
            LogWriteError(tableName, data, ex);
            throw;
        }
    }

    private async Task WriteBatchAuto(string @namespace, string table, List<object> data, string tableName)
    {
        try
        {
            // Try fast COPY first
            await Database.WriteBatch(@namespace, table, data, schemaPropertyMap);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == UniqueViolationSqlState)
        {
            // Duplicate key error - fall back to upsert mode
            Console.WriteLine($"[Sink] Duplicate key detected in {tableName}, retrying with upsert mode...");

            try
            {
                await Database.WriteBatchWithUpsert(@namespace, table, data, schemaPropertyMap);
                Console.WriteLine($"[Sink] Successfully recovered {tableName} using upsert mode");
            }
            catch (Exception retryEx)
            {
                Console.WriteLine($"[Sink] Upsert retry failed for {tableName}: {retryEx.Message}");
                LogWriteError(tableName, data, retryEx);
                throw;
            }
        }
        catch (Exception ex)
        {
            LogWriteError(tableName, data, ex);
            throw;
        }
    }

    private static void LogWriteError(string tableName, List<object> data, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Error writing batch to {tableName}");
        sb.AppendLine($"Data: {data.Count} rows:");
        for (int i = 0; i < Math.Min(data.Count, 10); i++)
        {
            sb.AppendLine($"- {i:0000}: {data[i]})");
        }
        if (data.Count > 10)
        {
            sb.AppendLine($"  ... and {data.Count - 10} more rows");
        }
        sb.AppendLine(ex.ToString());
        Console.WriteLine(sb.ToString());
    }
}
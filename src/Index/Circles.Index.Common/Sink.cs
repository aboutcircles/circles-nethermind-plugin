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

        if (snapshot.IsEmpty)
            return;

        // Group events by their target table
        var batchesByTable = new Dictionary<(string Namespace, string Table), List<object>>();

        foreach (var indexEvent in snapshot)
        {
            if (!eventDtoTableMap.Map.TryGetValue(indexEvent.GetType(), out var tableId))
            {
                Console.WriteLine($"Warning: No table mapping for {indexEvent.GetType()}");
                continue;
            }

            if (!batchesByTable.TryGetValue(tableId, out var tableEvents))
            {
                tableEvents = new List<object>();
                batchesByTable[tableId] = tableEvents;
            }

            tableEvents.Add(indexEvent);
        }

        if (batchesByTable.Count == 0)
            return;

        // Write all batches atomically within a single transaction
        await WriteAllBatchesAtomic(batchesByTable);
    }

    /// <summary>
    /// Writes all batches atomically. On duplicate key error in Auto mode, retries with upsert.
    /// </summary>
    private async Task WriteAllBatchesAtomic(Dictionary<(string Namespace, string Table), List<object>> batches)
    {
        var batchesAsEnumerable = batches.ToDictionary(
            kvp => kvp.Key,
            kvp => (IEnumerable<object>)kvp.Value);

        switch (writeMode)
        {
            case WriteMode.Copy:
                await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: false);
                break;

            case WriteMode.Upsert:
                await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: true);
                break;

            case WriteMode.Auto:
            default:
                try
                {
                    // Try fast COPY first
                    await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: false);
                }
                catch (PostgresException pgEx) when (pgEx.SqlState == UniqueViolationSqlState)
                {
                    // Duplicate key error - retry entire batch with upsert mode
                    Console.WriteLine($"[Sink] Duplicate key detected in atomic batch, retrying with upsert mode...");

                    try
                    {
                        await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: true);
                        Console.WriteLine($"[Sink] Successfully recovered atomic batch using upsert mode");
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[Sink] Upsert retry failed: {retryEx.Message}");
                        throw;
                    }
                }
                break;
        }
    }

}
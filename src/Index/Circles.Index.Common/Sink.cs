using System.Diagnostics;
using Npgsql;

namespace Circles.Index.Common;

/// <summary>
/// Tracks write performance metrics for monitoring and debugging.
/// </summary>
public class WriteMetrics
{
    private long _copyWrites;
    private long _upsertWrites;
    private long _copyRetries; // Times COPY failed and fell back to upsert
    private long _totalEventsWritten;
    private long _totalCopyTimeMs;
    private long _totalUpsertTimeMs;
    private readonly object _lock = new();

    public void RecordCopyWrite(int eventCount, long elapsedMs)
    {
        lock (_lock)
        {
            _copyWrites++;
            _totalEventsWritten += eventCount;
            _totalCopyTimeMs += elapsedMs;
        }
    }

    public void RecordUpsertWrite(int eventCount, long elapsedMs, bool wasRetry = false)
    {
        lock (_lock)
        {
            _upsertWrites++;
            _totalEventsWritten += eventCount;
            _totalUpsertTimeMs += elapsedMs;
            if (wasRetry)
                _copyRetries++;
        }
    }

    public (long copyWrites, long upsertWrites, long copyRetries, long totalEvents,
            long avgCopyMs, long avgUpsertMs) GetStats()
    {
        lock (_lock)
        {
            var avgCopy = _copyWrites > 0 ? _totalCopyTimeMs / _copyWrites : 0;
            var avgUpsert = _upsertWrites > 0 ? _totalUpsertTimeMs / _upsertWrites : 0;
            return (_copyWrites, _upsertWrites, _copyRetries, _totalEventsWritten, avgCopy, avgUpsert);
        }
    }

    public void LogStats()
    {
        var (copyWrites, upsertWrites, copyRetries, totalEvents, avgCopyMs, avgUpsertMs) = GetStats();

        if (copyWrites == 0 && upsertWrites == 0)
            return;

        Console.WriteLine($"[WriteMetrics] Stats: " +
            $"COPY writes={copyWrites:N0} (avg {avgCopyMs}ms), " +
            $"Upsert writes={upsertWrites:N0} (avg {avgUpsertMs}ms), " +
            $"COPY retries={copyRetries:N0}, " +
            $"Total events={totalEvents:N0}");
    }
}

public class Sink(
    IDatabase database,
    ISchemaPropertyMap schemaPropertyMap,
    IEventDtoTableMap eventDtoTableMap,
    int batchSize,
    WriteMode writeMode = WriteMode.Auto)
{
    private readonly InsertBuffer<object> _insertBuffer = new();
    private readonly WriteMetrics _metrics = new();
    private DateTime _lastMetricsLog = DateTime.UtcNow;
    private const int MetricsLogIntervalMinutes = 5;

    public readonly IDatabase Database = database;

    /// <summary>
    /// Gets the write performance metrics for this sink.
    /// </summary>
    public WriteMetrics Metrics => _metrics;

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

        var totalEvents = batches.Values.Sum(v => v.Count);
        var sw = Stopwatch.StartNew();

        switch (writeMode)
        {
            case WriteMode.Copy:
                await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: false);
                sw.Stop();
                _metrics.RecordCopyWrite(totalEvents, sw.ElapsedMilliseconds);
                break;

            case WriteMode.Upsert:
                await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: true);
                sw.Stop();
                _metrics.RecordUpsertWrite(totalEvents, sw.ElapsedMilliseconds);
                break;

            case WriteMode.Auto:
            default:
                try
                {
                    // Try fast COPY first
                    await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: false);
                    sw.Stop();
                    _metrics.RecordCopyWrite(totalEvents, sw.ElapsedMilliseconds);
                }
                catch (PostgresException pgEx) when (pgEx.SqlState == UniqueViolationSqlState)
                {
                    // Duplicate key error - retry entire batch with upsert mode
                    Console.WriteLine($"[Sink] Duplicate key detected in atomic batch, retrying with upsert mode...");

                    var upsertSw = Stopwatch.StartNew();
                    try
                    {
                        await Database.WriteBatchesAtomic(batchesAsEnumerable, schemaPropertyMap, useUpsert: true);
                        upsertSw.Stop();
                        _metrics.RecordUpsertWrite(totalEvents, upsertSw.ElapsedMilliseconds, wasRetry: true);
                        Console.WriteLine($"[Sink] Successfully recovered atomic batch using upsert mode ({upsertSw.ElapsedMilliseconds}ms)");
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[Sink] Upsert retry failed: {retryEx.Message}");
                        throw;
                    }
                }
                break;
        }

        // Periodically log metrics
        MaybeLogMetrics();
    }

    /// <summary>
    /// Logs metrics periodically (every 5 minutes) to avoid spamming logs.
    /// </summary>
    private void MaybeLogMetrics()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMetricsLog).TotalMinutes >= MetricsLogIntervalMinutes)
        {
            _metrics.LogStats();
            _lastMetricsLog = now;
        }
    }
}
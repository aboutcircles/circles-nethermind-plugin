using System.Diagnostics;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Npgsql;

namespace Circles.Index.Backfill;

/// <summary>
/// Standalone backfill runner that doesn't depend on Nethermind types.
/// Uses EventRegistry for schema definitions and LogParser for parsing.
/// </summary>
public class BackfillRunner
{
    private readonly BackfillOptions _options;
    private readonly HttpClient _httpClient;

    public BackfillRunner(BackfillOptions options)
    {
        _options = options;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Circles Index Backfill Tool ===");
        Console.WriteLine();

        // Safety check: Verify indexer is not running
        if (_options.Force)
        {
            Console.WriteLine("⚠ Safety check BYPASSED (--force flag used)");
            Console.WriteLine();
        }
        else
        {
            var safetyCheck = await VerifyIndexerNotRunningAsync(cancellationToken);
            if (!safetyCheck)
            {
                return 1;
            }
        }

        // Validate tables
        var unsupportedTables = _options.Tables
            .Where(t => !EventRegistry.Events.ContainsKey(t))
            .ToList();

        if (unsupportedTables.Count > 0)
        {
            Console.Error.WriteLine($"Error: Unsupported tables: {string.Join(", ", unsupportedTables)}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Supported tables:");
            foreach (var table in EventRegistry.Events.Keys.OrderBy(t => t))
            {
                Console.Error.WriteLine($"  {table}");
            }
            return 1;
        }

        var targetTables = _options.Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topics = EventRegistry.GetTopicsForTables(targetTables);
        var contractAddresses = EventRegistry.GetContractAddressesForTables(targetTables);

        Console.WriteLine($"Target tables: {string.Join(", ", _options.Tables)}");
        Console.WriteLine($"Topics to match: {topics.Count}");
        if (contractAddresses != null)
            Console.WriteLine($"Contract addresses: {string.Join(", ", contractAddresses)}");
        Console.WriteLine($"From block: {_options.FromBlock:N0}");
        Console.WriteLine($"RPC URL: {_options.RpcUrl}");
        Console.WriteLine($"Batch size: {_options.BatchSize}");
        Console.WriteLine($"Dry run: {_options.DryRun}");
        Console.WriteLine();

        // Determine end block
        var toBlock = _options.ToBlock ?? await GetLatestBlockFromDbAsync();
        if (toBlock == 0)
        {
            Console.Error.WriteLine("Error: Could not determine end block. Use --to-block or ensure database has System_Block data.");
            return 1;
        }

        Console.WriteLine($"To block: {toBlock:N0}");
        Console.WriteLine($"Total blocks to process: {toBlock - _options.FromBlock + 1:N0}");
        Console.WriteLine();

        // Check for existing progress
        var progress = await GetBackfillProgressAsync(_options.Tables);
        var startBlock = _options.FromBlock;

        if (progress != null && progress.CurrentBlock > startBlock)
        {
            Console.WriteLine($"Resuming from block {progress.CurrentBlock:N0} (previous run)");
            startBlock = progress.CurrentBlock + 1;
        }

        if (startBlock > toBlock)
        {
            Console.WriteLine("Backfill already complete!");
            return 0;
        }

        // Process blocks in batches
        var sw = Stopwatch.StartNew();
        var totalEvents = 0L;
        var totalBlocks = 0L;

        Console.WriteLine($"Starting backfill from block {startBlock:N0} to {toBlock:N0}...");
        Console.WriteLine();

        for (var batchStart = startBlock; batchStart <= toBlock; batchStart += _options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + _options.BatchSize - 1, toBlock);
            var batchSw = Stopwatch.StartNew();

            // Fetch logs for this batch using eth_getLogs
            var events = await FetchAndParseLogsAsync(batchStart, batchEnd, targetTables, topics, contractAddresses, cancellationToken);

            // Write events to database
            if (!_options.DryRun && events.Count > 0)
            {
                await WriteEventsAsync(events);
            }

            // Update progress
            if (!_options.DryRun)
            {
                await SetBackfillProgressAsync(_options.Tables, batchEnd, toBlock);
            }

            totalEvents += events.Count;
            totalBlocks += batchEnd - batchStart + 1;
            batchSw.Stop();

            var blocksPerSec = (batchEnd - batchStart + 1) / Math.Max(0.001, batchSw.Elapsed.TotalSeconds);
            var progressPct = (batchEnd - _options.FromBlock + 1) * 100.0 / (toBlock - _options.FromBlock + 1);

            Console.WriteLine(
                $"Batch {batchStart:N0}-{batchEnd:N0}: {events.Count:N0} events, " +
                $"{blocksPerSec:F1} blk/s, {progressPct:F1}% complete");
        }

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Backfill Complete ===");
        Console.WriteLine($"Total blocks: {totalBlocks:N0}");
        Console.WriteLine($"Total events: {totalEvents:N0}");
        Console.WriteLine($"Duration: {sw.Elapsed}");
        Console.WriteLine($"Average: {totalBlocks / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} blocks/sec");

        if (_options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("(Dry run - no data was written to database)");
        }

        return 0;
    }

    /// <summary>
    /// Verifies that the Circles indexer is not running to prevent conflicts.
    /// </summary>
    private async Task<bool> VerifyIndexerNotRunningAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Safety check: Verifying indexer is not running...");

        // Check 1: Environment variable
        var pluginDisabled = string.Equals(
            Environment.GetEnvironmentVariable("CIRCLES_PLUGIN_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (pluginDisabled)
        {
            Console.WriteLine("  ✓ CIRCLES_PLUGIN_DISABLED=true detected");
        }
        else
        {
            Console.WriteLine("  ⚠ CIRCLES_PLUGIN_DISABLED is not set to 'true'");
        }

        // Check 2: System_Block not advancing
        Console.WriteLine("  Checking if System_Block is stable...");

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Get current max block
            await using var cmd1 = new NpgsqlCommand(
                @"SELECT MAX(""blockNumber"") FROM ""System_Block""", connection);
            var block1 = await cmd1.ExecuteScalarAsync(cancellationToken);
            var maxBlock1 = block1 is long l1 ? l1 : 0;

            if (maxBlock1 == 0)
            {
                Console.Error.WriteLine("  ✗ Error: No System_Block data found. Database may be empty.");
                return false;
            }

            // Wait 15 seconds and check again (Gnosis chain has ~5s block time)
            Console.WriteLine($"    Block at start: {maxBlock1:N0}");
            Console.WriteLine("    Waiting 15 seconds to verify block is stable...");
            await Task.Delay(15000, cancellationToken);

            await using var cmd2 = new NpgsqlCommand(
                @"SELECT MAX(""blockNumber"") FROM ""System_Block""", connection);
            var block2 = await cmd2.ExecuteScalarAsync(cancellationToken);
            var maxBlock2 = block2 is long l2 ? l2 : 0;

            Console.WriteLine($"    Block after 15s: {maxBlock2:N0}");

            if (maxBlock2 > maxBlock1)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  ✗ ERROR: System_Block is advancing!");
                Console.Error.WriteLine($"    Block advanced from {maxBlock1:N0} to {maxBlock2:N0}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  The Circles indexer appears to be running.");
                Console.Error.WriteLine("  Running backfill while the indexer is active can cause data inconsistencies.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  To fix this:");
                Console.Error.WriteLine("    1. Stop the indexer or set CIRCLES_PLUGIN_DISABLED=true");
                Console.Error.WriteLine("    2. Restart Nethermind in RPC-only mode");
                Console.Error.WriteLine("    3. Run this backfill tool again");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  If you're certain the indexer is stopped, use --force to bypass this check.");
                return false;
            }

            Console.WriteLine("  ✓ System_Block is stable (not advancing)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ✗ Error checking database: {ex.Message}");
            return false;
        }

        // Final decision
        if (!pluginDisabled)
        {
            Console.WriteLine();
            Console.WriteLine("  ⚠ Warning: CIRCLES_PLUGIN_DISABLED is not set.");
            Console.WriteLine("    System_Block appears stable, but this could be due to sync issues.");
            Console.WriteLine("    For safety, it's recommended to set CIRCLES_PLUGIN_DISABLED=true.");
            Console.WriteLine();
            Console.WriteLine("    Proceeding anyway since System_Block is not advancing...");
        }

        Console.WriteLine();
        return true;
    }

    private async Task<List<ParsedEvent>> FetchAndParseLogsAsync(
        long fromBlock,
        long toBlock,
        HashSet<string> targetTables,
        List<string> topics,
        HashSet<string>? contractAddresses,
        CancellationToken cancellationToken)
    {
        var events = new List<ParsedEvent>();

        if (topics.Count == 0)
            return events;

        // Build filter
        object filter;
        if (contractAddresses != null && contractAddresses.Count > 0)
        {
            filter = new
            {
                address = contractAddresses.ToArray(),
                fromBlock = $"0x{fromBlock:X}",
                toBlock = $"0x{toBlock:X}",
                topics = new object[] { topics.ToArray() }
            };
        }
        else
        {
            filter = new
            {
                fromBlock = $"0x{fromBlock:X}",
                toBlock = $"0x{toBlock:X}",
                topics = new object[] { topics.ToArray() }
            };
        }

        var request = new
        {
            jsonrpc = "2.0",
            method = "eth_getLogs",
            @params = new object[] { filter },
            id = 1
        };

        var response = await _httpClient.PostAsJsonAsync(_options.RpcUrl, request, cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (!json.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            if (json.TryGetProperty("error", out var error))
            {
                Console.Error.WriteLine($"RPC error: {error}");
            }
            return events;
        }

        // We need block timestamps, so fetch them for unique blocks
        var blockNumbers = new HashSet<long>();
        foreach (var log in result.EnumerateArray())
        {
            var blockNum = Convert.ToInt64(log.GetProperty("blockNumber").GetString(), 16);
            blockNumbers.Add(blockNum);
        }

        var blockTimestamps = await FetchBlockTimestampsAsync(blockNumbers, cancellationToken);

        // Parse each log
        foreach (var log in result.EnumerateArray())
        {
            var parsed = LogParser.ParseLog(log, blockTimestamps, targetTables);
            if (parsed != null)
            {
                events.Add(parsed);
            }
        }

        return events;
    }

    private async Task<Dictionary<long, long>> FetchBlockTimestampsAsync(
        HashSet<long> blockNumbers,
        CancellationToken cancellationToken)
    {
        var timestamps = new Dictionary<long, long>();

        if (blockNumbers.Count == 0)
            return timestamps;

        // Batch fetch block headers
        var requests = blockNumbers.Select((bn, i) => new
        {
            jsonrpc = "2.0",
            method = "eth_getBlockByNumber",
            @params = new object[] { $"0x{bn:X}", false },
            id = i + 1
        }).ToArray();

        var response = await _httpClient.PostAsJsonAsync(_options.RpcUrl, requests, cancellationToken);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken);

        if (results != null)
        {
            foreach (var result in results)
            {
                if (result.TryGetProperty("result", out var block) && block.ValueKind == JsonValueKind.Object)
                {
                    var blockNumber = Convert.ToInt64(block.GetProperty("number").GetString(), 16);
                    var timestamp = Convert.ToInt64(block.GetProperty("timestamp").GetString(), 16);
                    timestamps[blockNumber] = timestamp;
                }
            }
        }

        return timestamps;
    }

    private async Task WriteEventsAsync(List<ParsedEvent> events)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync();

        // Group by table
        var byTable = events.GroupBy(e => e.Table).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (table, tableEvents) in byTable)
        {
            await WriteTableEventsAsync(connection, table, tableEvents);
        }
    }

    private async Task WriteTableEventsAsync(NpgsqlConnection connection, string table, List<ParsedEvent> events)
    {
        if (events.Count == 0) return;

        // Build INSERT with ON CONFLICT DO NOTHING
        var columns = new List<string> { "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash" };
        var fieldNames = events[0].Fields.Keys.ToList();
        columns.AddRange(fieldNames);

        var quotedTable = $"\"{table}\"";
        var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var paramPlaceholders = string.Join(", ", columns.Select((_, i) => $"${i + 1}"));

        var sql = $@"INSERT INTO {quotedTable} ({quotedColumns}) VALUES ({paramPlaceholders})
                     ON CONFLICT (""blockNumber"", ""transactionIndex"", ""logIndex"") DO NOTHING";

        foreach (var evt in events)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue(evt.BlockNumber);
            cmd.Parameters.AddWithValue(evt.Timestamp);
            cmd.Parameters.AddWithValue(evt.TransactionIndex);
            cmd.Parameters.AddWithValue(evt.LogIndex);
            cmd.Parameters.AddWithValue(evt.TransactionHash);

            foreach (var fieldName in fieldNames)
            {
                var value = evt.Fields.GetValueOrDefault(fieldName);
                if (value is BigInteger bi)
                {
                    cmd.Parameters.AddWithValue(bi);
                }
                else if (value != null)
                {
                    cmd.Parameters.AddWithValue(value);
                }
                else
                {
                    cmd.Parameters.AddWithValue(DBNull.Value);
                }
            }

            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> GetLatestBlockFromDbAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT MAX(""blockNumber"") FROM ""System_Block""", connection);

            var result = await cmd.ExecuteScalarAsync();
            return result is long l ? l : 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<BackfillProgress?> GetBackfillProgressAsync(string[] tables)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync();

            await EnsureProgressTableExistsAsync(connection);

            var tableKey = string.Join(",", tables.OrderBy(t => t));

            await using var cmd = new NpgsqlCommand(@"
                SELECT ""currentBlock"", ""toBlock"", ""status""
                FROM ""System_BackfillProgress""
                WHERE ""tableName"" = @tableName", connection);
            cmd.Parameters.AddWithValue("tableName", tableKey);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new BackfillProgress
                {
                    CurrentBlock = reader.GetInt64(0),
                    ToBlock = reader.GetInt64(1),
                    Status = reader.GetString(2)
                };
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    private async Task SetBackfillProgressAsync(string[] tables, long currentBlock, long toBlock)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync();

        await EnsureProgressTableExistsAsync(connection);

        var tableKey = string.Join(",", tables.OrderBy(t => t));
        var status = currentBlock >= toBlock ? "completed" : "running";

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""System_BackfillProgress""
                (""tableName"", ""fromBlock"", ""toBlock"", ""currentBlock"", ""status"", ""updatedAt"")
            VALUES (@tableName, @fromBlock, @toBlock, @currentBlock, @status, @updatedAt)
            ON CONFLICT (""tableName"") DO UPDATE SET
                ""currentBlock"" = @currentBlock,
                ""status"" = @status,
                ""updatedAt"" = @updatedAt", connection);

        cmd.Parameters.AddWithValue("tableName", tableKey);
        cmd.Parameters.AddWithValue("fromBlock", _options.FromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.Parameters.AddWithValue("currentBlock", currentBlock);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureProgressTableExistsAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS ""System_BackfillProgress"" (
                ""tableName"" TEXT PRIMARY KEY,
                ""fromBlock"" BIGINT NOT NULL,
                ""toBlock"" BIGINT NOT NULL,
                ""currentBlock"" BIGINT NOT NULL,
                ""status"" TEXT NOT NULL,
                ""updatedAt"" TIMESTAMP NOT NULL
            )", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ListTablesAsync(string connectionString)
    {
        Console.WriteLine("Supported tables for backfill:");
        Console.WriteLine();

        foreach (var table in EventRegistry.Events.Keys.OrderBy(t => t))
        {
            var def = EventRegistry.Events[table];
            Console.WriteLine($"  {table}");
            Console.WriteLine($"    Topic: {def.TopicHex[..10]}...");
            if (EventRegistry.ContractFilters.TryGetValue(def.TopicHex, out var addrs))
            {
                Console.WriteLine($"    Contracts: {string.Join(", ", addrs.Select(a => a[..10] + "..."))}");
            }
            Console.WriteLine();
        }

        // Try to get row counts
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            Console.WriteLine("Current row counts:");
            Console.WriteLine();

            foreach (var table in EventRegistry.Events.Keys.OrderBy(t => t))
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        $@"SELECT COUNT(*) FROM ""{table}""", connection);
                    var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);
                    Console.WriteLine($"  {table,-50} {count:N0} rows");
                }
                catch
                {
                    Console.WriteLine($"  {table,-50} (table not found)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not connect to database: {ex.Message}");
        }
    }

    public static async Task ShowStatusAsync(string connectionString)
    {
        Console.WriteLine("Backfill status:");
        Console.WriteLine();

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Check if progress table exists
            await using var checkCmd = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables
                    WHERE table_name = 'System_BackfillProgress'
                )", connection);
            var exists = (bool)(await checkCmd.ExecuteScalarAsync() ?? false);

            if (!exists)
            {
                Console.WriteLine("No backfill progress found (table doesn't exist yet)");
                return;
            }

            await using var cmd = new NpgsqlCommand(@"
                SELECT ""tableName"", ""fromBlock"", ""toBlock"", ""currentBlock"", ""status"", ""updatedAt""
                FROM ""System_BackfillProgress""
                ORDER BY ""tableName""", connection);

            await using var reader = await cmd.ExecuteReaderAsync();
            var hasRows = false;

            while (await reader.ReadAsync())
            {
                hasRows = true;
                var tableName = reader.GetString(0);
                var fromBlock = reader.GetInt64(1);
                var toBlock = reader.GetInt64(2);
                var currentBlock = reader.GetInt64(3);
                var status = reader.GetString(4);
                var updatedAt = reader.GetDateTime(5);

                var progress = (currentBlock - fromBlock + 1) * 100.0 / (toBlock - fromBlock + 1);

                Console.WriteLine($"  {tableName}:");
                Console.WriteLine($"    Range: {fromBlock:N0} - {toBlock:N0}");
                Console.WriteLine($"    Current: {currentBlock:N0} ({progress:F1}%)");
                Console.WriteLine($"    Status: {status}");
                Console.WriteLine($"    Updated: {updatedAt:u}");
                Console.WriteLine();
            }

            if (!hasRows)
            {
                Console.WriteLine("No backfill progress recorded yet");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }

    private class BackfillProgress
    {
        public long CurrentBlock { get; init; }
        public long ToBlock { get; init; }
        public string Status { get; init; } = "";
    }
}

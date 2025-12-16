using System.Diagnostics;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Nethereum.Util;
using Npgsql;

namespace Circles.Index.Backfill;

/// <summary>
/// Standalone backfill runner that doesn't depend on Nethermind types.
/// Parses specific CrcV2 events directly from RPC responses.
/// </summary>
public class BackfillRunner
{
    private readonly BackfillOptions _options;
    private readonly HttpClient _httpClient;

    // V2 Hub address (default for Gnosis)
    private readonly string _v2HubAddress;

    // Event topic hashes (keccak256 of event signatures)
    private static readonly string FlowEdgesScopeSingleStartedTopic =
        "0x" + Sha3Keccack.Current.CalculateHash("FlowEdgesScopeSingleStarted(uint256,uint16)").ToLowerInvariant();

    private static readonly string FlowEdgesScopeLastEndedTopic =
        "0x" + Sha3Keccack.Current.CalculateHash("FlowEdgesScopeLastEnded()").ToLowerInvariant();

    private static readonly string SetAdvancedUsageFlagTopic =
        "0x" + Sha3Keccack.Current.CalculateHash("SetAdvancedUsageFlag(address,bytes32)").ToLowerInvariant();

    // Supported tables
    private static readonly HashSet<string> SupportedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CrcV2_FlowEdgesScopeSingleStarted",
        "CrcV2_FlowEdgesScopeLastEnded",
        "CrcV2_SetAdvancedUsageFlag"
    };

    public BackfillRunner(BackfillOptions options)
    {
        _options = options;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _v2HubAddress = (options.V2HubAddress ?? "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8").ToLowerInvariant();
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Circles Index Backfill Tool ===");
        Console.WriteLine();

        // Validate tables
        var unsupportedTables = _options.Tables.Where(t => !SupportedTables.Contains(t)).ToList();
        if (unsupportedTables.Count > 0)
        {
            Console.Error.WriteLine($"Error: Unsupported tables: {string.Join(", ", unsupportedTables)}");
            Console.Error.WriteLine($"Supported tables: {string.Join(", ", SupportedTables)}");
            return 1;
        }

        var targetTables = _options.Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Target tables: {string.Join(", ", _options.Tables)}");
        Console.WriteLine($"From block: {_options.FromBlock:N0}");
        Console.WriteLine($"V2 Hub: {_v2HubAddress}");
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
            var events = await FetchAndParseLogsAsync(batchStart, batchEnd, targetTables, cancellationToken);

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

    private async Task<List<ParsedEvent>> FetchAndParseLogsAsync(
        long fromBlock,
        long toBlock,
        HashSet<string> targetTables,
        CancellationToken cancellationToken)
    {
        var events = new List<ParsedEvent>();

        // Build topics filter based on target tables
        var topics = new List<string>();
        if (targetTables.Contains("CrcV2_FlowEdgesScopeSingleStarted"))
            topics.Add(FlowEdgesScopeSingleStartedTopic);
        if (targetTables.Contains("CrcV2_FlowEdgesScopeLastEnded"))
            topics.Add(FlowEdgesScopeLastEndedTopic);
        if (targetTables.Contains("CrcV2_SetAdvancedUsageFlag"))
            topics.Add(SetAdvancedUsageFlagTopic);

        if (topics.Count == 0)
            return events;

        // eth_getLogs request
        var request = new
        {
            jsonrpc = "2.0",
            method = "eth_getLogs",
            @params = new object[]
            {
                new
                {
                    address = _v2HubAddress,
                    fromBlock = $"0x{fromBlock:X}",
                    toBlock = $"0x{toBlock:X}",
                    topics = new object[] { topics.ToArray() }
                }
            },
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
            var parsed = ParseLog(log, blockTimestamps, targetTables);
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

    private ParsedEvent? ParseLog(JsonElement log, Dictionary<long, long> blockTimestamps, HashSet<string> targetTables)
    {
        var topics = log.GetProperty("topics");
        if (topics.GetArrayLength() == 0)
            return null;

        var topic0 = topics[0].GetString()!.ToLowerInvariant();
        var blockNumber = Convert.ToInt64(log.GetProperty("blockNumber").GetString(), 16);
        var transactionIndex = Convert.ToInt32(log.GetProperty("transactionIndex").GetString(), 16);
        var logIndex = Convert.ToInt32(log.GetProperty("logIndex").GetString(), 16);
        var transactionHash = log.GetProperty("transactionHash").GetString()!;
        var timestamp = blockTimestamps.GetValueOrDefault(blockNumber, 0);
        var data = log.GetProperty("data").GetString() ?? "0x";

        if (topic0 == FlowEdgesScopeSingleStartedTopic.ToLowerInvariant() &&
            targetTables.Contains("CrcV2_FlowEdgesScopeSingleStarted"))
        {
            // event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId)
            // topics[1] = flowEdgeId (indexed)
            // data = streamId (uint16, but encoded as uint256)
            var flowEdgeId = topics.GetArrayLength() > 1
                ? BigInteger.Parse("0" + topics[1].GetString()![2..], System.Globalization.NumberStyles.HexNumber)
                : BigInteger.Zero;

            var streamId = 0L;
            if (data.Length > 2)
            {
                var dataBytes = Convert.FromHexString(data[2..]);
                if (dataBytes.Length >= 32)
                {
                    // streamId is in the last 2 bytes of the 32-byte word
                    streamId = (dataBytes[30] << 8) | dataBytes[31];
                }
            }

            return new ParsedEvent
            {
                Table = "CrcV2_FlowEdgesScopeSingleStarted",
                BlockNumber = blockNumber,
                Timestamp = timestamp,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                TransactionHash = transactionHash,
                Fields = new Dictionary<string, object?>
                {
                    ["flowEdgeId"] = flowEdgeId,
                    ["streamId"] = streamId
                }
            };
        }

        if (topic0 == FlowEdgesScopeLastEndedTopic.ToLowerInvariant() &&
            targetTables.Contains("CrcV2_FlowEdgesScopeLastEnded"))
        {
            // event FlowEdgesScopeLastEnded() - no parameters
            return new ParsedEvent
            {
                Table = "CrcV2_FlowEdgesScopeLastEnded",
                BlockNumber = blockNumber,
                Timestamp = timestamp,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                TransactionHash = transactionHash,
                Fields = new Dictionary<string, object?>()
            };
        }

        if (topic0 == SetAdvancedUsageFlagTopic.ToLowerInvariant() &&
            targetTables.Contains("CrcV2_SetAdvancedUsageFlag"))
        {
            // event SetAdvancedUsageFlag(address indexed avatar, bytes32 flag)
            // topics[1] = avatar (indexed)
            // data = flag (bytes32)
            var avatar = topics.GetArrayLength() > 1
                ? "0x" + topics[1].GetString()![^40..].ToLowerInvariant()
                : "";

            byte[] flag = Array.Empty<byte>();
            if (data.Length > 2)
            {
                flag = Convert.FromHexString(data[2..]);
            }

            return new ParsedEvent
            {
                Table = "CrcV2_SetAdvancedUsageFlag",
                BlockNumber = blockNumber,
                Timestamp = timestamp,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                TransactionHash = transactionHash,
                Fields = new Dictionary<string, object?>
                {
                    ["avatar"] = avatar,
                    ["flag"] = flag
                }
            };
        }

        return null;
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

        foreach (var table in SupportedTables.OrderBy(t => t))
        {
            Console.WriteLine($"  {table}");
        }

        Console.WriteLine();

        // Try to get row counts
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            Console.WriteLine("Current row counts:");
            Console.WriteLine();

            foreach (var table in SupportedTables.OrderBy(t => t))
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        $@"SELECT COUNT(*) FROM ""{table}""", connection);
                    var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);
                    Console.WriteLine($"  {table,-45} {count:N0} rows");
                }
                catch
                {
                    Console.WriteLine($"  {table,-45} (table not found)");
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

    private class ParsedEvent
    {
        public string Table { get; init; } = "";
        public long BlockNumber { get; init; }
        public long Timestamp { get; init; }
        public int TransactionIndex { get; init; }
        public int LogIndex { get; init; }
        public string TransactionHash { get; init; } = "";
        public Dictionary<string, object?> Fields { get; init; } = new();
    }
}

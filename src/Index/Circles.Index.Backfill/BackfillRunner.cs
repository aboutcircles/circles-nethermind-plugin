using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Postgres;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Npgsql;

namespace Circles.Index.Backfill;

public class BackfillRunner
{
    private readonly BackfillOptions _options;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public BackfillRunner(BackfillOptions options)
    {
        _options = options;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Circles Index Backfill Tool ===");
        Console.WriteLine();

        // Parse target tables
        var targetTables = _options.Tables
            .Select(ParseTableName)
            .ToHashSet();

        Console.WriteLine($"Target tables: {string.Join(", ", _options.Tables)}");
        Console.WriteLine($"From block: {_options.FromBlock:N0}");
        Console.WriteLine($"RPC URL: {_options.RpcUrl}");
        Console.WriteLine($"Batch size: {_options.BatchSize}");
        Console.WriteLine($"Dry run: {_options.DryRun}");
        Console.WriteLine();

        // Initialize database and schema
        IDatabaseSchema databaseSchema = new CompositeDatabaseSchema(
            DatabaseSchemaProvider.Schemas.AllSchemas.ToArray());

        var database = new PostgresDb(_options.ConnectionString, databaseSchema);

        // Validate target tables exist
        foreach (var table in targetTables)
        {
            if (!databaseSchema.Tables.ContainsKey(table))
            {
                Console.Error.WriteLine($"Error: Table '{table.Namespace}_{table.Table}' not found in schema");
                Console.Error.WriteLine("Use 'list-tables' command to see available tables");
                return 1;
            }
        }

        // Determine end block
        var toBlock = _options.ToBlock ?? database.LatestBlock() ?? 0;
        if (toBlock == 0)
        {
            Console.Error.WriteLine("Error: Could not determine end block. Database may be empty.");
            return 1;
        }

        Console.WriteLine($"To block: {toBlock:N0}");
        Console.WriteLine($"Total blocks to process: {toBlock - _options.FromBlock + 1:N0}");
        Console.WriteLine();

        // Build table-to-logparser mapping
        var logParsers = CreateLogParsers();
        var tableToParsers = BuildTableToParserMapping(logParsers, databaseSchema);

        // Find parsers needed for target tables
        var neededParsers = new HashSet<ILogParser>();
        foreach (var table in targetTables)
        {
            if (tableToParsers.TryGetValue(table, out var parsers))
            {
                foreach (var parser in parsers)
                {
                    neededParsers.Add(parser);
                }
            }
        }

        if (neededParsers.Count == 0)
        {
            Console.Error.WriteLine("Error: No LogParsers found that produce events for the target tables");
            return 1;
        }

        Console.WriteLine($"Using {neededParsers.Count} LogParser(s): {string.Join(", ", neededParsers.Select(p => p.GetType().Name))}");
        Console.WriteLine();

        // Initialize caches for needed parsers
        Console.WriteLine("Initializing LogParser caches from database...");
        var settings = new Settings();
        var logger = new ConsoleLogger();

        foreach (var parser in neededParsers)
        {
            await parser.InitCaches(logger, database, settings);
            Console.WriteLine($"  - {parser.GetType().Name} caches initialized");
        }
        Console.WriteLine();

        // Check for existing progress
        var progress = await GetBackfillProgressAsync(_options.ConnectionString, _options.Tables);
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

        // Create sink that filters to target tables only
        var sink = new FilteredSink(
            database,
            databaseSchema.SchemaPropertyMap,
            databaseSchema.EventDtoTableMap,
            targetTables,
            _options.DryRun);

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

            // Fetch and process blocks in this batch
            var batchEvents = 0;
            for (var blockNum = batchStart; blockNum <= batchEnd; blockNum++)
            {
                var (block, receipts) = await FetchBlockWithReceiptsAsync(blockNum, cancellationToken);

                if (block == null)
                {
                    Console.Error.WriteLine($"Warning: Block {blockNum:N0} not found, skipping");
                    continue;
                }

                // Parse events using only the needed parsers
                var events = ParseBlock(block, receipts, neededParsers.ToArray(), databaseSchema);

                foreach (var evt in events)
                {
                    await sink.AddEvent(evt);
                    batchEvents++;
                }

                totalBlocks++;
            }

            // Flush the batch
            await sink.Flush();

            // Update progress
            if (!_options.DryRun)
            {
                await SetBackfillProgressAsync(_options.ConnectionString, _options.Tables, batchEnd, toBlock);
            }

            totalEvents += batchEvents;
            batchSw.Stop();

            var blocksPerSec = (batchEnd - batchStart + 1) / Math.Max(0.001, batchSw.Elapsed.TotalSeconds);
            var progress2 = (batchEnd - _options.FromBlock + 1) * 100.0 / (toBlock - _options.FromBlock + 1);

            Console.WriteLine(
                $"Batch {batchStart:N0}-{batchEnd:N0}: {batchEvents:N0} events, " +
                $"{blocksPerSec:F1} blk/s, {progress2:F1}% complete");
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

    private (string Namespace, string Table) ParseTableName(string fullName)
    {
        var parts = fullName.Split('_', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid table name format: {fullName}. Expected format: Namespace_Table");
        }
        return (parts[0], parts[1]);
    }

    private ILogParser[] CreateLogParsers()
    {
        var settings = new Settings();

        return new ILogParser[]
        {
            new CirclesV1.LogParser(new Address(settings.CirclesV1HubAddress)),
            new CirclesV2.LogParser(
                new Address(settings.CirclesV2HubAddress),
                new Address(settings.CirclesErc20LiftAddress)),
            new CirclesV2.NameRegistry.LogParser(new Address(settings.CirclesNameRegistryAddress)),
            new CirclesV2.StandardTreasury.LogParser(new Address(settings.CirclesStandardTreasuryAddress)),
            new CirclesV2.LBP.LogParser(
                settings.CirclesLBPFactoryAddress.Select(o => new Address(o)).ToImmutableHashSet()),
            new CirclesV2.CMGroupDeployer.LogParser(
                settings.CMGroupDeployer.Select(o => new Address(o)).ToImmutableHashSet()),
            new Safe.LogParser(
                settings.SafeProxyFactoryAddresses.Select(o => new Address(o)).ToImmutableHashSet()),
            new CirclesV2.TokenOffers.LogParser(
                settings.CirclesTokenOfferFactoryAddress.Select(o => new Address(o)).ToImmutableHashSet()),
            new CirclesV1.NameRegistry.LogParser(new Address(settings.CirclesV1NameRegistry)),
            new CirclesV2.BaseGroupDeployer.LogParser(new Address(settings.BaseGroupDeployer)),
            new CirclesV2.AffiliateGroupRegistry.LogParser(new Address(settings.AffiliateGroupRegistry)),
            new CirclesV2.InvitationEscrow.LogParser(
                settings.InvitationEscrowContract.Select(a => new Address(a)).ToImmutableHashSet()),
            new CirclesV2.PaymentGateway.LogParser(
                settings.PaymentGatewayFactoryAddresses.Select(o => new Address(o)).ToImmutableHashSet()),
            new CirclesV2.OIC.LogParser(new Address(settings.OICContractAddress)),
            new CirclesV2.InvitationsAtScale.LogParser(
                new Address(settings.InvitationModuleAddress),
                new Address(settings.ReferralsModuleAddress),
                new Address(settings.InvitationFarmAddress))
        };
    }

    private Dictionary<(string Namespace, string Table), List<ILogParser>> BuildTableToParserMapping(
        ILogParser[] parsers,
        IDatabaseSchema schema)
    {
        var mapping = new Dictionary<(string Namespace, string Table), List<ILogParser>>();

        // For each parser, determine which tables it can produce events for
        // by checking which event types it might return
        foreach (var parser in parsers)
        {
            var parserType = parser.GetType();
            var parserNamespace = parserType.Namespace ?? "";

            // Find all event types in the same namespace as the parser
            // EventDtoTableMap.Map: Type -> (Namespace, Table)
            foreach (var (eventType, tableKey) in schema.EventDtoTableMap.Map)
            {
                var eventNamespace = eventType.Namespace ?? "";

                // Match parser to events based on namespace conventions
                // e.g., CirclesV2.LogParser -> CirclesV2.* events
                if (eventNamespace.StartsWith(parserNamespace.Replace(".LogParser", "").Replace("Circles.Index.", "")))
                {
                    if (!mapping.ContainsKey(tableKey))
                    {
                        mapping[tableKey] = new List<ILogParser>();
                    }
                    if (!mapping[tableKey].Contains(parser))
                    {
                        mapping[tableKey].Add(parser);
                    }
                }
            }
        }

        // Special case: CirclesV2.LogParser produces CrcV2_* events
        var v2Parser = parsers.FirstOrDefault(p => p.GetType() == typeof(CirclesV2.LogParser));
        if (v2Parser != null)
        {
            foreach (var tableKey in schema.Tables.Keys.Where(k => k.Namespace == "CrcV2"))
            {
                if (!mapping.ContainsKey(tableKey))
                {
                    mapping[tableKey] = new List<ILogParser>();
                }
                if (!mapping[tableKey].Contains(v2Parser))
                {
                    mapping[tableKey].Add(v2Parser);
                }
            }
        }

        return mapping;
    }

    private async Task<(Block? Block, TxReceipt[] Receipts)> FetchBlockWithReceiptsAsync(
        long blockNumber,
        CancellationToken cancellationToken)
    {
        // Fetch block with receipts using eth_getBlockReceipts (Nethermind extension)
        // Fall back to individual receipt fetching if not available
        var blockHex = $"0x{blockNumber:X}";

        // Get block
        var blockRequest = new
        {
            jsonrpc = "2.0",
            method = "eth_getBlockByNumber",
            @params = new object[] { blockHex, true },
            id = 1
        };

        var blockResponse = await _httpClient.PostAsJsonAsync(_options.RpcUrl, blockRequest, cancellationToken);
        var blockJson = await blockResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (!blockJson.TryGetProperty("result", out var blockResult) || blockResult.ValueKind == JsonValueKind.Null)
        {
            return (null, Array.Empty<TxReceipt>());
        }

        var block = ParseBlock(blockResult);

        // Get receipts
        var receiptsRequest = new
        {
            jsonrpc = "2.0",
            method = "eth_getBlockReceipts",
            @params = new object[] { blockHex },
            id = 2
        };

        var receiptsResponse = await _httpClient.PostAsJsonAsync(_options.RpcUrl, receiptsRequest, cancellationToken);
        var receiptsJson = await receiptsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var receipts = new List<TxReceipt>();
        if (receiptsJson.TryGetProperty("result", out var receiptsResult) &&
            receiptsResult.ValueKind == JsonValueKind.Array)
        {
            foreach (var receiptJson in receiptsResult.EnumerateArray())
            {
                receipts.Add(ParseReceipt(receiptJson));
            }
        }

        return (block, receipts.ToArray());
    }

    private Block ParseBlock(JsonElement json)
    {
        var number = Convert.ToInt64(json.GetProperty("number").GetString(), 16);
        var timestamp = (ulong)Convert.ToInt64(json.GetProperty("timestamp").GetString(), 16);
        var hash = new Hash256(json.GetProperty("hash").GetString()!);
        var parentHash = json.TryGetProperty("parentHash", out var ph) && ph.ValueKind == JsonValueKind.String
            ? new Hash256(ph.GetString()!)
            : Keccak.Zero;

        var transactions = new List<Transaction>();
        if (json.TryGetProperty("transactions", out var txsJson) && txsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var txJson in txsJson.EnumerateArray())
            {
                if (txJson.ValueKind == JsonValueKind.Object)
                {
                    transactions.Add(ParseTransaction(txJson));
                }
            }
        }

        // BlockHeader requires full constructor - provide minimal required values
        var header = new BlockHeader(
            parentHash: parentHash,
            unclesHash: Keccak.OfAnEmptySequenceRlp,
            beneficiary: Address.Zero,
            difficulty: UInt256.Zero,
            number: number,
            gasLimit: 0,
            timestamp: timestamp,
            extraData: Array.Empty<byte>());

        header.Hash = hash;

        return new Block(header, transactions.ToArray(), Array.Empty<BlockHeader>());
    }

    private Transaction ParseTransaction(JsonElement json)
    {
        var hash = new Hash256(json.GetProperty("hash").GetString()!);
        var from = new Address(json.GetProperty("from").GetString()!);

        Address? to = null;
        if (json.TryGetProperty("to", out var toJson) && toJson.ValueKind == JsonValueKind.String)
        {
            var toStr = toJson.GetString();
            if (!string.IsNullOrEmpty(toStr))
            {
                to = new Address(toStr);
            }
        }

        var value = UInt256.Zero;
        if (json.TryGetProperty("value", out var valueJson))
        {
            var valueStr = valueJson.GetString();
            if (!string.IsNullOrEmpty(valueStr))
            {
                value = UInt256.Parse(valueStr);
            }
        }

        byte[] input = Array.Empty<byte>();
        if (json.TryGetProperty("input", out var inputJson))
        {
            var inputStr = inputJson.GetString();
            if (!string.IsNullOrEmpty(inputStr) && inputStr != "0x")
            {
                input = Convert.FromHexString(inputStr[2..]);
            }
        }

        return new Transaction
        {
            Hash = hash,
            SenderAddress = from,
            To = to,
            Value = value,
            Data = input
        };
    }

    private TxReceipt ParseReceipt(JsonElement json)
    {
        var txHash = new Hash256(json.GetProperty("transactionHash").GetString()!);
        var txIndex = Convert.ToInt32(json.GetProperty("transactionIndex").GetString(), 16);

        var logs = new List<LogEntry>();
        if (json.TryGetProperty("logs", out var logsJson) && logsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var logJson in logsJson.EnumerateArray())
            {
                logs.Add(ParseLogEntry(logJson));
            }
        }

        return new TxReceipt
        {
            TxHash = txHash,
            Index = txIndex,
            Logs = logs.ToArray()
        };
    }

    private LogEntry ParseLogEntry(JsonElement json)
    {
        var address = new Address(json.GetProperty("address").GetString()!);

        var topics = new List<Hash256>();
        if (json.TryGetProperty("topics", out var topicsJson) && topicsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var topicJson in topicsJson.EnumerateArray())
            {
                topics.Add(new Hash256(topicJson.GetString()!));
            }
        }

        byte[] data = Array.Empty<byte>();
        if (json.TryGetProperty("data", out var dataJson))
        {
            var dataStr = dataJson.GetString();
            if (!string.IsNullOrEmpty(dataStr) && dataStr != "0x")
            {
                data = Convert.FromHexString(dataStr[2..]);
            }
        }

        return new LogEntry(address, data, topics.ToArray());
    }

    private IEnumerable<IIndexEvent> ParseBlock(
        Block block,
        TxReceipt[] receipts,
        ILogParser[] parsers,
        IDatabaseSchema schema)
    {
        var transactionsByHash = new Dictionary<Hash256, Transaction>();
        var transactionIndexByHash = new Dictionary<Hash256, int>();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            var tx = block.Transactions[i];
            if (tx.Hash == null) continue;
            transactionsByHash[tx.Hash] = tx;
            transactionIndexByHash[tx.Hash] = i;
        }

        foreach (var receipt in receipts)
        {
            var txHash = receipt.TxHash;
            if (txHash == null || !transactionsByHash.TryGetValue(txHash, out var transaction))
                continue;

            var transactionIndex = transactionIndexByHash[txHash];
            var parserToEvents = new Dictionary<ILogParser, List<IIndexEvent>>();

            foreach (var parser in parsers)
            {
                parserToEvents[parser] = new List<IIndexEvent>();
            }

            // Parse logs
            if (receipt.Logs != null)
            {
                for (int logIndex = 0; logIndex < receipt.Logs.Length; logIndex++)
                {
                    var logEntry = receipt.Logs[logIndex];
                    foreach (var parser in parsers)
                    {
                        try
                        {
                            var events = parser.ParseLog(block, transaction, receipt, logEntry, logIndex);
                            parserToEvents[parser].AddRange(events);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"Error parsing log at block {block.Number}, tx {txHash}, log {logIndex}: {ex.Message}");
                        }
                    }
                }
            }

            // Parse transaction (aggregation)
            foreach (var parser in parsers)
            {
                try
                {
                    var txEvents = parser.ParseTransaction(
                        block, transactionIndex, transaction, receipt, parserToEvents[parser]);
                    parserToEvents[parser].AddRange(txEvents);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Error in ParseTransaction at block {block.Number}, tx {txHash}: {ex.Message}");
                }
            }

            // Yield all events
            foreach (var events in parserToEvents.Values)
            {
                foreach (var evt in events)
                {
                    yield return evt;
                }
            }
        }
    }

    public static Task ListTablesAsync(string connectionString)
    {
        IDatabaseSchema schema = new CompositeDatabaseSchema(
            DatabaseSchemaProvider.Schemas.AllSchemas.ToArray());

        Console.WriteLine("Available tables:");
        Console.WriteLine();

        foreach (var (key, _) in schema.Tables.OrderBy(t => t.Key.Namespace).ThenBy(t => t.Key.Table))
        {
            if (key.Namespace.StartsWith("V_") || key.Namespace == "System")
                continue;

            Console.WriteLine($"  {key.Namespace}_{key.Table}");
        }

        Console.WriteLine();
        Console.WriteLine("Use --tables to specify which tables to backfill");
        return Task.CompletedTask;
    }

    public static async Task ShowStatusAsync(string connectionString)
    {
        Console.WriteLine("Backfill status:");
        Console.WriteLine();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Check if progress table exists
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = @"
            SELECT EXISTS (
                SELECT FROM information_schema.tables
                WHERE table_name = 'System_BackfillProgress'
            )";
        var exists = (bool)(await checkCmd.ExecuteScalarAsync() ?? false);

        if (!exists)
        {
            Console.WriteLine("No backfill progress found (table doesn't exist yet)");
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ""tableName"", ""fromBlock"", ""toBlock"", ""currentBlock"", ""status"", ""updatedAt""
            FROM ""System_BackfillProgress""
            ORDER BY ""tableName""";

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

    private static async Task<BackfillProgress?> GetBackfillProgressAsync(string connectionString, string[] tables)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Ensure table exists
        await EnsureProgressTableExistsAsync(connection);

        var tableKey = string.Join(",", tables.OrderBy(t => t));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ""currentBlock"", ""toBlock"", ""status""
            FROM ""System_BackfillProgress""
            WHERE ""tableName"" = @tableName";
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

        return null;
    }

    private static async Task SetBackfillProgressAsync(
        string connectionString,
        string[] tables,
        long currentBlock,
        long toBlock)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await EnsureProgressTableExistsAsync(connection);

        var tableKey = string.Join(",", tables.OrderBy(t => t));
        var status = currentBlock >= toBlock ? "completed" : "running";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ""System_BackfillProgress""
                (""tableName"", ""fromBlock"", ""toBlock"", ""currentBlock"", ""status"", ""updatedAt"")
            VALUES (@tableName, @fromBlock, @toBlock, @currentBlock, @status, @updatedAt)
            ON CONFLICT (""tableName"") DO UPDATE SET
                ""currentBlock"" = @currentBlock,
                ""status"" = @status,
                ""updatedAt"" = @updatedAt";

        cmd.Parameters.AddWithValue("tableName", tableKey);
        cmd.Parameters.AddWithValue("fromBlock", 37534026L); // Default V2 start
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.Parameters.AddWithValue("currentBlock", currentBlock);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureProgressTableExistsAsync(NpgsqlConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ""System_BackfillProgress"" (
                ""tableName"" TEXT PRIMARY KEY,
                ""fromBlock"" BIGINT NOT NULL,
                ""toBlock"" BIGINT NOT NULL,
                ""currentBlock"" BIGINT NOT NULL,
                ""status"" TEXT NOT NULL,
                ""updatedAt"" TIMESTAMP NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    private class BackfillProgress
    {
        public long CurrentBlock { get; init; }
        public long ToBlock { get; init; }
        public string Status { get; init; } = "";
    }
}

/// <summary>
/// A sink that only writes events for specific target tables
/// </summary>
internal class FilteredSink
{
    private readonly IDatabase _database;
    private readonly ISchemaPropertyMap _propertyMap;
    private readonly IEventDtoTableMap _tableMap;
    private readonly HashSet<(string Namespace, string Table)> _targetTables;
    private readonly bool _dryRun;
    private readonly List<IIndexEvent> _buffer = new();
    private const int BufferSize = 10000;

    public FilteredSink(
        IDatabase database,
        ISchemaPropertyMap propertyMap,
        IEventDtoTableMap tableMap,
        HashSet<(string Namespace, string Table)> targetTables,
        bool dryRun)
    {
        _database = database;
        _propertyMap = propertyMap;
        _tableMap = tableMap;
        _targetTables = targetTables;
        _dryRun = dryRun;
    }

    public async Task AddEvent(IIndexEvent evt)
    {
        // Check if this event maps to a target table
        if (!_tableMap.Map.TryGetValue(evt.GetType(), out var tableKey))
            return;

        if (!_targetTables.Contains(tableKey))
            return;

        _buffer.Add(evt);

        if (_buffer.Count >= BufferSize)
        {
            await Flush();
        }
    }

    public async Task Flush()
    {
        if (_buffer.Count == 0)
            return;

        if (_dryRun)
        {
            _buffer.Clear();
            return;
        }

        // Group by table
        var byTable = _buffer
            .GroupBy(e => _tableMap.Map[e.GetType()])
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (tableKey, events) in byTable)
        {
            try
            {
                // Use upsert mode for safety (handles duplicates from reruns)
                await _database.WriteBatchWithUpsert(
                    tableKey.Namespace,
                    tableKey.Table,
                    events,
                    _propertyMap);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error writing to {tableKey.Namespace}_{tableKey.Table}: {ex.Message}");
                throw;
            }
        }

        _buffer.Clear();
    }
}

/// <summary>
/// Simple console logger for backfill tool
/// </summary>
internal class ConsoleLogger : InterfaceLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Debug(string message) { } // Suppress debug in CLI
    public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
    public void Error(string message, Exception? ex = null)
    {
        Console.Error.WriteLine($"[ERROR] {message}");
        if (ex != null) Console.Error.WriteLine(ex.ToString());
    }
    public void Trace(string message) { } // Suppress trace in CLI

    public bool IsInfo => true;
    public bool IsWarn => true;
    public bool IsDebug => false;
    public bool IsTrace => false;
    public bool IsError => true;
}

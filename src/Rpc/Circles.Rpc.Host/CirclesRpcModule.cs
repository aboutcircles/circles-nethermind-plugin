using System.Collections;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Rpc.Host;

/// <summary>
/// JSON-RPC module for Circles protocol queries.
///
/// This class is split into multiple partial class files for maintainability:
/// - CirclesRpcModule.cs (this file) - Transaction history, Events, Pathfinder, SDK endpoints, Query
/// - RpcModule/CirclesRpcModule.Core.cs     - Constructor, fields, connection management
/// - RpcModule/CirclesRpcModule.Balances.cs - Token balance queries (GetTotalBalance, GetTokenBalances)
/// - RpcModule/CirclesRpcModule.Tokens.cs   - Token info (GetTokenInfo, GetTokenInfoBatch, GetTokenHolders)
/// - RpcModule/CirclesRpcModule.Avatars.cs  - Avatar information (GetAvatarInfo, GetAvatarInfoBatch)
/// - RpcModule/CirclesRpcModule.Profiles.cs - Profile operations (GetProfileByCid, SearchProfiles)
/// - RpcModule/CirclesRpcModule.Trust.cs    - Trust relations (GetTrustRelations, GetCommonTrust)
/// - RpcModule/CirclesRpcModule.Groups.cs   - Group operations (FindGroups, GetGroupMembers)
/// - RpcModule/CirclesRpcModule.Helpers.cs  - Utilities (GetHealth, GetTables)
///
/// Supporting files:
/// - CursorUtils.cs - Cursor-based pagination utilities
/// </summary>
public partial class CirclesRpcModule : ICirclesRpcModule
{
    // Note: Fields, constructor, and CreateConnectionAsync are in RpcModule/CirclesRpcModule.Core.cs

    #region GetTransactionHistory - Version-specific query builders

    /// <summary>
    /// Builds SQL query for V1 TransferSummary table (excludeIntermediary=true).
    /// </summary>
    private static string BuildV1TransferSummaryQuery(bool hasCursor)
    {
        return $@"
            SELECT
                ""blockNumber"",
                timestamp,
                ""transactionIndex"",
                ""logIndex"",
                0 as ""batchIndex"",
                ""transactionHash"",
                1 as version,
                NULL::text as operator,
                ""from"",
                ""to"",
                NULL::text as id,
                amount as value
            FROM ""CrcV1_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 Transfer + HubTransfer tables (excludeIntermediary=false).
    /// </summary>
    private static string BuildV1TransfersQuery(bool hasCursor)
    {
        // V1 transfers come from both Transfer (ERC20) and HubTransfer (direct hub transfers)
        // We need to UNION them and present a unified format
        return $@"
            SELECT * FROM (
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    1 as version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    ""tokenAddress"" as id,
                    amount as value
                FROM ""CrcV1_Transfer""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    1 as version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    NULL::text as id,
                    amount as value
                FROM ""CrcV1_HubTransfer""
                WHERE (""from"" = @address OR ""to"" = @address)
            ) AS v1_transfers
            WHERE true
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 TransferSummary table (excludeIntermediary=true).
    /// </summary>
    private static string BuildV2TransferSummaryQuery(bool hasCursor)
    {
        return $@"
            SELECT
                ""blockNumber"",
                timestamp,
                ""transactionIndex"",
                ""logIndex"",
                0 as ""batchIndex"",
                ""transactionHash"",
                2 as version,
                NULL::text as operator,
                ""from"",
                ""to"",
                NULL::text as id,
                amount as value
            FROM ""CrcV2_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 transfer tables (excludeIntermediary=false).
    /// Queries TransferSingle, TransferBatch, and Erc20WrapperTransfer directly.
    /// </summary>
    private static string BuildV2TransfersQuery(bool hasCursor)
    {
        return $@"
            SELECT * FROM (
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    2 as version,
                    operator,
                    ""from"",
                    ""to"",
                    id::text,
                    value
                FROM ""CrcV2_TransferSingle""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    ""batchIndex"",
                    ""transactionHash"",
                    2 as version,
                    operator,
                    ""from"",
                    ""to"",
                    id::text,
                    value
                FROM ""CrcV2_TransferBatch""
                WHERE (""from"" = @address OR ""to"" = @address)
                UNION ALL
                SELECT
                    ""blockNumber"",
                    timestamp,
                    ""transactionIndex"",
                    ""logIndex"",
                    0 as ""batchIndex"",
                    ""transactionHash"",
                    2 as version,
                    NULL::text as operator,
                    ""from"",
                    ""to"",
                    ""tokenAddress"" as id,
                    amount as value
                FROM ""CrcV2_Erc20WrapperTransfer""
                WHERE (""from"" = @address OR ""to"" = @address)
            ) AS v2_transfers
            WHERE true
              {(hasCursor ? @"AND (
                ""blockNumber"" < @cursorBlock OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex) OR
                (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" = @cursorLogIndex AND ""batchIndex"" < @cursorBatchIndex)
              )" : "")}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC, ""batchIndex"" DESC
            LIMIT @limit
        ";
    }

    /// <summary>
    /// Executes a transaction history query and returns results.
    /// </summary>
    private async Task<List<TransactionHistoryRow>> ExecuteTransactionHistoryQuery(
        NpgsqlConnection connection,
        string sql,
        string normalizedAddress,
        int limit,
        long? cursorBlock,
        int? cursorTxIndex,
        int? cursorLogIndex,
        int? cursorBatchIndex)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("limit", limit + 1);

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
            cmd.Parameters.AddWithValue("cursorBatchIndex", cursorBatchIndex ?? 0);
        }

        var results = new List<TransactionHistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = ReadTransactionHistoryRow(reader);
            results.Add(row);
        }

        return results;
    }

    /// <summary>
    /// Reads a single TransactionHistoryRow from a data reader.
    /// </summary>
    private static TransactionHistoryRow ReadTransactionHistoryRow(NpgsqlDataReader reader)
    {
        var blockNumber = reader.GetInt64(0);
        var timestamp = reader.GetInt64(1);
        var transactionIndex = reader.GetInt32(2);
        var logIndex = reader.GetInt32(3);
        var batchIndex = reader.GetInt32(4);
        var transactionHash = reader.GetString(5);
        var ver = reader.GetInt32(6);
        var operatorAddr = reader.IsDBNull(7) ? null : reader.GetString(7);
        var from = reader.GetString(8);
        var to = reader.GetString(9);
        var id = reader.IsDBNull(10) ? null : reader.GetString(10);
        var valueRaw = reader.GetFieldValue<System.Numerics.BigInteger>(11);

        // Calculate all circle amount formats
        BigInteger attoCirclesDemurraged;
        BigInteger staticAttoCircles;
        BigInteger attoCrc;

        if (ver == 1)
        {
            // V1: value is raw attoCrc
            attoCrc = valueRaw;
            attoCirclesDemurraged = CirclesConverter.AttoCrcToAttoCircles(attoCrc, (ulong)timestamp);
            staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCirclesDemurraged);
        }
        else
        {
            // V2: value is demurraged attoCircles
            attoCirclesDemurraged = valueRaw;
            var timestampUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var day = CirclesConverter.DayFromTimestamp(timestampUtc, 1_602_720_000);
            staticAttoCircles = CirclesConverter.DemurrageToInflationary(attoCirclesDemurraged, day);
            attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCirclesDemurraged, (ulong)timestamp);
        }

        var circles = CirclesConverter.AttoCirclesToCircles(attoCirclesDemurraged);
        var staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
        var crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

        return new TransactionHistoryRow(
            BlockNumber: blockNumber,
            Timestamp: timestamp,
            TransactionIndex: transactionIndex,
            LogIndex: logIndex,
            TransactionHash: transactionHash,
            Version: ver,
            From: from,
            To: to,
            Operator: operatorAddr,
            Id: id,
            Value: valueRaw.ToString(),
            Circles: circles.ToString(),
            AttoCircles: attoCirclesDemurraged.ToString(),
            Crc: crc.ToString(),
            AttoCrc: attoCrc.ToString(),
            StaticCircles: staticCircles.ToString(),
            StaticAttoCircles: staticAttoCircles.ToString()
        );
    }

    #endregion

    public async Task<PagedResponse<TransactionHistoryRow>> GetTransactionHistory(
        string avatarAddress,
        int limit = 50,
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = true)
    {
        var normalizedAddress = avatarAddress.ToLower();
        await using var connection = await CreateConnectionAsync();

        var (cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex) = CursorUtils.DecodeCursorWithBatch(cursor);
        var hasCursor = cursorBlock.HasValue;

        List<TransactionHistoryRow> results;

        if (version.HasValue)
        {
            // Query specific version directly - no UNION needed
            string sql = (version.Value, excludeIntermediary) switch
            {
                (1, true) => BuildV1TransferSummaryQuery(hasCursor),
                (1, false) => BuildV1TransfersQuery(hasCursor),
                (2, true) => BuildV2TransferSummaryQuery(hasCursor),
                (2, false) => BuildV2TransfersQuery(hasCursor),
                _ => throw new ArgumentException($"Invalid version: {version.Value}. Must be 1 or 2.")
            };

            results = await ExecuteTransactionHistoryQuery(
                connection, sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);
        }
        else
        {
            // Query both versions separately and merge results in application code
            // This avoids SQL UNION across V1+V2 which causes performance issues
            var v1Sql = excludeIntermediary
                ? BuildV1TransferSummaryQuery(hasCursor)
                : BuildV1TransfersQuery(hasCursor);
            var v2Sql = excludeIntermediary
                ? BuildV2TransferSummaryQuery(hasCursor)
                : BuildV2TransfersQuery(hasCursor);

            // Execute both queries
            var v1Results = await ExecuteTransactionHistoryQuery(
                connection, v1Sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);

            var v2Results = await ExecuteTransactionHistoryQuery(
                connection, v2Sql, normalizedAddress, limit,
                cursorBlock, cursorTxIndex, cursorLogIndex, cursorBatchIndex);

            // Merge and sort by block/tx/log descending, take limit+1
            results = v1Results
                .Concat(v2Results)
                .OrderByDescending(r => r.BlockNumber)
                .ThenByDescending(r => r.TransactionIndex)
                .ThenByDescending(r => r.LogIndex)
                .Take(limit + 1)
                .ToList();
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var lastResult = results[^1];
            nextCursor = CursorUtils.EncodeCursorWithBatch(lastResult.BlockNumber, lastResult.TransactionIndex, lastResult.LogIndex, 0);
        }

        return new PagedResponse<TransactionHistoryRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    public async Task<PagedResponse<TransferDataRow>> GetTransferData(
        string address,
        string? direction = null,
        string? counterparty = null,
        long? fromBlock = null,
        long? toBlock = null,
        int limit = 50,
        string? cursor = null)
    {
        var addr = address.ToLower();
        limit = Math.Clamp(limit, 1, 1000);

        if (direction != null && direction != "sent" && direction != "received")
            throw new ArgumentException("direction must be 'sent', 'received', or null");

        await using var connection = await CreateConnectionAsync();
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);
        var hasCursor = cursorBlock.HasValue;

        // Build WHERE clause
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        var counterAddr = counterparty?.ToLower();

        if (direction == "sent")
        {
            conditions.Add(@"""from"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""to"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else if (direction == "received")
        {
            conditions.Add(@"""to"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""from"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else // both directions
        {
            if (counterAddr != null)
            {
                // (from=addr AND to=counter) OR (from=counter AND to=addr)
                conditions.Add(@"(""from"" = @addr AND ""to"" = @counterparty) OR (""from"" = @counterparty AND ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
            else
            {
                conditions.Add(@"(""from"" = @addr OR ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
            }
        }

        if (fromBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" >= @fromBlock");
            parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        }

        if (toBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" <= @toBlock");
            parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));
        }

        if (hasCursor)
        {
            conditions.Add(
                @"(""blockNumber"", ""transactionIndex"", ""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)");
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock!.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
        }

        var where = string.Join(" AND ", conditions.Select((c, i) =>
            // Wrap the OR clause in parens so AND binds correctly
            c.Contains(" OR ") ? $"({c})" : c));

        var sql = $@"
            SELECT ""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"",
                   ""transactionHash"", ""from"", ""to"", ""data""
            FROM ""CrcV2_TransferData""
            WHERE {where}
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit";

        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters.ToArray());

        var results = new List<TransferDataRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dataBytes = reader.GetFieldValue<byte[]>(7);
            results.Add(new TransferDataRow(
                BlockNumber: reader.GetInt64(0),
                Timestamp: reader.GetInt64(1),
                TransactionIndex: reader.GetInt32(2),
                LogIndex: reader.GetInt32(3),
                TransactionHash: reader.GetString(4),
                From: reader.GetString(5),
                To: reader.GetString(6),
                Data: "0x" + Convert.ToHexString(dataBytes).ToLower()
            ));
        }

        var hasMore = results.Count > limit;
        if (hasMore)
            results.RemoveAt(results.Count - 1);

        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var last = results[^1];
            nextCursor = CursorUtils.EncodeCursor(last.BlockNumber, last.TransactionIndex, last.LogIndex);
        }

        return new PagedResponse<TransferDataRow>(
            Results: results.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    public async Task<JsonElement> GetNetworkSnapshot()
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/snapshot";

        // Build request with conditional ETag header if we have a cached version
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        string? cachedETag;
        JsonElement? cached;
        lock (_snapshotLock)
        {
            cachedETag = _snapshotETag;
            cached = _cachedSnapshot;
        }

        if (!string.IsNullOrEmpty(cachedETag))
        {
            request.Headers.IfNoneMatch.ParseAdd(cachedETag);
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // If 304 Not Modified, return cached version
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached.HasValue)
        {
            _logger?.LogDebug("Network snapshot returned from cache (ETag: {ETag})", cachedETag);
            return cached.Value;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        // Parse to JsonDocument and clone the root element to detach from the document
        using var doc = await JsonDocument.ParseAsync(stream);
        var snapshot = doc.RootElement.Clone();

        // Cache the response with its ETag
        var newETag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrEmpty(newETag))
        {
            lock (_snapshotLock)
            {
                _snapshotETag = newETag;
                _cachedSnapshot = snapshot;
            }
            _logger?.LogDebug("Network snapshot cached (ETag: {ETag})", newETag);
        }

        return snapshot;
    }

    public async Task<JsonElement> FindPathV2(FlowRequest flowRequest)
    {
        if (string.IsNullOrEmpty(_settings.ExternalPathfinderUrl))
        {
            throw new InvalidOperationException("ExternalPathfinderUrl is not configured.");
        }

        var baseUrl = _settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath";

        var jsonContent = JsonSerializer.Serialize(flowRequest, SharedJsonOptions.CamelCase);

        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var response = await HttpClient.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Pathfinder service returned {response.StatusCode}: {errorContent}");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(responseString);
    }


    public async Task<PagedEventsResponse> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        IFilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 100;
        const int maxLimit = 1000;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        // Decode cursor if provided
        var (cursorBlockNumber, cursorTransactionIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        // Use the schema-aware map to get all event tables and their address columns
        var eventTables = DatabaseSchemaMap.TableAddressColumns;

        if (eventTables == null)
        {
            return new PagedEventsResponse(Array.Empty<object>(), false, null);
        }

        var queries = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        // Filter to only requested event types, or use all tables if no filter specified
        var relevantTables = eventTypes == null || eventTypes.Length == 0
            ? eventTables
            : eventTables.Where(kvp => eventTypes.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Add basic filter parameters
        if (address != null) parameters.Add(new NpgsqlParameter("address", address.ToLower()));
        if (fromBlock.HasValue) parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        if (toBlock.HasValue) parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));

        // Add cursor parameters if cursor is provided
        if (cursorBlockNumber.HasValue)
        {
            parameters.Add(new NpgsqlParameter("cursorBlockNumber", cursorBlockNumber.Value));
            parameters.Add(new NpgsqlParameter("cursorTransactionIndex", cursorTransactionIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
        }

        // Determine sort order once
        var sortOrder = sortAscending == true ? "ASC" : "DESC";
        var cursorComparison = sortAscending == true ? ">" : "<";

        foreach (var table in relevantTables)
        {
            // Extract namespace from table name (format: "Namespace_TableName")
            var parts = table.Key.Split('_', 2);
            if (parts.Length < 2)
            {
                continue; // Skip malformed table names
            }

            var tableNamespace = parts[0];

            // Skip System namespace and View tables (starting with V_) to match remote behavior
            // System tables are internal (Block, EventTableHead, PathfinderRequestLog, etc.)
            // View tables are virtual tables and should not be queried as events
            if (tableNamespace == "System" || tableNamespace.StartsWith('V'))
            {
                continue;
            }

            // Skip tables that don't have the required event columns
            var tableColumns = DatabaseSchemaMap.GetTableColumns(table.Key);
            if (tableColumns == null ||
                !tableColumns.ContainsKey("blockNumber") ||
                !tableColumns.ContainsKey("transactionIndex") ||
                !tableColumns.ContainsKey("logIndex") ||
                !tableColumns.ContainsKey("transactionHash"))
            {
                continue;
            }

            var whereClauses = new List<string>();

            // Basic address filter - only add if address is specified and table has address columns
            if (address != null && table.Value.Any())
            {
                whereClauses.Add($"({string.Join(" OR ", table.Value.Select(col => $"t.\"{col}\" = @address"))})");
            }

            // Block range filters
            if (fromBlock.HasValue) whereClauses.Add("t.\"blockNumber\" >= @fromBlock");
            if (toBlock.HasValue) whereClauses.Add("t.\"blockNumber\" <= @toBlock");

            // Cursor-based pagination filter
            if (cursorBlockNumber.HasValue)
            {
                whereClauses.Add($"(t.\"blockNumber\", t.\"transactionIndex\", t.\"logIndex\") {cursorComparison} (@cursorBlockNumber, @cursorTransactionIndex, @cursorLogIndex)");
            }

            // Advanced filter predicates
            if (filterPredicates != null && filterPredicates.Length > 0)
            {
                foreach (var predicate in filterPredicates)
                {
                    var predicateClause = BuildPredicateClause(predicate, parameters, table.Key);
                    if (!string.IsNullOrEmpty(predicateClause))
                    {
                        whereClauses.Add(predicateClause);
                    }
                }
            }

            var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : "";

            var query = $@"(SELECT t.""blockNumber"", t.""transactionIndex"", t.""transactionHash"", t.""logIndex"", '{table.Key}' as event_name, to_jsonb(t) as event_payload FROM ""{table.Key}"" t{whereSql} ORDER BY t.""blockNumber"" {sortOrder}, t.""transactionIndex"" {sortOrder}, t.""logIndex"" {sortOrder})";
            queries.Add(query);
        }

        if (queries.Count == 0)
        {
            return new PagedEventsResponse(Array.Empty<object>(), false, null);
        }

        // Combine results from all tables and apply final ORDER BY with LIMIT
        // Fetch one extra row to determine if there are more results
        var finalSql = string.Join(" UNION ALL ", queries);
        finalSql = $"SELECT * FROM ({finalSql}) combined ORDER BY \"blockNumber\" {sortOrder}, \"transactionIndex\" {sortOrder}, \"logIndex\" {sortOrder} LIMIT {effectiveLimit + 1}";

        // Execute the combined query
        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.CommandTimeout = 30;

        // Add all parameters
        if (address != null) command.Parameters.AddWithValue("address", address.ToLower());
        if (fromBlock.HasValue) command.Parameters.AddWithValue("fromBlock", fromBlock.Value);
        if (toBlock.HasValue) command.Parameters.AddWithValue("toBlock", toBlock.Value);

        // Add cursor parameters
        if (cursorBlockNumber.HasValue)
        {
            command.Parameters.AddWithValue("cursorBlockNumber", cursorBlockNumber.Value);
            command.Parameters.AddWithValue("cursorTransactionIndex", cursorTransactionIndex!.Value);
            command.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
        }

        // Add filter predicate parameters
        foreach (var param in parameters)
        {
            // Skip parameters we've already added
            if (param.ParameterName == "address" || param.ParameterName == "fromBlock" ||
                param.ParameterName == "toBlock" || param.ParameterName == "cursorBlockNumber" ||
                param.ParameterName == "cursorTransactionIndex" || param.ParameterName == "cursorLogIndex")
            {
                continue;
            }
            command.Parameters.Add(param);
        }

        var events = new List<object>();
        long lastBlockNumber = 0;
        int lastTransactionIndex = 0;
        int lastLogIndex = 0;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Track cursor values from each row
            lastBlockNumber = reader.GetInt64(0);
            lastTransactionIndex = reader.GetInt32(1);
            lastLogIndex = reader.GetInt32(3);
            var eventName = reader.GetString(4);

            // Parse the event payload
            var payloadJson = reader.GetString(5);
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (payloadDict != null)
            {
                // Convert numeric fields to hex format and create ordered dictionary
                var orderedValues = new Dictionary<string, object?>();

                // Add standard fields in remote server order
                var standardFieldsOrder = new[] { "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash" };

                foreach (var fieldName in standardFieldsOrder)
                {
                    if (payloadDict.TryGetValue(fieldName, out var value))
                    {
                        if (fieldName == "blockNumber" || fieldName == "timestamp" || fieldName == "transactionIndex" || fieldName == "logIndex")
                        {
                            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numValue))
                            {
                                orderedValues[fieldName] = "0x" + numValue.ToString("x");
                            }
                            else
                            {
                                orderedValues[fieldName] = value.ToString();
                            }
                        }
                        else if (value.ValueKind == JsonValueKind.String)
                        {
                            orderedValues[fieldName] = value.GetString();
                        }
                        else
                        {
                            orderedValues[fieldName] = JsonSerializer.Deserialize<object>(value.GetRawText());
                        }
                    }
                }

                // Add remaining fields in alphabetical order but with "limit" last to match remote
                var remainingFields = payloadDict
                    .Where(kvp => !orderedValues.ContainsKey(kvp.Key))
                    .OrderBy(x => x.Key == "limit" ? "zzz" : x.Key);

                foreach (var field in remainingFields)
                {
                    var key = field.Key;
                    var value = field.Value;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        orderedValues[key] = value.GetString();
                    }
                    else if (value.ValueKind == JsonValueKind.Number)
                    {
                        // Convert numeric fields to hex format to match production
                        if (value.TryGetInt64(out long numValue))
                        {
                            orderedValues[key] = "0x" + numValue.ToString("x");
                        }
                        else
                        {
                            orderedValues[key] = value.ToString();
                        }
                    }
                    else
                    {
                        orderedValues[key] = JsonSerializer.Deserialize<object>(value.GetRawText());
                    }
                }

                events.Add(new
                {
                    @event = eventName,
                    values = orderedValues
                });
            }
        }

        // Determine if there are more results
        var hasMore = events.Count > effectiveLimit;
        if (hasMore)
        {
            // Remove the extra row we fetched
            events.RemoveAt(events.Count - 1);
            // Get cursor from the last row we're actually returning
            var secondLastEvent = events.Count > 0 ? events[^1] : null;
            if (secondLastEvent != null)
            {
                var eventDict = (dynamic)secondLastEvent;
                var values = (Dictionary<string, object?>)eventDict.values;
                if (values.TryGetValue("blockNumber", out var bn) &&
                    values.TryGetValue("transactionIndex", out var ti) &&
                    values.TryGetValue("logIndex", out var li))
                {
                    // Parse hex values back to numbers for the cursor
                    lastBlockNumber = Convert.ToInt64(bn?.ToString()?.Replace("0x", ""), 16);
                    lastTransactionIndex = Convert.ToInt32(ti?.ToString()?.Replace("0x", ""), 16);
                    lastLogIndex = Convert.ToInt32(li?.ToString()?.Replace("0x", ""), 16);
                }
            }
        }

        var nextCursor = hasMore ? CursorUtils.EncodeCursor(lastBlockNumber, lastTransactionIndex, lastLogIndex) : null;

        return new PagedEventsResponse(events.ToArray(), hasMore, nextCursor);
    }

    /// <summary>
    /// Builds a WHERE clause from an IFilterPredicateDto.
    /// </summary>
    private string BuildPredicateClause(IFilterPredicateDto predicate, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        return predicate switch
        {
            FilterPredicateDto fp => BuildFilterPredicateClause(fp, parameters, tablePrefix),
            ConjunctionDto conj => BuildConjunctionClause(conj, parameters, tablePrefix),
            _ => ""
        };
    }

    private string BuildFilterPredicateClause(FilterPredicateDto predicate, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        if (predicate.Column == null)
        {
            throw new ArgumentNullException(nameof(predicate.Column), "Filter column cannot be null.");
        }
        var validatedColumn = ValidateIdentifier(predicate.Column, "Filter column");
        var column = $"t.\"{validatedColumn}\"";
        var paramName = $"@pred_{tablePrefix}_{parameters.Count}";

        // Helper to convert string values to numeric when needed for comparison operators
        object? ConvertValueForNumericComparison(object? value)
        {
            if (value is string strValue && decimal.TryParse(strValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var numericValue))
            {
                return numericValue;
            }
            return value;
        }

        switch (predicate.FilterType)
        {
            case FilterType.Equals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} = {paramName}";

            case FilterType.NotEquals:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} != {paramName}";

            case FilterType.GreaterThan:
                parameters.Add(new NpgsqlParameter(paramName, ConvertValueForNumericComparison(predicate.Value) ?? DBNull.Value));
                return $"{column} > {paramName}";

            case FilterType.GreaterThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, ConvertValueForNumericComparison(predicate.Value) ?? DBNull.Value));
                return $"{column} >= {paramName}";

            case FilterType.LessThan:
                parameters.Add(new NpgsqlParameter(paramName, ConvertValueForNumericComparison(predicate.Value) ?? DBNull.Value));
                return $"{column} < {paramName}";

            case FilterType.LessThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, ConvertValueForNumericComparison(predicate.Value) ?? DBNull.Value));
                return $"{column} <= {paramName}";

            case FilterType.Like:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} LIKE {paramName}";

            case FilterType.ILike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} ILIKE {paramName}";

            case FilterType.NotLike:
                parameters.Add(new NpgsqlParameter(paramName, predicate.Value ?? DBNull.Value));
                return $"{column} NOT LIKE {paramName}";

            case FilterType.In:
                if (predicate.Value is Array arr)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr));
                    return $"{column} = ANY({paramName})";
                }
                return "";

            case FilterType.NotIn:
                if (predicate.Value is Array arr2)
                {
                    parameters.Add(new NpgsqlParameter(paramName, arr2));
                    return $"{column} != ALL({paramName})";
                }
                return "";

            case FilterType.IsNull:
                return $"{column} IS NULL";

            case FilterType.IsNotNull:
                return $"{column} IS NOT NULL";

            default:
                return "";
        }
    }

    private string BuildConjunctionClause(ConjunctionDto conjunction, List<NpgsqlParameter> parameters, string tablePrefix)
    {
        if (conjunction.Predicates == null || conjunction.Predicates.Length == 0)
            return "";

        var clauses = new List<string>();
        foreach (var pred in conjunction.Predicates)
        {
            var clause = BuildPredicateClause(pred, parameters, tablePrefix);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
            return "";

        var joinOperator = conjunction.ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        return $"({string.Join(joinOperator, clauses)})";
    }

    /// <summary>
    /// Builds a WHERE clause for the Query method.
    /// </summary>
    private string BuildQueryPredicateClause(IFilterPredicateDto predicate, List<NpgsqlParameter> parameters)
    {
        return predicate switch
        {
            FilterPredicateDto fp => BuildQueryFilterPredicateClause(fp, parameters),
            ConjunctionDto conj => BuildQueryConjunctionClause(conj, parameters),
            _ => ""
        };
    }

    private static object? ConvertJsonElementToClr(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone()
        };
    }

    private static List<object?>? TryExtractEnumerableFilterValues(object? value)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement
                .EnumerateArray()
                .Select(ConvertJsonElementToClr)
                .ToList();
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return enumerable
                .Cast<object?>()
                .Select(v => v is JsonElement e ? ConvertJsonElementToClr(e) : v)
                .ToList();
        }

        return null;
    }

    private static object? NormalizeFilterValue(object? value, bool tryNumericParse = false)
    {
        var normalized = value is JsonElement jsonElement
            ? ConvertJsonElementToClr(jsonElement)
            : value;

        if (tryNumericParse && normalized is string stringValue &&
            decimal.TryParse(stringValue, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var numericValue))
        {
            return numericValue;
        }

        return normalized;
    }

    private static string BuildInClause(
        string column,
        string parameterPrefix,
        IReadOnlyList<object?> values,
        List<NpgsqlParameter> parameters,
        bool negate)
    {
        var placeholders = new List<string>(values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var parameterName = $"{parameterPrefix}_{i}";
            placeholders.Add(parameterName);
            parameters.Add(new NpgsqlParameter(parameterName, values[i] ?? DBNull.Value));
        }

        var @operator = negate ? "NOT IN" : "IN";
        return $"{column} {@operator} ({string.Join(", ", placeholders)})";
    }

    /// <summary>
    /// Extracts the value from a top-level "group" Equals filter, if present.
    /// Used for WHERE pushdown optimization on GroupMintRedeem/GroupWrapUnWrap views.
    /// </summary>
    private static bool TryGetGroupEqualsValue(IEnumerable<IFilterPredicateDto>? filters, out string groupValue)
    {
        groupValue = "";
        if (filters == null) return false;

        foreach (var filter in filters)
        {
            if (filter is FilterPredicateDto fp &&
                fp.Column == "group" &&
                fp.FilterType == FilterType.Equals &&
                fp.Value is string val &&
                !string.IsNullOrEmpty(val))
            {
                groupValue = val;
                return true;
            }
        }
        return false;
    }

    private string BuildQueryFilterPredicateClause(FilterPredicateDto predicate, List<NpgsqlParameter> parameters)
    {
        if (predicate.Column == null)
        {
            throw new ArgumentNullException(nameof(predicate.Column), "Filter column cannot be null.");
        }
        var validatedColumn = ValidateIdentifier(predicate.Column, "Filter column");
        var column = $"\"{validatedColumn}\"";
        var paramName = $"@p{parameters.Count}";

        switch (predicate.FilterType)
        {
            case FilterType.Equals:
                var equalsValues = TryExtractEnumerableFilterValues(predicate.Value);
                if (equalsValues is { Count: > 0 })
                {
                    return BuildInClause(column, paramName, equalsValues, parameters, negate: false);
                }

                if (equalsValues is { Count: 0 })
                {
                    return "1=0 /* empty equals-array filter */";
                }

                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value) ?? DBNull.Value));
                return $"{column} = {paramName}";

            case FilterType.NotEquals:
                var notEqualsValues = TryExtractEnumerableFilterValues(predicate.Value);
                if (notEqualsValues is { Count: > 0 })
                {
                    return BuildInClause(column, paramName, notEqualsValues, parameters, negate: true);
                }

                if (notEqualsValues is { Count: 0 })
                {
                    return "1=1 /* empty not-equals-array filter */";
                }

                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value) ?? DBNull.Value));
                return $"{column} != {paramName}";

            case FilterType.GreaterThan:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value, true) ?? DBNull.Value));
                return $"{column} > {paramName}";

            case FilterType.GreaterThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value, true) ?? DBNull.Value));
                return $"{column} >= {paramName}";

            case FilterType.LessThan:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value, true) ?? DBNull.Value));
                return $"{column} < {paramName}";

            case FilterType.LessThanOrEquals:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value, true) ?? DBNull.Value));
                return $"{column} <= {paramName}";

            case FilterType.Like:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value) ?? DBNull.Value));
                return $"{column} LIKE {paramName}";

            case FilterType.ILike:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value) ?? DBNull.Value));
                return $"{column} ILIKE {paramName}";

            case FilterType.NotLike:
                parameters.Add(new NpgsqlParameter(paramName, NormalizeFilterValue(predicate.Value) ?? DBNull.Value));
                return $"{column} NOT LIKE {paramName}";

            case FilterType.In:
                var inValues = TryExtractEnumerableFilterValues(predicate.Value);
                if (inValues is null)
                {
                    throw new ArgumentException("Value must be an IEnumerable for In filter.");
                }

                if (inValues.Count == 0)
                {
                    return "1=0 /* empty 'in' filter */";
                }

                return BuildInClause(column, paramName, inValues, parameters, negate: false);

            case FilterType.NotIn:
                var notInValues = TryExtractEnumerableFilterValues(predicate.Value);
                if (notInValues is null)
                {
                    throw new ArgumentException("Value must be an IEnumerable for NotIn filter.");
                }

                if (notInValues.Count == 0)
                {
                    return "1=0 /* empty 'not in' filter */";
                }

                return BuildInClause(column, paramName, notInValues, parameters, negate: true);

            case FilterType.IsNull:
                return $"{column} IS NULL";

            case FilterType.IsNotNull:
                return $"{column} IS NOT NULL";

            default:
                return "";
        }
    }

    private string BuildQueryConjunctionClause(ConjunctionDto conjunction, List<NpgsqlParameter> parameters)
    {
        if (conjunction.Predicates == null || conjunction.Predicates.Length == 0)
            return "";

        var clauses = new List<string>();
        foreach (var pred in conjunction.Predicates)
        {
            var clause = BuildQueryPredicateClause(pred, parameters);
            if (!string.IsNullOrEmpty(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
            return "";

        var joinOperator = conjunction.ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        return $"({string.Join(joinOperator, clauses)})";
    }

    private static object? NormalizeQueryCellValue(object value)
    {
        if (value is DBNull)
        {
            return null;
        }

        if (value is byte[] bytes)
        {
            return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
        }

        if (value is ReadOnlyMemory<byte> memory)
        {
            return "0x" + Convert.ToHexString(memory.Span).ToLowerInvariant();
        }

        return value;
    }

    private static Func<NpgsqlDataReader, int, object?>[] BuildQueryColumnReaders(NpgsqlDataReader reader)
    {
        var resultSchema = reader.GetColumnSchema();
        var columnReaders = new Func<NpgsqlDataReader, int, object?>[resultSchema.Count];

        for (int i = 0; i < resultSchema.Count; i++)
        {
            var col = resultSchema[i];

            if (col.NpgsqlDbType == NpgsqlDbType.Numeric)
            {
                int precision = col.NumericPrecision ?? 0;
                int scale = col.NumericScale ?? 0;

                bool hasNoScale = scale == 0;
                bool fitsInDecimal = precision <= 28;
                bool fitsIn256BitInteger = precision <= 78;

                if (hasNoScale)
                {
                    columnReaders[i] = fitsIn256BitInteger
                        ? static (r, idx) => r.IsDBNull(idx) ? null : (object)r.GetFieldValue<BigInteger>(idx).ToString()
                        : static (r, idx) => r.IsDBNull(idx) ? null : r.GetValue(idx)?.ToString();
                }
                else
                {
                    columnReaders[i] = fitsInDecimal
                        ? static (r, idx) => r.IsDBNull(idx) ? null : r.GetFieldValue<decimal>(idx)
                        : static (r, idx) => r.IsDBNull(idx) ? null : (object?)r.GetFieldValue<double>(idx);
                }
            }
            else
            {
                columnReaders[i] = static (r, idx) =>
                    r.IsDBNull(idx) ? null : NormalizeQueryCellValue(r.GetValue(idx));
            }
        }

        return columnReaders;
    }

    #region SDK Enablement Endpoints

    /// <summary>
    /// Gets a consolidated profile view combining avatar info, profile data, trust stats, and balances.
    /// Replaces 6-7 separate RPC calls typically needed to display a user profile.
    /// </summary>
    public async Task<ProfileViewResponse> GetProfileView(string address)
    {
        // Get avatar info
        var avatarInfo = await GetAvatarInfoBatchInternal(new[] { address });
        var avatar = avatarInfo.FirstOrDefault();

        // Get profile data (if exists)
        JsonElement? profile = null;
        try
        {
            profile = await GetProfileByAddress(address);
        }
        catch
        {
            // Profile optional
        }

        // Get trust relations
        var trustRelations = await GetTrustRelations(address);

        // Get balances
        TotalBalanceResponse? v1Balance = null;
        TotalBalanceResponse? v2Balance = null;

        if (avatar?.HasV1 == true)
        {
            try
            {
                v1Balance = await GetTotalBalance(address, 1, true);
            }
            catch
            {
                // Balance query optional
            }
        }

        if (avatar?.Version == 2)
        {
            try
            {
                v2Balance = await GetTotalBalance(address, 2, true);
            }
            catch
            {
                // Balance query optional
            }
        }

        return new ProfileViewResponse
        {
            Address = address,
            AvatarInfo = avatar,
            Profile = profile,
            TrustStats = new TrustStats
            {
                TrustsCount = trustRelations.Trusts?.Length ?? 0,
                TrustedByCount = trustRelations.TrustedBy?.Length ?? 0
            },
            V1Balance = v1Balance?.Balance,
            V2Balance = v2Balance?.Balance
        };
    }

    /// <summary>
    /// Gets aggregated trust network summary including trust counts, common trusts, and network depth.
    /// Server-side aggregation reduces client-side processing.
    /// </summary>
    public async Task<TrustNetworkSummaryResponse> GetTrustNetworkSummary(string address, int? maxDepth = 2)
    {
        var trustRelations = await GetTrustRelations(address);

        // Calculate network size at different depths
        var depth1Trusts = new HashSet<string>(trustRelations.Trusts?.Select(t => t.User) ?? Array.Empty<string>());
        var depth1TrustedBy = new HashSet<string>(trustRelations.TrustedBy?.Select(t => t.User) ?? Array.Empty<string>());

        // Mutual trusts (intersection)
        var mutualTrusts = depth1Trusts.Intersect(depth1TrustedBy).ToArray();

        return new TrustNetworkSummaryResponse
        {
            Address = address,
            DirectTrustsCount = depth1Trusts.Count,
            DirectTrustedByCount = depth1TrustedBy.Count,
            MutualTrustsCount = mutualTrusts.Length,
            MutualTrusts = mutualTrusts,
            NetworkReach = depth1Trusts.Count + depth1TrustedBy.Count - mutualTrusts.Length // Union count
        };
    }

    /// <summary>
    /// Gets aggregated trust relations showing mutual, one-way trusts, and trusted-by in a single call.
    /// Categorizes relationships for easier UI rendering. Enriched with avatar info.
    /// </summary>
    public async Task<PagedAggregatedTrustRelationsResponse> GetAggregatedTrustRelationsEnriched(
        string address,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 50;
        const int maxLimit = 200;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        var trustRelations = await GetTrustRelations(address);

        var trustsSet = new HashSet<string>(trustRelations.Trusts?.Select(t => t.User) ?? Array.Empty<string>());
        var trustedBySet = new HashSet<string>(trustRelations.TrustedBy?.Select(t => t.User) ?? Array.Empty<string>());

        var mutualAddresses = trustsSet.Intersect(trustedBySet).OrderBy(a => a).ToList();
        var oneWayTrustsAddresses = trustsSet.Except(trustedBySet).OrderBy(a => a).ToList();
        var oneWayTrustedByAddresses = trustedBySet.Except(trustsSet).OrderBy(a => a).ToList();

        // Build combined sorted list with relation types for stable cursor-based pagination
        var allRelations = new List<(string Address, string RelationType)>();
        allRelations.AddRange(mutualAddresses.Select(a => (a, "mutual")));
        allRelations.AddRange(oneWayTrustsAddresses.Select(a => (a, "trusts")));
        allRelations.AddRange(oneWayTrustedByAddresses.Select(a => (a, "trustedBy")));

        // Sort by address for consistent ordering
        allRelations = allRelations.OrderBy(r => r.Address).ToList();

        // Decode cursor (we use address as cursor for simplicity since addresses are unique)
        string? cursorAddress = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                cursorAddress = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            }
            catch
            {
                // Invalid cursor, ignore
            }
        }

        // Filter by cursor
        if (cursorAddress != null)
        {
            allRelations = allRelations.Where(r => string.Compare(r.Address, cursorAddress, StringComparison.Ordinal) > 0).ToList();
        }

        // Take limit + 1 to check if there are more
        var pageRelations = allRelations.Take(effectiveLimit + 1).ToList();
        var hasMore = pageRelations.Count > effectiveLimit;
        if (hasMore)
        {
            pageRelations.RemoveAt(pageRelations.Count - 1);
        }

        // Get avatar info for addresses in this page
        var pageAddresses = pageRelations.Select(r => r.Address).ToArray();
        var avatars = pageAddresses.Length > 0 ? await GetAvatarInfoBatchInternal(pageAddresses) : Array.Empty<AvatarInfo?>();
        var avatarDict = avatars.Where(a => a != null).ToDictionary(a => a!.Avatar, a => a);

        // Build results
        var results = pageRelations.Select(r => new TrustRelationInfo
        {
            Address = r.Address,
            AvatarInfo = avatarDict.TryGetValue(r.Address, out var avatar) ? avatar : null,
            RelationType = r.RelationType
        }).ToArray();

        // Generate next cursor from last address
        string? nextCursor = null;
        if (hasMore && results.Length > 0)
        {
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(results[^1].Address));
        }

        return new PagedAggregatedTrustRelationsResponse
        {
            Address = address,
            Results = results,
            Counts = new TrustRelationCounts
            {
                Mutual = mutualAddresses.Count,
                Trusts = oneWayTrustsAddresses.Count,
                TrustedBy = oneWayTrustedByAddresses.Count,
                Total = mutualAddresses.Count + oneWayTrustsAddresses.Count + oneWayTrustedByAddresses.Count
            },
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    /// <summary>
    /// Gets list of valid inviters for an address (addresses that trust them and have sufficient balance).
    /// Useful for invitation flows and invitation escrow scenarios.
    /// </summary>
    public async Task<PagedValidInvitersResponse> GetValidInviters(
        string address,
        string? minimumBalance = null,
        int? limit = null,
        string? cursor = null)
    {
        // Apply pagination limits
        const int defaultLimit = 50;
        const int maxLimit = 200;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        var trustRelations = await GetTrustRelations(address);
        var trustedByAddresses = trustRelations.TrustedBy?.Select(t => t.User).OrderBy(a => a).ToList() ?? new List<string>();

        if (trustedByAddresses.Count == 0)
        {
            return new PagedValidInvitersResponse
            {
                Address = address,
                Results = Array.Empty<InviterInfo>(),
                HasMore = false,
                NextCursor = null
            };
        }

        // Decode cursor (using address as cursor)
        string? cursorAddress = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                cursorAddress = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            }
            catch
            {
                // Invalid cursor, ignore
            }
        }

        // Filter by cursor
        if (cursorAddress != null)
        {
            trustedByAddresses = trustedByAddresses.Where(a => string.Compare(a, cursorAddress, StringComparison.Ordinal) > 0).ToList();
        }

        // Process addresses and collect valid inviters until we have enough
        var validInviters = new List<InviterInfo>();
        var processedCount = 0;

        foreach (var inviterAddress in trustedByAddresses)
        {
            if (validInviters.Count > effectiveLimit)
            {
                break; // We have enough (including the extra one for hasMore check)
            }

            try
            {
                // Get avatar info to determine version
                var avatarInfo = await GetAvatarInfoBatchInternal(new[] { inviterAddress });
                var avatar = avatarInfo.FirstOrDefault();

                if (avatar == null)
                {
                    processedCount++;
                    continue;
                }

                // Get balance (try both v1 and v2)
                TotalBalanceResponse? balance = null;

                if (avatar.Version == 2)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 2, true);
                    }
                    catch { }
                }
                else if (avatar.HasV1 == true)
                {
                    try
                    {
                        balance = await GetTotalBalance(inviterAddress, 1, true);
                    }
                    catch { }
                }

                if (balance != null)
                {
                    // Check minimum balance if specified
                    if (string.IsNullOrEmpty(minimumBalance) ||
                        decimal.TryParse(balance.Balance, out var balanceValue) &&
                        decimal.TryParse(minimumBalance, out var minValue) &&
                        balanceValue >= minValue)
                    {
                        validInviters.Add(new InviterInfo
                        {
                            Address = inviterAddress,
                            Balance = balance.Balance,
                            AvatarInfo = avatar
                        });
                    }
                }
            }
            catch
            {
                // Skip inviters with errors
            }

            processedCount++;
        }

        // Determine if there are more results
        var hasMore = validInviters.Count > effectiveLimit;
        if (hasMore)
        {
            validInviters.RemoveAt(validInviters.Count - 1);
        }

        // Generate next cursor from last address
        string? nextCursor = null;
        if (hasMore && validInviters.Count > 0)
        {
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(validInviters[^1].Address));
        }

        return new PagedValidInvitersResponse
        {
            Address = address,
            Results = validInviters.ToArray(),
            HasMore = hasMore,
            NextCursor = nextCursor
        };
    }

    /// <summary>
    /// Gets transaction history with enriched data including demurrage calculations and profile info.
    /// Reduces need for separate profile lookups and demurrage computations on client side.
    /// </summary>
    #region GetTransactionHistoryEnriched - Version-specific query builders

    /// <summary>
    /// Builds SQL query for V1 enriched TransferSummary (excludeIntermediary=true).
    /// </summary>
    private static string BuildV1EnrichedTransferSummaryQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_TransferSummary'
                ) as event_payload
            FROM ""CrcV1_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 enriched transfers (excludeIntermediary=false).
    /// </summary>
    private static string BuildV1EnrichedTransfersQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            -- CrcV1_Transfer: ERC20 token transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'id', ""tokenAddress"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_Transfer'
                ) as event_payload
            FROM ""CrcV1_Transfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV1_HubTransfer: direct hub transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 1,
                    'type', 'CrcV1_HubTransfer'
                ) as event_payload
            FROM ""CrcV1_HubTransfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V1 enriched trust events.
    /// </summary>
    private static string BuildV1EnrichedTrustQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'trust' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'canSendTo', ""canSendTo"",
                    'user', ""user"",
                    'limit', ""limit""::text,
                    'version', 1,
                    'type', 'CrcV1_Trust'
                ) as event_payload
            FROM ""CrcV1_Trust""
            WHERE (""canSendTo"" = @address OR ""user"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched TransferSummary (excludeIntermediary=true).
    /// </summary>
    private static string BuildV2EnrichedTransferSummaryQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'value', amount::text,
                    'version', 2,
                    'type', 'CrcV2_TransferSummary'
                ) as event_payload
            FROM ""CrcV2_TransferSummary""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched transfers (excludeIntermediary=false).
    /// </summary>
    private static string BuildV2EnrichedTransfersQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            -- CrcV2_TransferSingle: most common V2 transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'operator', operator,
                    'from', ""from"",
                    'to', ""to"",
                    'id', id::text,
                    'value', value::text,
                    'version', 2,
                    'type', 'CrcV2_TransferSingle'
                ) as event_payload
            FROM ""CrcV2_TransferSingle""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV2_TransferBatch: batch transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'batchIndex', ""batchIndex"",
                    'transactionHash', ""transactionHash"",
                    'operator', operator,
                    'from', ""from"",
                    'to', ""to"",
                    'id', id::text,
                    'value', value::text,
                    'version', 2,
                    'type', 'CrcV2_TransferBatch'
                ) as event_payload
            FROM ""CrcV2_TransferBatch""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}

            UNION ALL

            -- CrcV2_Erc20WrapperTransfer: ERC20 wrapper transfers
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'transfer' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'from', ""from"",
                    'to', ""to"",
                    'id', ""tokenAddress"",
                    'value', amount::text,
                    'version', 2,
                    'type', 'CrcV2_Erc20WrapperTransfer'
                ) as event_payload
            FROM ""CrcV2_Erc20WrapperTransfer""
            WHERE (""from"" = @address OR ""to"" = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Builds SQL query for V2 enriched trust events.
    /// </summary>
    private static string BuildV2EnrichedTrustQuery(string cursorCondition, string toBlockCondition)
    {
        return $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                'trust' as event_name,
                jsonb_build_object(
                    'blockNumber', ""blockNumber"",
                    'timestamp', timestamp,
                    'transactionIndex', ""transactionIndex"",
                    'logIndex', ""logIndex"",
                    'transactionHash', ""transactionHash"",
                    'truster', truster,
                    'trustee', trustee,
                    'expiryTime', ""expiryTime""::text,
                    'version', 2,
                    'type', 'CrcV2_Trust'
                ) as event_payload
            FROM ""CrcV2_Trust""
            WHERE (truster = @address OR trustee = @address)
              AND ""blockNumber"" >= @fromBlock
              {toBlockCondition}
              {cursorCondition}
        ";
    }

    /// <summary>
    /// Executes an enriched transaction history query and returns raw events.
    /// </summary>
    private async Task<List<JsonElement>> ExecuteEnrichedTransactionQuery(
        NpgsqlConnection connection,
        string sql,
        string normalizedAddress,
        long fromBlock,
        long? toBlock,
        int limit,
        long? cursorBlock,
        int? cursorTxIndex,
        int? cursorLogIndex)
    {
        var wrappedSql = $@"
            SELECT
                ""blockNumber"",
                ""transactionIndex"",
                ""logIndex"",
                ""transactionHash"",
                event_name,
                event_payload
            FROM ({sql}) combined
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC
            LIMIT @limit
        ";

        await using var cmd = new NpgsqlCommand(wrappedSql, connection);
        cmd.Parameters.AddWithValue("address", normalizedAddress);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("limit", limit + 1);

        if (toBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("toBlock", toBlock.Value);
        }

        if (cursorBlock.HasValue)
        {
            cmd.Parameters.AddWithValue("cursorBlock", cursorBlock.Value);
            cmd.Parameters.AddWithValue("cursorTxIndex", cursorTxIndex!.Value);
            cmd.Parameters.AddWithValue("cursorLogIndex", cursorLogIndex!.Value);
        }

        var events = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var eventPayloadJson = reader.GetString(5);
            var eventPayload = JsonSerializer.Deserialize<JsonElement>(eventPayloadJson);
            events.Add(eventPayload);
        }

        return events;
    }

    #endregion

    public async Task<PagedResponse<EnrichedTransaction>> GetTransactionHistoryEnriched(
        string address,
        long fromBlock,
        long? toBlock = null,
        int? limit = null,
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = true)
    {
        var normalizedAddress = address.ToLower();
        await using var connection = await CreateConnectionAsync();

        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);
        var effectiveLimit = limit ?? 20;

        var cursorCondition = cursorBlock.HasValue ? @"AND (
                    ""blockNumber"" < @cursorBlock OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" < @cursorTxIndex) OR
                    (""blockNumber"" = @cursorBlock AND ""transactionIndex"" = @cursorTxIndex AND ""logIndex"" < @cursorLogIndex)
                  )" : "";
        var toBlockCondition = toBlock.HasValue ? "AND \"blockNumber\" <= @toBlock" : "";

        List<JsonElement> events;

        // Determine which version to query
        // Default: version=null means V2 only (backward compatibility with existing behavior)
        var effectiveVersion = version ?? 2;

        if (effectiveVersion == 1)
        {
            // V1 queries
            var transferSql = excludeIntermediary
                ? BuildV1EnrichedTransferSummaryQuery(cursorCondition, toBlockCondition)
                : BuildV1EnrichedTransfersQuery(cursorCondition, toBlockCondition);
            var trustSql = BuildV1EnrichedTrustQuery(cursorCondition, toBlockCondition);
            var combinedSql = $"{transferSql} UNION ALL {trustSql}";

            events = await ExecuteEnrichedTransactionQuery(
                connection, combinedSql, normalizedAddress, fromBlock, toBlock, effectiveLimit,
                cursorBlock, cursorTxIndex, cursorLogIndex);
        }
        else
        {
            // V2 queries (default)
            var transferSql = excludeIntermediary
                ? BuildV2EnrichedTransferSummaryQuery(cursorCondition, toBlockCondition)
                : BuildV2EnrichedTransfersQuery(cursorCondition, toBlockCondition);
            var trustSql = BuildV2EnrichedTrustQuery(cursorCondition, toBlockCondition);
            var combinedSql = $"{transferSql} UNION ALL {trustSql}";

            events = await ExecuteEnrichedTransactionQuery(
                connection, combinedSql, normalizedAddress, fromBlock, toBlock, effectiveLimit,
                cursorBlock, cursorTxIndex, cursorLogIndex);
        }

        // Check if there are more results
        var hasMore = events.Count > effectiveLimit;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1);
        }

        // Extract all involved addresses from events
        var involvedAddresses = new HashSet<string>();
        foreach (var evt in events)
        {
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(trustee.GetString()!);
            // V1 Trust uses canSendTo and user
            if (evt.TryGetProperty("canSendTo", out var canSendTo) && canSendTo.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(canSendTo.GetString()!);
            if (evt.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String)
                involvedAddresses.Add(user.GetString()!);
        }

        // Batch fetch avatar info and profiles for all involved addresses (in parallel)
        var addressArray = involvedAddresses.ToArray();
        AvatarInfo?[] avatars;
        JsonElement?[] profiles;

        if (addressArray.Length > 0)
        {
            var avatarTask = GetAvatarInfoBatchInternal(addressArray);
            var profileTask = GetProfileByAddressBatch(addressArray);
            await Task.WhenAll(avatarTask, profileTask);
            avatars = await avatarTask;
            profiles = await profileTask;
        }
        else
        {
            avatars = Array.Empty<AvatarInfo?>();
            profiles = Array.Empty<JsonElement?>();
        }

        var avatarDict = avatars.Where(a => a != null).ToDictionary(a => a!.Avatar, a => a);
        var profileDict = involvedAddresses.Zip(profiles, (addr, prof) => new { addr, prof })
            .Where(x => x.prof != null)
            .ToDictionary(x => x.addr, x => x.prof);

        // Enrich each event
        var enrichedTransactions = new List<EnrichedTransaction>();
        foreach (var evt in events)
        {
            var blockNumber = evt.TryGetProperty("blockNumber", out var bn) ? bn.GetInt64() : 0;
            var timestamp = evt.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0;
            var transactionHash = evt.TryGetProperty("transactionHash", out var th) ? th.GetString() ?? "" : "";
            var transactionIndex = evt.TryGetProperty("transactionIndex", out var ti) ? ti.GetInt32() : 0;
            var logIndex = evt.TryGetProperty("logIndex", out var li) ? li.GetInt32() : 0;

            var enriched = new EnrichedTransaction
            {
                BlockNumber = blockNumber,
                Timestamp = timestamp,
                TransactionHash = transactionHash,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                Event = evt,
                Participants = new Dictionary<string, ParticipantInfo>()
            };

            var eventAddresses = new HashSet<string>();
            if (evt.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                eventAddresses.Add(from.GetString()!);
            if (evt.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
                eventAddresses.Add(to.GetString()!);
            if (evt.TryGetProperty("truster", out var truster) && truster.ValueKind == JsonValueKind.String)
                eventAddresses.Add(truster.GetString()!);
            if (evt.TryGetProperty("trustee", out var trustee) && trustee.ValueKind == JsonValueKind.String)
                eventAddresses.Add(trustee.GetString()!);
            if (evt.TryGetProperty("canSendTo", out var canSendTo) && canSendTo.ValueKind == JsonValueKind.String)
                eventAddresses.Add(canSendTo.GetString()!);
            if (evt.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String)
                eventAddresses.Add(user.GetString()!);

            foreach (var addr in eventAddresses)
            {
                var participantInfo = new ParticipantInfo
                {
                    AvatarInfo = avatarDict.TryGetValue(addr, out var avatar) ? avatar : null,
                    Profile = profileDict.TryGetValue(addr, out var profile) ? profile : null
                };
                enriched.Participants[addr] = participantInfo;
            }

            enrichedTransactions.Add(enriched);
        }

        // Generate next cursor
        string? nextCursor = null;
        if (hasMore && enrichedTransactions.Count > 0)
        {
            var lastEvent = enrichedTransactions[^1].Event;
            if (lastEvent.TryGetProperty("blockNumber", out var blockNum) &&
                lastEvent.TryGetProperty("transactionIndex", out var txIdx) &&
                lastEvent.TryGetProperty("logIndex", out var logIdx))
            {
                nextCursor = CursorUtils.EncodeCursor(
                    blockNum.GetInt64(),
                    txIdx.GetInt32(),
                    logIdx.GetInt32());
            }
        }

        return new PagedResponse<EnrichedTransaction>(
            Results: enrichedTransactions.ToArray(),
            HasMore: hasMore,
            NextCursor: nextCursor
        );
    }

    /// <summary>
    /// Unified search across profiles by address prefix or name/description text.
    /// Combines address lookup and full-text search in a single endpoint.
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    public async Task<PagedProfileSearchResponse> SearchProfileByAddressOrName(
        string query,
        int? limit = null,
        string? cursor = null,
        string[]? types = null)
    {
        // Apply pagination limits
        const int defaultLimit = 20;
        const int maxLimit = 100;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        // Check if query looks like an address (starts with 0x and is hex)
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            query.Length >= 10 &&
            Regex.IsMatch(query, @"^0x[0-9a-fA-F]+$"))
        {
            // Address search - find avatars with matching address prefix
            // For address search, we use the avatar address as cursor

            // Decode cursor (avatar address)
            string? cursorAddress = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    cursorAddress = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                }
                catch
                {
                    // Invalid cursor, ignore
                }
            }

            // Build filters
            var filters = new List<IFilterPredicateDto>
            {
                new FilterPredicateDto
                {
                    Column = "avatar",
                    FilterType = FilterType.Like,
                    Value = $"{query.ToLowerInvariant()}%"
                }
            };

            // Add cursor filter for pagination
            if (cursorAddress != null)
            {
                filters.Add(new FilterPredicateDto
                {
                    Column = "avatar",
                    FilterType = FilterType.GreaterThan,
                    Value = cursorAddress
                });
            }

            // Add type filter if specified
            if (types != null && types.Length > 0)
            {
                filters.Add(new FilterPredicateDto
                {
                    Column = "type",
                    FilterType = FilterType.In,
                    Value = types
                });
            }

            var selectQuery = new SelectDto
            {
                Namespace = "V_Crc",
                Table = "Avatars",
                Columns = Array.Empty<string>(),
                Filter = filters,
                Order = new[]
                {
                    new OrderByDto { Column = "avatar", SortOrder = "ASC" }
                },
                Limit = effectiveLimit + 1 // Fetch one extra for hasMore check
            };

            var results = await Query(selectQuery);

            // Get full profiles for matching addresses
            var addresses = new List<string>();
            int avatarIndex = results.Columns.IndexOf("avatar");
            if (avatarIndex >= 0)
            {
                foreach (var row in results.Rows)
                {
                    var avatarValue = row[avatarIndex];
                    if (avatarValue is string avatarStr)
                    {
                        addresses.Add(avatarStr);
                    }
                }
            }

            // Check if there are more results
            var hasMore = addresses.Count > effectiveLimit;
            if (hasMore)
            {
                addresses.RemoveAt(addresses.Count - 1);
            }

            var profiles = addresses.Count > 0
                ? await GetProfileByAddressBatch(addresses.ToArray())
                : Array.Empty<JsonElement?>();

            var profileResults = profiles.Where(p => p != null).Cast<JsonElement>().ToArray();

            // Generate next cursor from last address
            string? nextCursor = null;
            if (hasMore && addresses.Count > 0)
            {
                nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(addresses[^1]));
            }

            return new PagedProfileSearchResponse
            {
                Query = query,
                SearchType = "address",
                Results = profileResults,
                HasMore = hasMore,
                NextCursor = nextCursor
            };
        }
        else
        {
            // Text search - use cursor-based pagination with rank+avatar composite cursor
            // Cursor format: "rank:avatar" base64 encoded

            double? cursorRank = null;
            string? cursorAvatar = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                    var parts = decoded.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        cursorRank = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        cursorAvatar = parts[1];
                    }
                }
                catch
                {
                    // Invalid cursor, ignore
                }
            }

            // Use the SearchProfilesWithCursor helper
            var searchResults = await SearchProfilesWithCursor(query, effectiveLimit, cursorRank, cursorAvatar, types);

            return new PagedProfileSearchResponse
            {
                Query = query,
                SearchType = "text",
                Results = searchResults.Results.Select(r => r.Profile).Where(p => p != null).Cast<JsonElement>().ToArray(),
                HasMore = searchResults.HasMore,
                NextCursor = searchResults.NextCursor
            };
        }
    }

    /// <summary>
    /// Gets the invitation origin for an address, reconstructing how they were invited to Circles.
    /// Checks multiple invitation mechanisms in order of specificity:
    /// 1. InvitationsAtScale.RegisterHuman (most specific - has originInviter + proxyInviter)
    /// 2. InvitationEscrow.InvitationRedeemed (escrow invitation)
    /// 3. CrcV2.RegisterHuman (standard V2 invitation)
    /// 4. CrcV1.Signup (V1 self-signup)
    /// </summary>
    public async Task<InvitationOriginResponse?> GetInvitationOrigin(string address)
    {
        var normalizedAddress = address.ToLowerInvariant();
        await using var connection = await CreateConnectionAsync();

        // 1. Check InvitationsAtScale.RegisterHuman (most specific - has originInviter + proxyInviter)
        var atScaleResult = await QueryInvitationsAtScale(connection, normalizedAddress);
        if (atScaleResult != null) return atScaleResult;

        // 2. Check InvitationEscrow.InvitationRedeemed (escrow invitation)
        var escrowResult = await QueryEscrowInvitation(connection, normalizedAddress);
        if (escrowResult != null) return escrowResult;

        // 3. Check CrcV2.RegisterHuman (standard V2 invitation)
        var v2Result = await QueryV2RegisterHuman(connection, normalizedAddress);
        if (v2Result != null) return v2Result;

        // 4. Check CrcV1.Signup (V1 self-signup)
        var v1Result = await QueryV1Signup(connection, normalizedAddress);
        if (v1Result != null) return v1Result;

        return null;
    }

    /// <summary>
    /// Queries the InvitationsAtScale.RegisterHuman table for registration with origin/proxy inviters.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryInvitationsAtScale(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""human"", ""originInviter"", ""proxyInviter""
            FROM ""CrcV2_InvitationsAtScale_RegisterHuman""
            WHERE ""human"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var human = reader.GetString(3);
            var originInviter = reader.IsDBNull(4) ? null : reader.GetString(4);
            var proxyInviter = reader.IsDBNull(5) ? null : reader.GetString(5);

            // Check if originInviter is the zero address (no inviter)
            var zeroAddress = "0x0000000000000000000000000000000000000000";
            var effectiveInviter = originInviter == zeroAddress ? null : originInviter;
            var effectiveProxyInviter = proxyInviter == zeroAddress ? null : proxyInviter;

            return new InvitationOriginResponse(
                Address: human,
                InvitationType: "v2_at_scale",
                Inviter: effectiveInviter,
                ProxyInviter: effectiveProxyInviter,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the InvitationEscrow.InvitationRedeemed table for escrow-based invitations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryEscrowInvitation(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""inviter"", ""invitee"", ""amount""
            FROM ""CrcV2_InvitationEscrow_InvitationRedeemed""
            WHERE ""invitee"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var inviter = reader.GetString(3);
            var invitee = reader.GetString(4);
            var amount = reader.GetDecimal(5);

            return new InvitationOriginResponse(
                Address: invitee,
                InvitationType: "v2_escrow",
                Inviter: inviter,
                ProxyInviter: null,
                EscrowAmount: amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the CrcV2.RegisterHuman table for standard V2 registrations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryV2RegisterHuman(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""avatar"", ""inviter""
            FROM ""CrcV2_RegisterHuman""
            WHERE ""avatar"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var avatar = reader.GetString(3);
            var inviter = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Check if inviter is the zero address (no inviter)
            var zeroAddress = "0x0000000000000000000000000000000000000000";
            var effectiveInviter = inviter == zeroAddress ? null : inviter;

            return new InvitationOriginResponse(
                Address: avatar,
                InvitationType: "v2_standard",
                Inviter: effectiveInviter,
                ProxyInviter: null,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 2
            );
        }

        return null;
    }

    /// <summary>
    /// Queries the CrcV1.Signup table for V1 self-signup registrations.
    /// </summary>
    private async Task<InvitationOriginResponse?> QueryV1Signup(NpgsqlConnection connection, string address)
    {
        const string sql = @"
            SELECT ""blockNumber"", ""timestamp"", ""transactionHash"", ""user"", ""token""
            FROM ""CrcV1_Signup""
            WHERE ""user"" = @address
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var transactionHash = reader.GetString(2);
            var user = reader.GetString(3);
            // token at index 4 is available but not used in response

            return new InvitationOriginResponse(
                Address: user,
                InvitationType: "v1_signup",
                Inviter: null,  // V1 signups have no inviter
                ProxyInviter: null,
                EscrowAmount: null,
                BlockNumber: blockNumber,
                Timestamp: timestamp,
                TransactionHash: transactionHash,
                Version: 1
            );
        }

        return null;
    }

    /// <summary>
    /// Gets the list of accounts invited by a specific avatar.
    /// When accepted=true: returns accounts that registered using this avatar as inviter.
    /// When accepted=false: returns accounts this avatar trusts that are NOT yet registered (pending).
    /// </summary>
    public async Task<InvitationsFromResponse> GetInvitationsFrom(string address, bool accepted = false)
    {
        var normalizedAddress = address.ToLowerInvariant();
        await using var connection = await CreateConnectionAsync();

        if (accepted)
        {
            // Query RegisterHuman where inviter = this address
            const string sql = @"
                SELECT ""avatar"", ""blockNumber"", ""timestamp""
                FROM ""CrcV2_RegisterHuman""
                WHERE ""inviter"" = @address
                ORDER BY ""blockNumber"" DESC
                LIMIT 200";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("address", normalizedAddress);

            var results = new List<InvitedAccountInfo>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new InvitedAccountInfo
                {
                    Address = reader.GetString(0),
                    BlockNumber = reader.GetInt64(1),
                    Timestamp = reader.GetInt64(2),
                    Status = "accepted"
                });
            }

            // Batch fetch avatar info
            if (results.Count > 0)
            {
                var addresses = results.Select(r => r.Address).ToArray();
                var avatarInfos = await GetAvatarInfoBatchInternal(addresses);
                for (int i = 0; i < results.Count; i++)
                {
                    results[i] = results[i] with { AvatarInfo = avatarInfos[i] };
                }
            }

            return new InvitationsFromResponse
            {
                Address = address,
                Accepted = true,
                Results = results.ToArray()
            };
        }
        else
        {
            // Find accounts this avatar trusts (V2) that are NOT registered
            const string sql = @"
                SELECT t.""trustee""
                FROM ""CrcV2_Trust"" t
                WHERE t.""truster"" = @address
                  AND t.""expiryTime"" > EXTRACT(EPOCH FROM NOW())::bigint
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterHuman"" rh
                      WHERE rh.""avatar"" = t.""trustee""
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterGroup"" rg
                      WHERE rg.""group"" = t.""trustee""
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CrcV2_RegisterOrganization"" ro
                      WHERE ro.""organization"" = t.""trustee""
                  )
                ORDER BY t.""blockNumber"" DESC
                LIMIT 200";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("address", normalizedAddress);

            var pendingAddresses = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pendingAddresses.Add(reader.GetString(0));
            }

            var results = pendingAddresses.Select(addr => new InvitedAccountInfo
            {
                Address = addr,
                Status = "pending"
            }).ToArray();

            return new InvitationsFromResponse
            {
                Address = address,
                Accepted = false,
                Results = results
            };
        }
    }

    /// <summary>
    /// Gets all available invitations for an address from all sources (trust, escrow, at-scale).
    /// Combines multiple invitation mechanisms into a single optimized response.
    /// </summary>
    public async Task<AllInvitationsResponse> GetAllInvitations(string address, string? minimumBalance = null)
    {
        var normalizedAddress = address.ToLowerInvariant();

        // Run all queries in parallel for efficiency
        // Note: Each query must use its own connection because Npgsql doesn't support concurrent operations on one connection
        var trustTask = GetTrustInvitations(normalizedAddress, minimumBalance);
        var escrowTask = GetEscrowInvitations(normalizedAddress);
        var atScaleTask = GetAtScaleInvitations(normalizedAddress);

        await Task.WhenAll(trustTask, escrowTask, atScaleTask);

        return new AllInvitationsResponse
        {
            Address = address,
            TrustInvitations = await trustTask,
            EscrowInvitations = await escrowTask,
            AtScaleInvitations = await atScaleTask
        };
    }

    /// <summary>
    /// Gets trust-based invitations (addresses that trust the invitee and have sufficient balance).
    /// </summary>
    public async Task<TrustInvitation[]> GetTrustInvitations(string address, string? minimumBalance = null)
    {
        // Reuse existing GetValidInviters logic but transform to TrustInvitation format
        var validInviters = await GetValidInviters(address, minimumBalance, 100, null);

        return validInviters.Results.Select(inviter => new TrustInvitation
        {
            Address = inviter.Address,
            Source = "trust",
            Balance = inviter.Balance,
            AvatarInfo = inviter.AvatarInfo
        }).ToArray();
    }

    /// <summary>
    /// Gets escrow-based invitations using optimized SQL with JOINs.
    /// Filters out redeemed, revoked, and refunded escrows in a single query.
    /// </summary>
    public async Task<EscrowInvitation[]> GetEscrowInvitations(string address)
    {
        const string sql = @"
            SELECT e.""inviter"", e.""amount"", e.""blockNumber"", e.""timestamp""
            FROM ""CrcV2_InvitationEscrow_InvitationEscrowed"" e
            WHERE e.""invitee"" = @address
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRedeemed"" r
                  WHERE r.""inviter"" = e.""inviter"" AND r.""invitee"" = e.""invitee""
              )
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRevoked"" v
                  WHERE v.""inviter"" = e.""inviter"" AND v.""invitee"" = e.""invitee""
              )
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationEscrow_InvitationRefunded"" f
                  WHERE f.""inviter"" = e.""inviter"" AND f.""invitee"" = e.""invitee""
              )
            ORDER BY e.""blockNumber"" DESC
            LIMIT 100";

        var escrows = new List<(string inviter, decimal amount, long blockNumber, long timestamp)>();

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            escrows.Add((
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetInt64(2),
                reader.GetInt64(3)
            ));
        }

        if (escrows.Count == 0)
        {
            return Array.Empty<EscrowInvitation>();
        }

        // Get avatar info for all inviters
        var inviterAddresses = escrows.Select(e => e.inviter).ToArray();
        var avatarInfos = await GetAvatarInfoBatchInternal(inviterAddresses);
        var avatarInfoDict = avatarInfos.ToDictionary(a => a?.Avatar?.ToLowerInvariant() ?? "", a => a);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return escrows.Select(e =>
        {
            var daysSinceEscrow = (int)((now - e.timestamp) / 86400);
            avatarInfoDict.TryGetValue(e.inviter.ToLowerInvariant(), out var avatarInfo);

            return new EscrowInvitation
            {
                Address = e.inviter,
                Source = "escrow",
                EscrowedAmount = e.amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                EscrowDays = daysSinceEscrow,
                BlockNumber = e.blockNumber,
                Timestamp = e.timestamp,
                AvatarInfo = avatarInfo
            };
        }).ToArray();
    }

    /// <summary>
    /// Gets at-scale invitations (pre-created accounts that haven't been claimed).
    /// </summary>
    public async Task<AtScaleInvitation[]> GetAtScaleInvitations(string address)
    {
        // Check if account was pre-created but not claimed
        const string sql = @"
            SELECT c.""account"", c.""blockNumber"", c.""timestamp""
            FROM ""CrcV2_InvitationsAtScale_AccountCreated"" c
            WHERE c.""account"" = @address
              AND NOT EXISTS (
                  SELECT 1 FROM ""CrcV2_InvitationsAtScale_AccountClaimed"" cl
                  WHERE cl.""account"" = c.""account""
              )
            LIMIT 1";

        await using var connection = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("address", address);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var account = reader.GetString(0);
            var blockNumber = reader.GetInt64(1);
            var timestamp = reader.GetInt64(2);

            return new[]
            {
                new AtScaleInvitation
                {
                    Address = account,
                    Source = "atScale",
                    BlockNumber = blockNumber,
                    Timestamp = timestamp,
                    OriginInviter = null // Will be set when account is used for registration
                }
            };
        }

        return Array.Empty<AtScaleInvitation>();
    }


    /// <summary>
    /// Internal helper for cursor-based profile search with ranking.
    /// </summary>
    private async Task<(ProfileSearchResultItem[] Results, bool HasMore, string? NextCursor)> SearchProfilesWithCursor(
        string text,
        int limit,
        double? cursorRank,
        string? cursorAvatar,
        string[]? types = null)
    {
        const int hardLimit = 100;
        if (limit > hardLimit)
        {
            limit = hardLimit;
        }

        string qText = text.Trim();
        string[] tokens = qText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!tokens.Any(o => o.Length > 1))
        {
            return (Array.Empty<ProfileSearchResultItem>(), false, null);
        }

        if (tokens.Length > 3)
        {
            throw new ArgumentException("Too many search terms. Maximum is 3.");
        }

        qText = string.Join(' ', tokens);

        string[]? typeFilter = types?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool hasTypeFilter = typeFilter is { Length: > 0 };
        string typeFilterClause = hasTypeFilter ? " AND a.type = ANY(@types)" : string.Empty;

        // Build cursor filter clause
        string cursorFilterClause = "";
        if (cursorRank.HasValue && cursorAvatar != null)
        {
            // For descending order, we want items with lower rank OR same rank but higher avatar
            cursorFilterClause = " AND (COALESCE(r.receive_count, 0), p.rank, p.avatar) < (@cursorReceiveCount, @cursorRank, @cursorAvatar)";
        }

        string sql = $@"
        WITH
            input(txt) AS (VALUES (@search)),
            q AS (
                SELECT to_tsquery(
                         'simple',
                         (
                           SELECT string_agg(quote_literal(tok) || ':*', ' & ')
                           FROM   unnest(string_to_array(txt, ' ')) AS tok
                         )
                       ) AS query
                FROM input
            ),
            w_profile AS (
                SELECT  a.avatar, a.""timestamp"", a.name AS avatar_name, rs.""shortName"" AS short_name,
                        a.type AS avatar_type, f.cid AS cid, f.metadata_digest, f.payload,
                        ts_rank_cd(
                          ARRAY[1.0, 0.4, 0.2, 0.05],
                          (
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'name', '')), 'A') ||
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                            setweight(to_tsvector('simple', a.avatar), 'C')
                          ),
                          q.query
                        ) AS rank
                FROM   ""V_CrcV2_Avatars"" a
                LEFT JOIN ""CrcV2_RegisterShortName"" rs ON rs.avatar = a.avatar
                JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
                CROSS JOIN q
                WHERE (
                        setweight(to_tsvector('simple', coalesce(f.payload ->> 'name', '')), 'A') ||
                        setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                        setweight(to_tsvector('simple', a.avatar), 'C')
                      ) @@ q.query
                  {typeFilterClause}
            ),
            wo_profile AS (
                SELECT  a.avatar, a.""timestamp"", a.name AS avatar_name, rs.""shortName"" AS short_name,
                        a.type AS avatar_type, NULL::text AS cid, NULL::bytea AS metadata_digest, NULL::jsonb AS payload,
                        ts_rank_cd(
                          ARRAY[1.0, 0.4, 0.2, 0.05],
                          (
                            setweight(to_tsvector('simple', a.name), 'A') ||
                            setweight(to_tsvector('simple', a.avatar), 'C')
                          ),
                          q.query
                        ) AS rank
                FROM   ""V_CrcV2_Avatars"" a
                LEFT JOIN ""CrcV2_RegisterShortName"" rs ON rs.avatar = a.avatar
                LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
                CROSS JOIN q
                WHERE f.metadata_digest IS NULL
                  AND (
                        setweight(to_tsvector('simple', a.name), 'A') ||
                        setweight(to_tsvector('simple', a.avatar), 'C')
                      ) @@ q.query
                  {typeFilterClause}
            )
        SELECT  p.avatar, p.avatar_name, p.short_name::text as short_name, p.avatar_type, p.payload, p.cid,
                COALESCE(r.receive_count, 0) as receive_count, p.rank
        FROM   (SELECT * FROM w_profile
                UNION ALL
                SELECT * FROM wo_profile) p
        LEFT JOIN ""V_CrcV2_ReceiveCount"" r USING (avatar)
        WHERE 1=1 {cursorFilterClause}
        ORDER BY COALESCE(r.receive_count, 0) DESC, p.rank DESC, p.avatar ASC
        LIMIT  @limit;";

        await using var conn = await CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.ProfileSearchTimeoutSeconds;
        cmd.Parameters.AddWithValue("search", qText);
        cmd.Parameters.AddWithValue("limit", limit + 1); // Fetch one extra for hasMore check
        if (hasTypeFilter)
        {
            cmd.Parameters.AddWithValue("types", typeFilter!);
        }
        if (cursorRank.HasValue && cursorAvatar != null)
        {
            cmd.Parameters.AddWithValue("cursorReceiveCount", 0L); // We'll use rank primarily
            cmd.Parameters.AddWithValue("cursorRank", cursorRank.Value);
            cmd.Parameters.AddWithValue("cursorAvatar", cursorAvatar);
        }

        var results = new List<(ProfileSearchResultItem Item, long ReceiveCount, double Rank)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var avatar = reader.GetString(0);
            var avatarName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var shortName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var avatarType = reader.GetString(3);
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            var cid = reader.IsDBNull(5) ? null : reader.GetString(5);
            var receiveCount = reader.GetInt64(6);
            var rank = reader.GetDouble(7);

            // Get full avatar info for this result
            var avatarInfos = await GetAvatarInfoBatchInternal(new[] { avatar });
            var avatarInfo = avatarInfos[0];

            if (avatarInfo == null)
            {
                // Skip if no avatar info available
                continue;
            }

            JsonElement? profile = null;
            if (payload != null)
            {
                profile = JsonSerializer.Deserialize<JsonElement>(payload);
                profile = StripJsonLdFields(profile);
            }

            results.Add((new ProfileSearchResultItem(
                Avatar: avatar,
                AvatarInfo: avatarInfo,
                Profile: profile
            ), receiveCount, rank));
        }

        // Check if there are more results
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }

        // Generate next cursor from last result
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var last = results[^1];
            var cursorStr = $"{last.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{last.Item.Avatar}";
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(cursorStr));
        }

        return (results.Select(r => r.Item).ToArray(), hasMore, nextCursor);
    }

    #endregion

    public async Task<PagedQueryResponse> Query(SelectDto query, string? cursor = null)
    {
        if (string.IsNullOrEmpty(query.Table) || string.IsNullOrEmpty(query.Namespace))
        {
            throw new ArgumentException("Namespace and Table must be provided.");
        }

        // Validate and safely construct table name
        var validatedNamespace = ValidateIdentifier(query.Namespace, "Namespace");
        var validatedTable = ValidateIdentifier(query.Table, "Table");
        var fullTableName = $"{validatedNamespace}_{validatedTable}";
        var tableName = $"\"{fullTableName}\"";

        // Check if the table has event columns for cursor-based pagination
        var tableColumns = DatabaseSchemaMap.GetTableColumns(fullTableName);
        var hasEventColumns = tableColumns != null &&
            tableColumns.ContainsKey("blockNumber") &&
            tableColumns.ContainsKey("transactionIndex") &&
            tableColumns.ContainsKey("logIndex");

        // Decode cursor if provided and table supports cursor-based pagination
        var (cursorBlockNumber, cursorTransactionIndex, cursorLogIndex) = hasEventColumns
            ? CursorUtils.DecodeCursor(cursor)
            : (null, null, null);

        // Validate and quote columns - always include event columns for cursor if table supports it
        var columns = "*";
        var requestedColumns = query.Columns?.ToList() ?? new List<string>();

        // Ensure event columns are included if we need them for pagination
        if (hasEventColumns && requestedColumns.Any() && !requestedColumns.Contains("*"))
        {
            var eventColumns = new[] { "blockNumber", "transactionIndex", "logIndex" };
            foreach (var eventCol in eventColumns)
            {
                if (!requestedColumns.Contains(eventCol))
                {
                    requestedColumns.Add(eventCol);
                }
            }
        }

        if (requestedColumns.Any())
        {
            var validatedColumns = requestedColumns.Select(c => ValidateIdentifier(c, "Column")).ToArray();
            var quotedColumns = validatedColumns.Select(c => $"\"{c}\"").ToArray();
            columns = string.Join(", ", quotedColumns);
        }

        var parameters = new List<NpgsqlParameter>();
        var whereClauses = new List<string>();
        if (query.Filter != null)
        {
            foreach (var filter in query.Filter)
            {
                var clause = BuildQueryPredicateClause(filter, parameters);
                if (!string.IsNullOrEmpty(clause))
                {
                    whereClauses.Add(clause);
                }
            }
        }

        // Determine sort order from query.Order for cursor comparison
        var sortAscending = true; // Default ASC
        if (query.Order != null && query.Order.Any())
        {
            var firstOrder = query.Order.First();
            sortAscending = firstOrder.SortOrder?.ToUpper() != "DESC";
        }
        var cursorComparison = sortAscending ? ">" : "<";

        // Add cursor-based pagination filter if table supports it and cursor is provided
        if (hasEventColumns && cursorBlockNumber.HasValue)
        {
            parameters.Add(new NpgsqlParameter("cursorBlockNumber", cursorBlockNumber.Value));
            parameters.Add(new NpgsqlParameter("cursorTransactionIndex", cursorTransactionIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
            whereClauses.Add($"(\"blockNumber\", \"transactionIndex\", \"logIndex\") {cursorComparison} (@cursorBlockNumber, @cursorTransactionIndex, @cursorLogIndex)");
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // Validate and quote ORDER BY columns
        var orderBySql = "";
        if (query.Order != null && query.Order.Any())
        {
            var orderByClauses = query.Order.Select(o =>
            {
                if (o.Column == null)
                {
                    throw new ArgumentNullException("Order column", "Order column cannot be null.");
                }
                var validatedColumn = ValidateIdentifier(o.Column, "Order column");
                var quotedColumn = $"\"{validatedColumn}\"";
                var sortOrder = o.SortOrder?.ToUpper() == "DESC" ? "DESC" : "ASC";
                return $"{quotedColumn} {sortOrder}";
            });
            orderBySql = "ORDER BY " + string.Join(", ", orderByClauses);
        }
        else if (hasEventColumns)
        {
            // Default ordering by event columns if table supports them and no order specified
            orderBySql = "ORDER BY \"blockNumber\" ASC, \"transactionIndex\" ASC, \"logIndex\" ASC";
        }

        // Validate LIMIT parameters
        const int defaultLimit = 100;
        const int maxLimit = 10000; // Reasonable safety limit
        var effectiveLimit = query.Limit.HasValue
            ? Math.Min(Math.Max(query.Limit.Value, 1), maxLimit)
            : defaultLimit;

        // Fetch one extra row to determine if there are more results
        var limitSql = $"LIMIT {effectiveLimit + 1}";

        // WHERE pushdown optimization: for GroupMintRedeem and GroupWrapUnWrap views,
        // if a "group" Equals filter is present, rewrite to use the table-returning function
        // which pushes the filter into the innermost joins (avoiding full table scan).
        var fromClause = tableName;
        var functionRewriteApplied = false;

        if (validatedNamespace == "V_CrcV2" && TryGetGroupEqualsValue(query.Filter, out var groupValue))
        {
            var functionName = validatedTable switch
            {
                "GroupMintRedeem_1h" => "F_CrcV2_GroupMintRedeem_1h",
                "GroupMintRedeem_1d" => "F_CrcV2_GroupMintRedeem_1d",
                "GroupWrapUnWrap_1h" => "F_CrcV2_GroupWrapUnWrap_1h",
                "GroupWrapUnWrap_1d" => "F_CrcV2_GroupWrapUnWrap_1d",
                _ => null
            };

            if (functionName != null)
            {
                var groupParam = new NpgsqlParameter("fn_group", groupValue);
                parameters.Add(groupParam);
                fromClause = $"\"{functionName}\"(@fn_group)";
                functionRewriteApplied = true;

                // Remove the "group" = X clause from WHERE since the function handles it
                whereClauses.RemoveAll(c => c.Contains("\"group\"") && c.Contains("@p"));
                whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            }
        }

        var finalSql = $"SELECT {columns} FROM {fromClause} {whereSql} {orderBySql} {limitSql}";

        await using var connection = await CreateConnectionAsync();
        await using var command = new NpgsqlCommand(finalSql, connection);
        command.CommandTimeout = _settings.DatabaseQueryTimeoutSeconds;
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<object?[]>();
        var columnNames = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();
        var columnReaders = BuildQueryColumnReaders(reader);

        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        // Find column indices for cursor generation
        var blockNumberIdx = columnNames.IndexOf("blockNumber");
        var transactionIndexIdx = columnNames.IndexOf("transactionIndex");
        var logIndexIdx = columnNames.IndexOf("logIndex");

        long lastBlockNumber = 0;
        int lastTransactionIndex = 0;
        int lastLogIndex = 0;

        while (await reader.ReadAsync())
        {
            var row = new object?[columnNames.Count];
            for (int i = 0; i < columnNames.Count; i++)
            {
                row[i] = columnReaders[i](reader, i);
            }

            // Track cursor values if available
            if (hasEventColumns && blockNumberIdx >= 0)
            {
                if (row[blockNumberIdx] is long bn) lastBlockNumber = bn;
                else if (row[blockNumberIdx] != null) long.TryParse(row[blockNumberIdx]?.ToString(), out lastBlockNumber);

                if (row[transactionIndexIdx] is int ti) lastTransactionIndex = ti;
                else if (row[transactionIndexIdx] is long tiLong) lastTransactionIndex = (int)tiLong;
                else if (row[transactionIndexIdx] != null) int.TryParse(row[transactionIndexIdx]?.ToString(), out lastTransactionIndex);

                if (row[logIndexIdx] is int li) lastLogIndex = li;
                else if (row[logIndexIdx] is long liLong) lastLogIndex = (int)liLong;
                else if (row[logIndexIdx] != null) int.TryParse(row[logIndexIdx]?.ToString(), out lastLogIndex);
            }

            results.Add(row);
        }

        // Determine if there are more results
        var hasMore = results.Count > effectiveLimit;
        string? nextCursor = null;

        if (hasMore)
        {
            // Remove the extra row we fetched
            results.RemoveAt(results.Count - 1);

            // Get cursor from the last row we're actually returning
            if (hasEventColumns && results.Count > 0 && blockNumberIdx >= 0)
            {
                var lastRow = results[^1];
                if (lastRow[blockNumberIdx] is long bn) lastBlockNumber = bn;
                else if (lastRow[blockNumberIdx] != null) long.TryParse(lastRow[blockNumberIdx]?.ToString(), out lastBlockNumber);

                if (lastRow[transactionIndexIdx] is int ti) lastTransactionIndex = ti;
                else if (lastRow[transactionIndexIdx] is long tiLong) lastTransactionIndex = (int)tiLong;
                else if (lastRow[transactionIndexIdx] != null) int.TryParse(lastRow[transactionIndexIdx]?.ToString(), out lastTransactionIndex);

                if (lastRow[logIndexIdx] is int li) lastLogIndex = li;
                else if (lastRow[logIndexIdx] is long liLong) lastLogIndex = (int)liLong;
                else if (lastRow[logIndexIdx] != null) int.TryParse(lastRow[logIndexIdx]?.ToString(), out lastLogIndex);

                nextCursor = CursorUtils.EncodeCursor(lastBlockNumber, lastTransactionIndex, lastLogIndex);
            }
        }

        return new PagedQueryResponse(Columns: columnNames, Rows: results, HasMore: hasMore, NextCursor: nextCursor);
    }
}

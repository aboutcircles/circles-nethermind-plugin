using System.Text.Json;
using Circles.Common.Dto;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Events query methods for CirclesRpcModule.
/// </summary>
public partial class CirclesRpcModule
{
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
        // Validate address if provided (nullable — null means "all addresses")
        if (address != null)
        {
            address = ValidateAndNormalizeAddress(address);
        }

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

        // Pre-build the tx-hash subquery used to address-filter flow-scope event
        // tables. Tables like CrcV2_FlowEdgesScopeLastEnded / *_SingleStarted
        // have no address column, so we can't filter them directly by avatar.
        // Without this restriction, the previous logic skipped the address
        // predicate for those tables entirely and returned every flow-scope
        // event in the block range regardless of avatar — a bug that surfaced
        // as profile pages showing transfers from unrelated transactions.
        //
        // Scope to V2 address-bearing tables only: flow-scope events are
        // emitted exclusively inside Hub.operateFlowMatrix calls, which always
        // also emit V2 transfer/mint events in the same tx. A flow-scope row
        // never appears in a V1- or wrapper-only tx, so other namespaces
        // contribute zero useful tx hashes — and including them would
        // wrongly tie unrelated multi-call txs to flow-scope events.
        //
        // Wrapped at finalSql composition into "WITH avatar_txs AS
        // MATERIALIZED (...)" so PG evaluates the UNION exactly once and
        // address-less flow-scope tables hash-join into the materialized
        // set instead of re-executing the UNION per address-less table.
        string? addressTxHashSubquery = null;
        if (address != null)
        {
            var addressTxQueries = new List<string>();
            foreach (var addrTable in eventTables)
            {
                if (!addrTable.Value.Any()) continue;

                var nameParts = addrTable.Key.Split('_', 2);
                if (nameParts.Length < 2) continue;
                var ns = nameParts[0];
                if (!FlowScopeAddressFilterNamespaces.Contains(ns)) continue;

                var cols = DatabaseSchemaMap.GetTableColumns(addrTable.Key);
                if (cols == null
                    || !cols.ContainsKey("transactionHash")
                    || !cols.ContainsKey("blockNumber"))
                {
                    continue;
                }

                var addrPredicate = $"({string.Join(" OR ", addrTable.Value.Select(c => $"\"{c}\" = @address"))})";
                var subClauses = new List<string> { addrPredicate };
                if (fromBlock.HasValue) subClauses.Add("\"blockNumber\" >= @fromBlock");
                if (toBlock.HasValue) subClauses.Add("\"blockNumber\" <= @toBlock");

                var subWhere = $" WHERE {string.Join(" AND ", subClauses)}";
                addressTxQueries.Add($"SELECT \"transactionHash\" FROM \"{addrTable.Key}\"{subWhere}");
            }

            if (addressTxQueries.Count > 0)
            {
                // UNION ALL skips the dedup pass — duplicates are absorbed naturally
                // by the outer "WHERE transactionHash IN (SELECT ... FROM avatar_txs)"
                // (set semantics). Cheaper materialization at no result-set cost.
                addressTxHashSubquery = string.Join(" UNION ALL ", addressTxQueries);
            }
        }

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

            // Address filter. For tables that have address columns (most event
            // tables) we filter directly. For tables without address columns
            // (flow-scope: CrcV2_FlowEdgesScope*) we restrict to txHashes
            // where this avatar appears in any address-bearing event in the
            // same block range. If no address-bearing table exists for this
            // address we skip the table entirely rather than over-returning.
            if (address != null)
            {
                if (table.Value.Any())
                {
                    whereClauses.Add($"({string.Join(" OR ", table.Value.Select(col => $"t.\"{col}\" = @address"))})");
                }
                else if (addressTxHashSubquery != null)
                {
                    whereClauses.Add("t.\"transactionHash\" IN (SELECT \"transactionHash\" FROM avatar_txs)");
                }
                else
                {
                    continue;
                }
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

        // Prepend the materialized CTE (when we built one) so the avatar's
        // tx-hash set is evaluated exactly once. Address-less flow-scope
        // tables reference avatar_txs via WHERE ... IN (SELECT ... FROM
        // avatar_txs) and hash-join into the materialized set instead of
        // re-executing the UNION per table.
        if (addressTxHashSubquery != null)
        {
            finalSql = $"WITH avatar_txs AS MATERIALIZED ({addressTxHashSubquery}) {finalSql}";
        }

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
                    // Parse hex values back to numbers for the cursor.
                    // Use Int64 intermediate to handle unsigned 32-bit hex (0x80000000+),
                    // then checked narrowing to Int32 (throws on truly out-of-range values).
                    try
                    {
                        lastBlockNumber = Convert.ToInt64(bn?.ToString()?.Replace("0x", ""), 16);
                        lastTransactionIndex = checked((int)Convert.ToInt64(ti?.ToString()?.Replace("0x", ""), 16));
                        lastLogIndex = checked((int)Convert.ToInt64(li?.ToString()?.Replace("0x", ""), 16));
                    }
                    catch (OverflowException)
                    {
                        _logger?.LogError(
                            "Cursor hex overflow: bn={Bn} ti={Ti} li={Li} (raw types: bn={BnType} ti={TiType} li={LiType})",
                            bn?.ToString(), ti?.ToString(), li?.ToString(),
                            bn?.GetType().Name, ti?.GetType().Name, li?.GetType().Name);
                        throw;
                    }
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
                {
                    var inValues = TryExtractEnumerableFilterValues(predicate.Value);
                    if (inValues == null)
                        throw new ArgumentException($"Value for 'In' filter on column '{predicate.Column}' must be an array.");
                    if (inValues.Count == 0)
                        return "1=0"; // empty IN matches nothing
                    if (inValues.Count > MaxInFilterElements)
                        throw new ArgumentException($"In filter exceeds maximum of {MaxInFilterElements} elements.");
                    return BuildInClause(column, paramName, inValues, parameters, negate: false);
                }

            case FilterType.NotIn:
                {
                    var notInValues = TryExtractEnumerableFilterValues(predicate.Value);
                    if (notInValues == null)
                        throw new ArgumentException($"Value for 'NotIn' filter on column '{predicate.Column}' must be an array.");
                    if (notInValues.Count == 0)
                        return "1=1"; // empty NOT IN excludes nothing
                    if (notInValues.Count > MaxInFilterElements)
                        throw new ArgumentException($"NotIn filter exceeds maximum of {MaxInFilterElements} elements.");
                    return BuildInClause(column, paramName, notInValues, parameters, negate: true);
                }

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
}

using System.Collections;
using System.Numerics;
using System.Text.Json;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Rpc.Host;

/// <summary>
/// Generic Query method and its filter/conversion helpers for CirclesRpcModule.
/// Helpers are also reused by GetEvents (CirclesRpcModule.Events.cs).
/// </summary>
public partial class CirclesRpcModule
{
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
                    return "1=1 /* empty 'not in' excludes nothing */";
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

        // Security: Only allow querying tables that exist in the known schema.
        // Without this check, users could probe system tables like pg_catalog.
        var tableColumns = DatabaseSchemaMap.GetTableColumns(fullTableName);
        if (tableColumns == null)
        {
            throw new ArgumentException($"Table '{fullTableName}' is not a known Circles table.");
        }
        var hasEventColumns = tableColumns.ContainsKey("blockNumber") &&
            tableColumns.ContainsKey("transactionIndex") &&
            tableColumns.ContainsKey("logIndex");

        // Validate filter and order columns against the known schema to prevent
        // column-name injection through the double-quoted identifier interpolation.
        void AssertColumnExists(string col, string role)
        {
            if (!tableColumns.ContainsKey(col))
                throw new ArgumentException($"{role} column '{col}' does not exist in table '{fullTableName}'.");
        }

        if (query.Filter != null)
        {
            foreach (var f in query.Filter.OfType<FilterPredicateDto>())
                if (f.Column != null) AssertColumnExists(f.Column, "Filter");
        }

        if (query.Order != null)
        {
            foreach (var o in query.Order)
                if (o.Column != null) AssertColumnExists(o.Column, "Order");
        }

        if (query.Columns != null)
        {
            foreach (var c in query.Columns)
                if (c != "*") AssertColumnExists(c, "Column");
        }

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

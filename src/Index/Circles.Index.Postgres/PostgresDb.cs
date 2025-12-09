using System.Data;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core.Crypto;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Postgres;

public class ReadonlyPostgresDb(string connectionString, IDatabaseSchema schema) : IReadonlyDatabase
{
    public IDatabaseSchema Schema { get; } = schema;

    protected string ConnectionString { get; } = connectionString;

    public DatabaseQueryResult Select(ParameterizedSql select)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = select.Sql;
        foreach (var param in select.Parameters)
        {
            command.Parameters.Add(param);
        }

        using var reader = command.ExecuteReader();

        var resultSchema = reader.GetColumnSchema();
        var columnNames = new string[resultSchema.Count];
        var columnFuncs = new Func<NpgsqlDataReader, int, object?>[resultSchema.Count];

        for (int i = 0; i < resultSchema.Count; i++)
        {
            var col = resultSchema[i];
            columnNames[i] = col.ColumnName;

            if (col.NpgsqlDbType == NpgsqlDbType.Numeric)
            {
                int precision = col.NumericPrecision ?? 0;
                int scale = col.NumericScale ?? 0;

                bool hasNoScale = scale == 0;
                bool fitsInDecimal = precision <= 28;
                bool fitsIn256BitInteger = precision <= 78; // 256-bit max ≈ 7.9e76 (78 digits)

                if (hasNoScale)
                {
                    columnFuncs[i] = fitsIn256BitInteger
                        ? static (r, idx) => r.IsDBNull(idx) ? null : r.GetFieldValue<BigInteger>(idx)
                        : static (r, idx) => r.IsDBNull(idx) ? null : r.GetValue(idx)?.ToString();
                }
                else
                {
                    columnFuncs[i] = fitsInDecimal
                        ? static (r, idx) => r.IsDBNull(idx) ? null : r.GetFieldValue<decimal>(idx)
                        : static (r, idx) => r.IsDBNull(idx) ? null : (object?)r.GetFieldValue<double>(idx);
                }
            }
            else
            {
                columnFuncs[i] = static (r, idx) => r.IsDBNull(idx) ? null : r.GetValue(idx);
            }
        }


        var resultRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[resultSchema.Count];
            for (int i = 0; i < resultSchema.Count; i++)
            {
                row[i] = columnFuncs[i](reader, i);
            }

            resultRows.Add(row);
        }

        return new DatabaseQueryResult(columnNames, resultRows);
    }

    public IDbDataParameter CreateParameter(string? name, object? value)
    {
        return new NpgsqlParameter(name, value);
    }
}

public class PostgresDb(string connectionString, IDatabaseSchema schema)
    : ReadonlyPostgresDb(connectionString, schema), IDatabase
{
    private bool HasPrimaryKey(NpgsqlConnection connection, EventSchema table)
    {
        var checkPkSql = $@"
        SELECT 1
        FROM  pg_constraint
        WHERE conrelid = '""{table.Namespace}_{table.Table}""'::regclass
        AND contype = 'p';";

        using var command = connection.CreateCommand();
        command.CommandText = checkPkSql;
        return command.ExecuteScalar() != null;
    }

    public void Migrate()
    {
        // TODO: Make sure that all tables are created first, then the views follow.

        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            StringBuilder ddlSql = new StringBuilder();
            var tables = Schema.Tables.Where(o => !o.Key.Namespace.StartsWith("V_")).ToArray();
            var views = Schema.Tables.Where(o => o.Key.Namespace.StartsWith("V_")).ToArray();

            foreach (var table in tables)
            {
                Console.WriteLine($"PostgresDb.Migrate: Creating DDL for table " + table + "...");
                var ddl = GetDdl(table.Value);
                ddlSql.AppendLine(ddl);
            }

            ExecuteNonQuery(connection, ddlSql.ToString());

            ddlSql.Clear();

            foreach (var table in views)
            {
                Console.WriteLine($"PostgresDb.Migrate: Creating DDL for view " + table + "...");
                var ddl = GetDdl(table.Value);
                ddlSql.AppendLine(ddl);
            }

            ExecuteNonQuery(connection, ddlSql.ToString());

            StringBuilder indexesSql = new StringBuilder();
            foreach (var index in Schema.Indexes)
            {
                Console.WriteLine($"PostgresDb.Migrate: Creating DDL for index " + index + "...");
                var ddl = index.Value;
                indexesSql.AppendLine(ddl);
            }

            ExecuteNonQuery(connection, indexesSql.ToString());

            StringBuilder primaryKeyDdl = new StringBuilder();
            foreach (var table in tables)
            {
                Console.WriteLine($"PostgresDb.Migrate: Creating Primary Key DDL for table " + table + "...");

                if (HasPrimaryKey(connection, table.Value))
                {
                    continue;
                }

                if (table.Value is
                    {
                        Namespace: Common.DatabaseSchema.SystemNamespace,
                        Table: Common.DatabaseSchema.Block
                    })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"blockNumber\");");
                }
                else if (table.Value is
                         {
                             Namespace: Common.DatabaseSchema.SystemNamespace,
                             Table: Common.DatabaseSchema.EventTableHead
                         })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"tableName\");");
                }
                else if (table.Value is
                         {
                             Namespace: Common.DatabaseSchema.SystemNamespace,
                             Table: Common.DatabaseSchema.PathfinderRequestLog
                         })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"blockNumber\", \"requestId\");");
                }
                else if (table.Value is
                         {
                             Namespace: Common.DatabaseSchema.SystemNamespace,
                             Table: Common.DatabaseSchema.PathfinderResponseLog
                         })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"requestId\");");
                }
                else
                {
                    var defaultKeyColumns = new[] { "\"blockNumber\"", "\"transactionIndex\"", "\"logIndex\"" };
                    var allKeyColumns = defaultKeyColumns.Union(table.Value.Columns
                            .Where(column => column.IncludeInPrimaryKey)
                            .Select(column => $"\"{column.Column}\""))
                        .ToArray();

                    var allKeyColumnsString = string.Join(", ", allKeyColumns);

                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY ({allKeyColumnsString});");
                }
            }

            if (primaryKeyDdl.Length > 0)
            {
                ExecuteNonQuery(connection, primaryKeyDdl.ToString());
            }
        }
        catch (Exception)
        {
            transaction.Rollback();
            throw;
        }

        transaction.Commit();
    }

    public async Task WriteBatch(string @namespace, string table, IEnumerable<object> data,
        ISchemaPropertyMap propertyMap)
    {
        var tableSchema = Schema.Tables[(@namespace, table)];
        var columnTypes = tableSchema.Columns.ToDictionary(o => o.Column, o => o.Type);
        var columnList = string.Join(", ", columnTypes.Select(o => $"\"{o.Key}\""));

        // Build connection string with extended timeouts for large batch operations
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            CommandTimeout = 300,  // 5 minutes command timeout
            Timeout = 300,         // 5 minutes connection timeout
            WriteBufferSize = 32768,  // Increase write buffer size to 32KB (default is 8KB)
            ReadBufferSize = 32768    // Also increase read buffer for consistency
        };

        await using var connection = new NpgsqlConnection(csb.ToString());
        connection.Open();

        await using var writer = await connection.BeginBinaryImportAsync(
            $"COPY \"{tableSchema.Namespace}_{tableSchema.Table}\" ({columnList}) FROM STDIN (FORMAT BINARY)"
        );

        foreach (var indexEvent in data)
        {
            await writer.StartRowAsync();
            foreach (var column in tableSchema.Columns)
            {
                var value = propertyMap.Map[(@namespace, table)][column.Column](indexEvent);
                await writer.WriteAsync(value, GetNpgsqlDbType(column.Type));
            }
        }

        try
        {
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Detailed error in WriteBatch for {tableSchema.Namespace}_{tableSchema.Table}: {ex.Message}");
            Console.WriteLine($"Inner exception: {ex.InnerException?.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            // Optionally log the data
            Console.WriteLine($"Data count: {data.Count()}");
            foreach (var item in data.Take(5)) // Log first 5 items
            {
                Console.WriteLine($"Item: {item}");
            }
            throw;
        }
    }

    public async Task WriteBatchWithUpsert(string @namespace, string table, IEnumerable<object> data,
        ISchemaPropertyMap propertyMap)
    {
        var tableSchema = Schema.Tables[(@namespace, table)];
        var columns = tableSchema.Columns;
        var columnList = string.Join(", ", columns.Select(c => $"\"{c.Column}\""));

        // Build primary key columns for ON CONFLICT clause
        var primaryKeyColumns = GetPrimaryKeyColumns(tableSchema);
        var primaryKeyList = string.Join(", ", primaryKeyColumns.Select(c => $"\"{c}\""));

        // Build parameter placeholders for each column
        var parameterList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        var sql = $@"
            INSERT INTO ""{tableSchema.Namespace}_{tableSchema.Table}"" ({columnList})
            VALUES ({parameterList})
            ON CONFLICT ({primaryKeyList}) DO NOTHING
        ";

        // Build connection string with extended timeouts
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            CommandTimeout = 300,
            Timeout = 300
        };

        await using var connection = new NpgsqlConnection(csb.ToString());
        await connection.OpenAsync();

        // Use a transaction for better performance with multiple inserts
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var dataList = data.ToList();
            var insertedCount = 0;
            var skippedCount = 0;

            foreach (var indexEvent in dataList)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);

                for (int i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    var value = propertyMap.Map[(@namespace, table)][column.Column](indexEvent);
                    command.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                }

                var rowsAffected = await command.ExecuteNonQueryAsync();
                if (rowsAffected > 0)
                    insertedCount++;
                else
                    skippedCount++;
            }

            await transaction.CommitAsync();

            if (skippedCount > 0)
            {
                Console.WriteLine($"[WriteBatchWithUpsert] {tableSchema.Namespace}_{tableSchema.Table}: " +
                                  $"Inserted {insertedCount}, skipped {skippedCount} duplicates");
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Error in WriteBatchWithUpsert for {tableSchema.Namespace}_{tableSchema.Table}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Gets the primary key columns for a table schema.
    /// </summary>
    private List<string> GetPrimaryKeyColumns(EventSchema tableSchema)
    {
        // System tables have special primary keys
        if (tableSchema is { Namespace: Common.DatabaseSchema.SystemNamespace, Table: Common.DatabaseSchema.Block })
        {
            return ["blockNumber"];
        }

        if (tableSchema is { Namespace: Common.DatabaseSchema.SystemNamespace, Table: Common.DatabaseSchema.EventTableHead })
        {
            return ["tableName"];
        }

        if (tableSchema is { Namespace: Common.DatabaseSchema.SystemNamespace, Table: Common.DatabaseSchema.PathfinderRequestLog })
        {
            return ["blockNumber", "requestId"];
        }

        if (tableSchema is { Namespace: Common.DatabaseSchema.SystemNamespace, Table: Common.DatabaseSchema.PathfinderResponseLog })
        {
            return ["requestId"];
        }

        // Default primary key: blockNumber, transactionIndex, logIndex + any columns marked IncludeInPrimaryKey
        var defaultKeyColumns = new List<string> { "blockNumber", "transactionIndex", "logIndex" };
        var additionalKeyColumns = tableSchema.Columns
            .Where(c => c.IncludeInPrimaryKey)
            .Select(c => c.Column);

        return defaultKeyColumns.Concat(additionalKeyColumns).ToList();
    }

    private string GetDdl(EventSchema @event)
    {
        StringBuilder ddlSql = new StringBuilder();

        if (!@event.Namespace.StartsWith("V_"))
        {
            ddlSql.AppendLine($"CREATE TABLE IF NOT EXISTS \"{@event.Namespace}_{@event.Table}\" (");

            List<string> columnDefinitions = new List<string>();

            foreach (var column in @event.Columns)
            {
                string columnType = GetSqlType(column.Type);
                string columnName = column.Column;
                string columnDefinition = $"\"{columnName}\" {columnType}";

                columnDefinitions.Add(columnDefinition);
            }

            ddlSql.AppendLine(string.Join(",\n", columnDefinitions));
            ddlSql.AppendLine(");");
            ddlSql.AppendLine();

            // Generate index creation statements
            var indexedColumns = @event.Columns
                .Where(column => column.IsIndexed);

            foreach (var column in indexedColumns)
            {
                if (@event.Namespace.StartsWith("V_"))
                {
                    // Dirty way to skip indexes and primary keys for views
                    continue;
                }

                string indexName = $"idx_{@event.Namespace}_{@event.Table}_{column.Column}";
                ddlSql.AppendLine(
                    $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{@event.Namespace}_{@event.Table}\" (\"{column.Column}\");");
            }
        }

        // If the event schema has a SqlMigrationItem, execute it
        if (@event.SqlMigrationItem != null)
        {
            ddlSql.AppendLine();
            ddlSql.AppendLine(@event.SqlMigrationItem.Sql);
            ddlSql.AppendLine(";"); // An additional semicolon doesn't hurt
        }

        return ddlSql.ToString();
    }

    private string GetSqlType(ValueTypes type)
    {
        return type switch
        {
            ValueTypes.BigInt => "NUMERIC",
            ValueTypes.Int => "BIGINT",
            ValueTypes.String => "TEXT",
            ValueTypes.Address => "TEXT",
            ValueTypes.Boolean => "BOOLEAN",
            ValueTypes.Bytes => "BYTEA",
            ValueTypes.AddressArray => "TEXT[]",
            ValueTypes.Json => "JSON",
            ValueTypes.Double => "DOUBLE PRECISION",
            _ => throw new ArgumentException("Unsupported type")
        };
    }

    private NpgsqlDbType GetNpgsqlDbType(ValueTypes type)
    {
        return type switch
        {
            ValueTypes.BigInt => NpgsqlDbType.Numeric,
            ValueTypes.Int => NpgsqlDbType.Bigint,
            ValueTypes.String => NpgsqlDbType.Text,
            ValueTypes.Address => NpgsqlDbType.Text,
            ValueTypes.Boolean => NpgsqlDbType.Boolean,
            ValueTypes.Bytes => NpgsqlDbType.Bytea,
            ValueTypes.AddressArray => NpgsqlDbType.Array | NpgsqlDbType.Text,
            ValueTypes.Json => NpgsqlDbType.Json,
            ValueTypes.Double => NpgsqlDbType.Double,
            _ => throw new ArgumentException("Unsupported type")
        };
    }

    private void ExecuteNonQuery(NpgsqlConnection connection, string command, IDbDataParameter[]? parameters = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = command;
        cmd.Parameters.AddRange(parameters ?? []);
        cmd.ExecuteNonQuery();
    }

    public long? LatestBlock()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(""blockNumber"") as block_number FROM ""System_Block""
        ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }

    public long? FirstGap()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT (prev.""blockNumber"" + 1) AS gap_start
            FROM (
                     SELECT ""blockNumber"", LEAD(""blockNumber"") OVER (ORDER BY ""blockNumber"") AS next_block_number
                     FROM (
                              SELECT ""blockNumber"" FROM ""System_Block"" ORDER BY ""blockNumber"" DESC LIMIT 1000000
                          ) AS sub
                 ) AS prev
            WHERE prev.next_block_number - prev.""blockNumber"" > 1
            ORDER BY gap_start
            LIMIT 1;
        ";

        object? result = cmd.ExecuteScalar();
        if (result is long longResult)
        {
            return longResult;
        }

        return null;
    }

    public IEnumerable<(long BlockNumber, Hash256 BlockHash)> LastPersistedBlocks(int count = 100)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""System_Block""
            ORDER BY ""blockNumber"" DESC
            LIMIT {count}
        ";

        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), new Hash256(reader.GetString(1)));
        }
    }

    public async Task DeleteAllGreaterOrEqualBlock(long reorgAt)
    {
        NpgsqlTransaction? transaction = null;
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            transaction = await connection.BeginTransactionAsync();
            foreach (var table in Schema.Tables.Values)
            {
                if (table.Namespace.StartsWith("V_") 
                    || table.Table == DatabaseSchema.PathfinderRequestLog 
                    || table.Table == DatabaseSchema.PathfinderResponseLog)
                {
                    // Dirty way to skip views
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"DELETE FROM \"{table.Namespace}_{table.Table}\" WHERE \"blockNumber\" >= @reorgAt;";
                command.Parameters.AddWithValue("@reorgAt", reorgAt);
                command.Transaction = transaction;

                var rowsDeleted = await command.ExecuteNonQueryAsync();
                if (rowsDeleted > 0)
                {
                    Console.WriteLine($"Deleted {rowsDeleted} rows from {table.Namespace}_{table.Table}");
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
            if (transaction != null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public async Task DeleteFromTablesGreaterOrEqualBlock(long reorgAt, IEnumerable<string> tableNames)
    {
        var tableNameSet = new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);

        NpgsqlTransaction? transaction = null;
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            transaction = await connection.BeginTransactionAsync();
            foreach (var table in Schema.Tables.Values)
            {
                if (table.Namespace.StartsWith("V_") 
                    || table.Table == DatabaseSchema.PathfinderRequestLog 
                    || table.Table == DatabaseSchema.PathfinderResponseLog)
                {
                    continue;
                }

                var fullTableName = $"{table.Namespace}_{table.Table}";
                if (!tableNameSet.Contains(fullTableName) && !tableNameSet.Contains(table.Table))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"DELETE FROM \"{fullTableName}\" WHERE \"blockNumber\" >= @reorgAt;";
                command.Parameters.AddWithValue("@reorgAt", reorgAt);
                command.Transaction = transaction;

                var rowsDeleted = await command.ExecuteNonQueryAsync();
                Console.WriteLine($"[REINDEX] Deleted {rowsDeleted} rows from {fullTableName} (block >= {reorgAt})");
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[REINDEX] An error occurred: {ex.Message}");
            if (transaction != null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }


    /// <summary>
    /// Upsert a single (tableName -> blockNumber) mapping.
    /// </summary>
    public void SetEventTableHead(string tableName, long blockNumber)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        const string sql = @"
            INSERT INTO ""System_EventTableHead"" (""tableName"", ""blockNumber"")
            VALUES (@tableName, @blockNumber)
            ON CONFLICT (""tableName"")
                DO UPDATE SET ""blockNumber"" = EXCLUDED.""blockNumber"";
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@tableName", tableName);
        cmd.Parameters.AddWithValue("@blockNumber", blockNumber);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the block number for a table, or null if not found.
    /// </summary>
    public long? GetEventTableHead(string tableName)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        const string sql = @"
            SELECT ""blockNumber""
            FROM ""System_EventTableHead""
            WHERE ""tableName"" = @tableName;
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@tableName", tableName);

        object? result = cmd.ExecuteScalar();
        return result is long l ? l : null;
    }

    /// <summary>
    /// Upsert multiple (tableName -> blockNumber) mappings in a single transaction.
    /// </summary>
    public void SetEventTableHeads(IDictionary<string, long> mappings)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            const string sql = @"
                INSERT INTO ""System_EventTableHead"" (""tableName"", ""blockNumber"")
                VALUES (@tableName, @blockNumber)
                ON CONFLICT (""tableName"")
                    DO UPDATE SET ""blockNumber"" = EXCLUDED.""blockNumber"";
            ";

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;

            // Reuse parameters for each iteration
            var paramTable = cmd.CreateParameter();
            paramTable.ParameterName = "@tableName";
            cmd.Parameters.Add(paramTable);

            var paramBlock = cmd.CreateParameter();
            paramBlock.ParameterName = "@blockNumber";
            cmd.Parameters.Add(paramBlock);

            foreach (var (tName, blockNum) in mappings)
            {
                paramTable.Value = tName;
                paramBlock.Value = blockNum;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Returns all (tableName -> blockNumber) mappings.
    /// </summary>
    public IDictionary<string, long> GetEventTableHeads()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        const string sql = @"
            SELECT ""tableName"", ""blockNumber""
            FROM ""System_EventTableHead"";
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, long>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0), reader.GetInt64(1));
        }

        return result;
    }

    /// <summary>
    /// Gets the maximum block number for each event table.
    /// Excludes System tables and views.
    /// </summary>
    public IDictionary<string, long> GetMaxBlockPerTable()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        var result = new Dictionary<string, long>();

        foreach (var table in Schema.Tables.Values)
        {
            // Skip views and system tables (except System_Block which we include for comparison)
            if (table.Namespace.StartsWith("V_"))
                continue;

            if (table.Namespace == Common.DatabaseSchema.SystemNamespace &&
                table.Table != Common.DatabaseSchema.Block)
                continue;

            var tableName = $"{table.Namespace}_{table.Table}";

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"SELECT MAX(""blockNumber"") FROM ""{tableName}""";
                var maxBlock = cmd.ExecuteScalar();

                if (maxBlock is long block)
                {
                    result[tableName] = block;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetMaxBlockPerTable] Error querying {tableName}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the safe resume block by analyzing event table consistency.
    /// Only considers tables that are significantly behind as potential partial write issues.
    /// Tables with sparse events (like CrcV2_Stopped) are handled separately.
    /// </summary>
    public long? GetSafeResumeBlock()
    {
        var maxBlocks = GetMaxBlockPerTable();

        if (maxBlocks.Count == 0)
            return null;

        // Get the System_Block max for reference
        maxBlocks.TryGetValue("System_Block", out var systemBlockMax);

        // Get event tables only (exclude System_Block)
        var eventTableMaxBlocks = maxBlocks
            .Where(kvp => !kvp.Key.StartsWith("System_"))
            .ToList();

        if (eventTableMaxBlocks.Count == 0)
            return systemBlockMax > 0 ? systemBlockMax : null;

        var maxEventBlock = eventTableMaxBlocks.Max(kvp => kvp.Value);

        // Consider a table "behind" only if it's within a reasonable range of the max
        // Tables that are way behind (>100k blocks) are likely just sparse event tables
        // We use a threshold of 50,000 blocks (roughly 3 days on Gnosis Chain)
        const long significantGapThreshold = 50_000;

        var recentTables = eventTableMaxBlocks
            .Where(kvp => maxEventBlock - kvp.Value < significantGapThreshold)
            .ToList();

        var sparseTables = eventTableMaxBlocks
            .Where(kvp => maxEventBlock - kvp.Value >= significantGapThreshold)
            .ToList();

        if (recentTables.Count == 0)
        {
            // All tables are sparse - just use System_Block
            return systemBlockMax > 0 ? systemBlockMax : null;
        }

        var minRecentBlock = recentTables.Min(kvp => kvp.Value);
        var maxRecentBlock = recentTables.Max(kvp => kvp.Value);

        // Log sparse tables for visibility (these are expected to be behind)
        if (sparseTables.Count > 0)
        {
            Console.WriteLine($"[GetSafeResumeBlock] Sparse event tables (expected to be behind):");
            foreach (var kvp in sparseTables.OrderBy(k => k.Value))
            {
                Console.WriteLine($"    {kvp.Key}: {kvp.Value} ({maxEventBlock - kvp.Value:N0} blocks behind)");
            }
        }

        // Only report as inconsistency if recent tables have significant gaps
        // A gap of more than 1 batch size (default 20k blocks) indicates a problem
        const long batchGapThreshold = 20_000;
        if (maxRecentBlock - minRecentBlock > batchGapThreshold)
        {
            Console.WriteLine($"[GetSafeResumeBlock] INCONSISTENCY DETECTED - possible partial write:");
            Console.WriteLine($"  System_Block max: {systemBlockMax:N0}");
            Console.WriteLine($"  Recent tables min: {minRecentBlock:N0}, max: {maxRecentBlock:N0}");
            Console.WriteLine($"  Gap: {maxRecentBlock - minRecentBlock:N0} blocks");

            foreach (var kvp in recentTables.OrderBy(k => k.Value).Take(10))
            {
                Console.WriteLine($"    {kvp.Key}: {kvp.Value:N0}");
            }

            // Return the min to trigger recovery
            return minRecentBlock;
        }

        // No significant gap detected - use the max from recent tables
        // This allows normal operation even when sparse tables are behind
        return maxRecentBlock;
    }
}
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
        var dataList = data.ToList();
        if (dataList.Count == 0)
            return;

        var tableSchema = Schema.Tables[(@namespace, table)];
        var columns = tableSchema.Columns;
        var columnList = string.Join(", ", columns.Select(c => $"\"{c.Column}\""));

        // Build primary key columns for ON CONFLICT clause
        var primaryKeyColumns = GetPrimaryKeyColumns(tableSchema);
        var primaryKeyList = string.Join(", ", primaryKeyColumns.Select(c => $"\"{c}\""));

        // Build arrays for each column (UNNEST optimization)
        var columnArrays = new object?[columns.Count][];
        for (int i = 0; i < columns.Count; i++)
        {
            columnArrays[i] = new object?[dataList.Count];
        }

        // Populate the arrays with values from each row
        for (int rowIndex = 0; rowIndex < dataList.Count; rowIndex++)
        {
            var indexEvent = dataList[rowIndex];
            for (int colIndex = 0; colIndex < columns.Count; colIndex++)
            {
                var column = columns[colIndex];
                var value = propertyMap.Map[(@namespace, table)][column.Column](indexEvent);
                columnArrays[colIndex][rowIndex] = value;
            }
        }

        // Build UNNEST SQL with proper type casts
        var unnestParams = new List<string>();
        for (int i = 0; i < columns.Count; i++)
        {
            var sqlType = GetSqlType(columns[i].Type);
            unnestParams.Add($"UNNEST(@p{i})::{sqlType}");
        }
        var unnestList = string.Join(", ", unnestParams);

        var sql = $@"
            INSERT INTO ""{tableSchema.Namespace}_{tableSchema.Table}"" ({columnList})
            SELECT {unnestList}
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

        await using var command = new NpgsqlCommand(sql, connection);

        // Add array parameters with proper NpgsqlDbType
        for (int i = 0; i < columns.Count; i++)
        {
            var arrayType = GetNpgsqlArrayDbType(columns[i].Type);
            var param = new NpgsqlParameter($"@p{i}", arrayType)
            {
                Value = columnArrays[i]
            };
            command.Parameters.Add(param);
        }

        try
        {
            var rowsAffected = await command.ExecuteNonQueryAsync();
            var skippedCount = dataList.Count - rowsAffected;

            if (skippedCount > 0)
            {
                Console.WriteLine($"[WriteBatchWithUpsert] {tableSchema.Namespace}_{tableSchema.Table}: " +
                                  $"Inserted {rowsAffected}, skipped {skippedCount} duplicates");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in WriteBatchWithUpsert for {tableSchema.Namespace}_{tableSchema.Table}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Writes multiple batches to different tables within a single transaction.
    /// All writes succeed or all fail together, preventing partial writes.
    /// </summary>
    public async Task WriteBatchesAtomic(
        IDictionary<(string Namespace, string Table), IEnumerable<object>> batches,
        ISchemaPropertyMap propertyMap,
        bool useUpsert = false)
    {
        if (batches.Count == 0)
            return;

        // Build connection string with extended timeouts for large batch operations
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            CommandTimeout = 600,  // 10 minutes for atomic batch
            Timeout = 300,
            WriteBufferSize = 32768,
            ReadBufferSize = 32768
        };

        await using var connection = new NpgsqlConnection(csb.ToString());
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var batch in batches)
            {
                var (@namespace, table) = batch.Key;
                var data = batch.Value.ToList();

                if (data.Count == 0)
                    continue;

                if (useUpsert)
                {
                    await WriteBatchWithUpsertInternal(connection, transaction, @namespace, table, data, propertyMap);
                }
                else
                {
                    await WriteBatchCopyInternal(connection, transaction, @namespace, table, data, propertyMap);
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WriteBatchesAtomic] Transaction failed, rolling back: {ex.Message}");
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Internal COPY implementation that uses an existing connection and transaction.
    /// </summary>
    private async Task WriteBatchCopyInternal(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string @namespace,
        string table,
        IList<object> data,
        ISchemaPropertyMap propertyMap)
    {
        var tableSchema = Schema.Tables[(@namespace, table)];
        var columnTypes = tableSchema.Columns.ToDictionary(o => o.Column, o => o.Type);
        var columnList = string.Join(", ", columnTypes.Select(o => $"\"{o.Key}\""));

        // Note: COPY doesn't support transactions directly, but it runs within the connection's transaction context
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

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Internal upsert implementation that uses an existing connection and transaction.
    /// Uses PostgreSQL UNNEST for efficient batch inserts.
    /// </summary>
    private async Task WriteBatchWithUpsertInternal(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string @namespace,
        string table,
        IList<object> data,
        ISchemaPropertyMap propertyMap)
    {
        if (data.Count == 0)
            return;

        var tableSchema = Schema.Tables[(@namespace, table)];
        var columns = tableSchema.Columns;
        var columnList = string.Join(", ", columns.Select(c => $"\"{c.Column}\""));

        var primaryKeyColumns = GetPrimaryKeyColumns(tableSchema);
        var primaryKeyList = string.Join(", ", primaryKeyColumns.Select(c => $"\"{c}\""));

        // Build arrays for each column
        var columnArrays = new object?[columns.Count][];
        for (int i = 0; i < columns.Count; i++)
        {
            columnArrays[i] = new object?[data.Count];
        }

        // Populate the arrays with values from each row
        for (int rowIndex = 0; rowIndex < data.Count; rowIndex++)
        {
            var indexEvent = data[rowIndex];
            for (int colIndex = 0; colIndex < columns.Count; colIndex++)
            {
                var column = columns[colIndex];
                var value = propertyMap.Map[(@namespace, table)][column.Column](indexEvent);
                columnArrays[colIndex][rowIndex] = value;
            }
        }

        // Build UNNEST SQL with proper type casts
        var unnestParams = new List<string>();
        for (int i = 0; i < columns.Count; i++)
        {
            var sqlType = GetSqlType(columns[i].Type);
            unnestParams.Add($"UNNEST(@p{i})::{sqlType}");
        }
        var unnestList = string.Join(", ", unnestParams);

        var sql = $@"
            INSERT INTO ""{tableSchema.Namespace}_{tableSchema.Table}"" ({columnList})
            SELECT {unnestList}
            ON CONFLICT ({primaryKeyList}) DO NOTHING
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);

        // Add array parameters with proper NpgsqlDbType
        for (int i = 0; i < columns.Count; i++)
        {
            var arrayType = GetNpgsqlArrayDbType(columns[i].Type);
            var param = new NpgsqlParameter($"@p{i}", arrayType)
            {
                Value = columnArrays[i]
            };
            command.Parameters.Add(param);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Gets the NpgsqlDbType for array parameters.
    /// </summary>
    private NpgsqlDbType GetNpgsqlArrayDbType(ValueTypes type)
    {
        return type switch
        {
            ValueTypes.BigInt => NpgsqlDbType.Array | NpgsqlDbType.Numeric,
            ValueTypes.Int => NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            ValueTypes.String => NpgsqlDbType.Array | NpgsqlDbType.Text,
            ValueTypes.Address => NpgsqlDbType.Array | NpgsqlDbType.Text,
            ValueTypes.Boolean => NpgsqlDbType.Array | NpgsqlDbType.Boolean,
            ValueTypes.Bytes => NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            // For array of arrays (AddressArray), we need to handle specially
            ValueTypes.AddressArray => NpgsqlDbType.Array | NpgsqlDbType.Array | NpgsqlDbType.Text,
            ValueTypes.Json => NpgsqlDbType.Array | NpgsqlDbType.Json,
            ValueTypes.Double => NpgsqlDbType.Array | NpgsqlDbType.Double,
            _ => throw new ArgumentException($"Unsupported type for array: {type}")
        };
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
        // Delete from each table in separate transactions to avoid long-running transactions
        // that can timeout. This is safer for large deletions spanning millions of blocks.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var table in Schema.Tables.Values)
        {
            if (table.Namespace.StartsWith("V_")
                || table.Table == DatabaseSchema.PathfinderRequestLog
                || table.Table == DatabaseSchema.PathfinderResponseLog)
            {
                // Dirty way to skip views
                continue;
            }

            var tableName = $"{table.Namespace}_{table.Table}";
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM \"{tableName}\" WHERE \"blockNumber\" >= @reorgAt;";
                command.Parameters.AddWithValue("@reorgAt", reorgAt);
                command.CommandTimeout = 600; // 10 minutes per table

                var rowsDeleted = await command.ExecuteNonQueryAsync();
                if (rowsDeleted > 0)
                {
                    Console.WriteLine($"Deleted {rowsDeleted} rows from {tableName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting from {tableName}: {ex.Message}");
                throw;
            }
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
        // Use extended timeout for potentially slow queries after large deletions
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            CommandTimeout = 120,  // 2 minutes
            Timeout = 60
        };

        using var connection = new NpgsqlConnection(csb.ToString());
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
                cmd.CommandTimeout = 60;  // 1 minute per table
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
    ///
    /// With atomic batch writes (WriteBatchesAtomic), event tables should be consistent with System_Block.
    /// If an event table has a lower max block than System_Block, it simply means no events occurred
    /// for that table in the more recent blocks - this is normal for sparse event tables.
    ///
    /// A TRUE inconsistency only occurs if:
    /// 1. An event was written but System_Block wasn't updated (crash during non-atomic write)
    /// 2. System_Block was written but event tables weren't (crash during non-atomic write)
    ///
    /// Since we now use atomic writes, we simply return System_Block max as the safe resume point.
    /// The caller should compare this with LatestBlock() and FirstGap() to determine actual resume point.
    /// </summary>
    public long? GetSafeResumeBlock()
    {
        var maxBlocks = GetMaxBlockPerTable();

        if (maxBlocks.Count == 0)
            return null;

        // Get the System_Block max - this is our primary source of truth
        if (!maxBlocks.TryGetValue("System_Block", out var systemBlockMax) || systemBlockMax == 0)
            return null;

        // Get event tables only (exclude System_Block)
        var eventTableMaxBlocks = maxBlocks
            .Where(kvp => !kvp.Key.StartsWith("System_"))
            .ToList();

        if (eventTableMaxBlocks.Count == 0)
            return systemBlockMax;

        // Check for partial write scenario: events written but System_Block not updated.
        // This can happen if a crash occurs between Sink.Flush() and FlushBlocks().
        // In this case, event tables will be AHEAD of System_Block.
        var tablesAheadOfSystem = eventTableMaxBlocks
            .Where(kvp => kvp.Value > systemBlockMax)
            .ToList();

        if (tablesAheadOfSystem.Count > 0)
        {
            Console.WriteLine($"[GetSafeResumeBlock] PARTIAL WRITE DETECTED: Event tables ahead of System_Block:");
            Console.WriteLine($"  System_Block max: {systemBlockMax:N0}");
            foreach (var kvp in tablesAheadOfSystem)
            {
                Console.WriteLine($"    {kvp.Key}: {kvp.Value:N0} (+{kvp.Value - systemBlockMax:N0})");
            }
            // Return System_Block max - we need to re-index from there.
            // The events that are ahead will be handled by upsert (ON CONFLICT DO NOTHING).
            Console.WriteLine($"  Will resume from System_Block max ({systemBlockMax:N0}) - duplicates handled by upsert");
            return systemBlockMax;
        }

        // Event tables at or below System_Block is normal - they just don't have recent events.
        // Log some statistics for debugging (only if there's significant variance).
        var maxEventBlock = eventTableMaxBlocks.Max(kvp => kvp.Value);
        var minEventBlock = eventTableMaxBlocks.Min(kvp => kvp.Value);
        var blocksVariance = maxEventBlock - minEventBlock;

        if (blocksVariance > 100_000)  // Only log if >100k block variance
        {
            Console.WriteLine($"[GetSafeResumeBlock] Event table statistics:");
            Console.WriteLine($"  System_Block: {systemBlockMax:N0}");
            Console.WriteLine($"  Event tables: min={minEventBlock:N0}, max={maxEventBlock:N0}");
            Console.WriteLine($"  Variance: {blocksVariance:N0} blocks (normal for sparse tables)");
        }

        // Return System_Block max as the safe resume point
        return systemBlockMax;
    }
}
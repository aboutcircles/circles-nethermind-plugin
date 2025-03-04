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

        var resultSchema = reader.GetColumnSchema().ToArray();
        var columnNames = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames[i] = reader.GetName(i);
        }

        var resultRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (resultSchema[i].NpgsqlDbType == NpgsqlDbType.Numeric)
                {
                    row[i] = reader.GetFieldValue<BigInteger?>(i);
                }
                else
                {
                    row[i] = reader.GetValue(i);
                }

                if (row[i] is DBNull)
                {
                    row[i] = null;
                }
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
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            StringBuilder ddlSql = new StringBuilder();
            foreach (var table in Schema.Tables)
            {
                var ddl = GetDdl(table.Value);
                ddlSql.AppendLine(ddl);
            }

            ExecuteNonQuery(connection, ddlSql.ToString());

            StringBuilder primaryKeyDdl = new StringBuilder();
            foreach (var table in Schema.Tables)
            {
                if (table.Key.Namespace.StartsWith("V_"))
                {
                    // Skip views
                    continue;
                }

                if (HasPrimaryKey(connection, table.Value))
                {
                    continue;
                }

                if (table.Value is
                    {
                        Namespace: Common.DatabaseSchema.SystemNamespace,
                        Table: Common.DatabaseSchema.BlockTable
                    })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"blockNumber\");");
                }
                else if (table.Value is
                         {
                             Namespace: Common.DatabaseSchema.SystemNamespace,
                             Table: Common.DatabaseSchema.EventTableHeadTable
                         })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"tableName\");");
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

        await using var connection = new NpgsqlConnection(ConnectionString);
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

        await writer.CompleteAsync();
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

    public async Task DeleteFromBlockOnwards(long reorgAt)
    {
        NpgsqlTransaction transaction = null;
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            transaction = await connection.BeginTransactionAsync();
            foreach (var table in Schema.Tables.Values)
            {
                if (table.Namespace.StartsWith("V_"))
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
}
using System.Data;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core.Crypto;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Postgres;

public class PostgresDb(string connectionString, IDatabaseSchema schema) : IDatabase
{
    public IDatabaseSchema Schema { get; } = schema;

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
        using var connection = new NpgsqlConnection(connectionString);
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
                if (HasPrimaryKey(connection, table.Value))
                {
                    continue;
                }

                var additionalKeyColumns = table.Value.Columns
                    .Where(column => column.IncludeInPrimaryKey)
                    .Select(column => $"\"{column.Column}\"")
                    .ToArray();
                var additionalKeyColumnsString = string.Join(", ", additionalKeyColumns);
                if (additionalKeyColumns.Length > 0)
                {
                    additionalKeyColumnsString = ", " + additionalKeyColumnsString;
                }

                if (table.Value is { Namespace: "System", Table: "Block" })
                {
                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"blockNumber\");");
                }
                else
                {
                    if (table.Value.Namespace.StartsWith("V_"))
                    {
                        // Dirty way to skip indexes and primary keys for views
                        continue;
                    }

                    primaryKeyDdl.AppendLine(
                        $"ALTER TABLE \"{table.Value.Namespace}_{table.Value.Table}\" ADD PRIMARY KEY (\"blockNumber\", \"transactionIndex\", \"logIndex\"{additionalKeyColumnsString});");
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

        await using var connection = new NpgsqlConnection(connectionString);
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
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(""blockNumber"") as block_number FROM ""{"System_Block"}""
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
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT (prev.""blockNumber"" + 1) AS gap_start
            --        ,(prev.next_block_number - 1) AS gap_end
            --        ,(prev.next_block_number - prev.""blockNumber"" - 1) AS gap_size
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
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT ""blockNumber"", ""blockHash""
            FROM ""{"System_Block"}""
            ORDER BY ""blockNumber"" DESC
            LIMIT {count}
        ";

        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), new Hash256(reader.GetString(1)));
        }
    }

    public DatabaseQueryResult Select(ParameterizedSql select)
    {
        using var connection = new NpgsqlConnection(connectionString);
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
                    row[i] = reader.GetFieldValue<BigInteger>(i);
                }
                else
                {
                    row[i] = reader.GetValue(i);
                    if (row[i] is DBNull)
                    {
                        row[i] = null;
                    }
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

    public async Task DeleteFromBlockOnwards(long reorgAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var table in Schema.Tables.Values)
            {
                if (table.Namespace.StartsWith("V_"))
                {
                    // Dirty way to skip views
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"DELETE FROM \"{table.Namespace}_{table.Table}\" WHERE \"{"blockNumber"}\" >= @reorgAt;";
                command.Parameters.AddWithValue("@reorgAt", reorgAt);
                command.Transaction = transaction;

                command.ExecuteNonQuery();
            }

            await transaction.CommitAsync();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
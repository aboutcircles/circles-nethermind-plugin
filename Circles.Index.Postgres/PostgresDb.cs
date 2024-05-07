using System.Data;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.Postgres;

public class PostgresDb(string connectionString, IDatabaseSchema schema, ILogger logger) : IDatabase
{
    public IDatabaseSchema Schema { get; } = schema;

    public void Migrate()
    {
        foreach (var table in Schema.Tables)
        {
            var ddl = GetDdl(table.Value);
            ExecuteNonQuery(ddl);
        }
    }

    public async Task WriteBatch(string table, IEnumerable<object> data, ISchemaPropertyMap propertyMap)
    {
        var tableSchema = Schema.Tables[table];
        var columnTypes = tableSchema.Columns.ToDictionary(o => o.Column, o => o.Type);
        var columnList = string.Join(", ", columnTypes.Select(o => $"\"{o.Key}\""));

        await using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        await using var writer = await connection.BeginBinaryImportAsync(
            $"COPY \"{table}\" ({columnList}) FROM STDIN (FORMAT BINARY)"
        );

        foreach (var indexEvent in data)
        {
            await writer.StartRowAsync();
            foreach (var column in tableSchema.Columns)
            {
                var value = propertyMap.Map[table][column.Column](indexEvent);
                await writer.WriteAsync(value, GetNpgsqlDbType(column.Type));
            }
        }

        await writer.CompleteAsync();
    }

    private string GetDdl(EventSchema @event)
    {
        StringBuilder ddlSql = new StringBuilder();
        ddlSql.AppendLine($"CREATE TABLE IF NOT EXISTS \"{@event.Table}\" (");

        List<string> columnDefinitions = new List<string>();
        List<string> primaryKeyColumns = new List<string>();

        foreach (var column in @event.Columns)
        {
            string columnType = GetSqlType(column.Type);
            string columnName = column.Column;
            string columnDefinition = $"\"{columnName}\" {columnType}";

            // if (column.IsPrimaryKey)
            // {
            //     primaryKeyColumns.Add(columnName);
            // }

            columnDefinitions.Add(columnDefinition);
        }

        if (primaryKeyColumns.Any())
        {
            columnDefinitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns.Select(o => $"\"{o}\""))})");
        }

        ddlSql.AppendLine(string.Join(",\n", columnDefinitions));
        ddlSql.AppendLine(");");
        ddlSql.AppendLine();

        // Generate index creation statements
        var indexedColumns = @event.Columns
            .Where(column =>
                column is
                {
                    IsIndexed: true,
                    // IsPrimaryKey: false
                });

        foreach (var column in indexedColumns)
        {
            string indexName = $"idx_{@event.Table}_{column.Column}";
            ddlSql.AppendLine(
                $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{@event.Table}\" (\"{column.Column}\");");
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
            _ => throw new ArgumentException("Unsupported type")
        };
    }

    private void ExecuteNonQuery(string command, IDbDataParameter[]? parameters = null)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = command;
        cmd.Parameters.AddRange(parameters ?? []);

        Console.WriteLine($"Executing: {command}");
        Console.WriteLine(
            $" with parameters: {string.Join(", ", cmd.Parameters.Select(o => o.Value?.ToString() ?? "<null>"))}");
        cmd.ExecuteNonQuery();
    }

    public long? LatestBlock()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        NpgsqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT MAX(""BlockNumber"") as block_number FROM ""{"Block"}""
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
        SELECT (prev.""BlockNumber"" + 1) AS gap_start 
        FROM (
            SELECT ""BlockNumber"", LEAD(""BlockNumber"") OVER (ORDER BY ""BlockNumber"") AS next_block_number 
            FROM (
                SELECT ""BlockNumber"" FROM ""{"Block"}"" ORDER BY ""BlockNumber"" DESC LIMIT 500000
            ) AS sub
        ) AS prev
        WHERE prev.next_block_number - prev.""BlockNumber"" > 1
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
            SELECT ""BlockNumber"", ""BlockHash""
            FROM {"Block"}
            ORDER BY ""BlockNumber"" DESC
            LIMIT {count}
        ";

        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), new Hash256(reader.GetString(1)));
        }
    }

    public IEnumerable<object[]> Select(Select select)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = select.ToSql();
        foreach (var param in select.GetParameters(Schema))
        {
            command.Parameters.Add(param);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            yield return row;
        }
    }

    public IDataParameter CreateParameter()
    {
        return new NpgsqlParameter();
    }

    public async Task DeleteFromBlockOnwards(long reorgAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var tableName in Schema.Tables.Keys)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM \"{tableName}\" WHERE \"{"BlockNumber"}\" >= @reorgAt;";
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

    public object? Convert(object? input, ValueTypes target)
    {
        if (input == null)
        {
            return null;
        }

        switch (target)
        {
            case ValueTypes.String:
                return input.ToString() ?? throw new ArgumentNullException(nameof(input));
            case ValueTypes.Int:
                return System.Convert.ToInt64(input.ToString());
            case ValueTypes.BigInt when input is string i:
                return BigInteger.Parse(i);
            case ValueTypes.BigInt when input is BigInteger:
                return input;
            case ValueTypes.BigInt when input is UInt256:
            case ValueTypes.BigInt when input is ulong:
            case ValueTypes.BigInt when input is uint:
            case ValueTypes.BigInt when input is long:
            case ValueTypes.BigInt when input is int:
                return (BigInteger)input;
            case ValueTypes.BigInt:
                return BigInteger.Parse(input.ToString() ?? throw new ArgumentNullException(nameof(input)));
            case ValueTypes.Address when input is string i:
                return i.ToLowerInvariant();
            case ValueTypes.Address:
                return input.ToString()?.ToLowerInvariant();
            case ValueTypes.Boolean when input is bool b:
                return b;
            case ValueTypes.Boolean when input is string s:
                return bool.Parse(s);
            case ValueTypes.Boolean when input is int i:
                return i != 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target,
                    $"Cannot convert input {input} (type: {input.GetType().Name}) to target type {target}.");
        }
    }
}
using Circles.Common;
using Npgsql;
using Testcontainers.PostgreSql;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// A view's columns are declared in the "-- COLUMNS:" comment of its SQL file, separately from the
/// SQL that creates it, and circles_tables reports the declared types to clients. This creates the
/// schema on an empty database and checks every declared view column against the view Postgres built.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class ViewColumnTypeTests
{
    // The SQL type PostgresDb.GetDdl gives a base-table column of each declared type.
    private static readonly Dictionary<ValueTypes, string> TableColumnType = new()
    {
        [ValueTypes.BigInt] = "NUMERIC",
        [ValueTypes.Int] = "BIGINT",
        [ValueTypes.String] = "TEXT",
        [ValueTypes.Address] = "TEXT",
        [ValueTypes.Boolean] = "BOOLEAN",
        [ValueTypes.Bytes] = "BYTEA",
        [ValueTypes.AddressArray] = "TEXT[]",
        [ValueTypes.Json] = "JSON",
        [ValueTypes.Double] = "DOUBLE PRECISION",
    };

    // The declared types that describe what a client receives for a view column of each Postgres
    // type: numeric arrives as a JSON string (BigInt), bigint and integer as a JSON number (Int),
    // and a timestamp as an ISO 8601 string (String).
    private static readonly Dictionary<string, ValueTypes[]> DeclarableTypes = new()
    {
        ["numeric"] = [ValueTypes.BigInt],
        ["bigint"] = [ValueTypes.Int],
        ["integer"] = [ValueTypes.Int],
        ["text"] = [ValueTypes.String, ValueTypes.Address],
        ["timestamp with time zone"] = [ValueTypes.String],
        ["boolean"] = [ValueTypes.Boolean],
        ["bytea"] = [ValueTypes.Bytes],
        ["text[]"] = [ValueTypes.AddressArray],
        ["json"] = [ValueTypes.Json],
        ["jsonb"] = [ValueTypes.Json],
        ["double precision"] = [ValueTypes.Double],
    };

    private PostgreSqlContainer? _postgres;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Docker/Postgres test container unavailable: {ex.Message}");
            return;
        }

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        foreach (var sql in MigrationSql(new CompositeDatabaseSchema(SchemaProvider.AllSchemas.ToArray())))
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    /// <summary>
    /// The statements of PostgresDb.Migrate that decide column types, in its order: tables
    /// (columns, then their migration SQL), functions, then views. PostgresDb itself needs the
    /// Nethermind runtime, which the test projects do not have. Indexes and keys are left out.
    /// </summary>
    private static IEnumerable<string> MigrationSql(IDatabaseSchema schema)
    {
        var tables = schema.Tables.Values.Where(t => !t.Namespace.StartsWith("V_"));
        var views = schema.Tables.Values.Where(t => t.Namespace.StartsWith("V_"));

        foreach (var table in tables)
        {
            var columns = table.Columns.Select(c => $"\"{c.Column}\" {TableColumnType[c.Type]}");
            yield return $"CREATE TABLE IF NOT EXISTS \"{table.Namespace}_{table.Table}\" ({string.Join(", ", columns)});"
                         + (table.SqlMigrationItem?.Sql ?? "");
        }

        foreach (var function in schema.FunctionSql)
            yield return function;

        foreach (var view in views.Where(v => v.SqlMigrationItem != null))
            yield return view.SqlMigrationItem!.Sql;
    }

    [Test]
    public async Task Every_declared_view_column_exists_with_the_type_clients_receive()
    {
        // pg_attribute rather than information_schema.columns, which leaves out materialized views.
        var databaseColumns = new Dictionary<(string View, string Column), string>();
        await using (var conn = new NpgsqlConnection(_postgres!.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod)
                FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE c.relkind IN ('v', 'm') AND a.attnum > 0 AND NOT a.attisdropped
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databaseColumns[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        var views = SchemaProvider.AllSchemas
            .SelectMany(s => s.Tables)
            .Where(t => t.Key.Namespace.StartsWith("V_"))
            .ToList();
        Assert.That(views, Is.Not.Empty);

        var mismatches = new List<string>();
        foreach (var (key, view) in views)
        {
            var name = $"{key.Namespace}_{key.Table}";
            foreach (var column in view.Columns)
            {
                if (!databaseColumns.TryGetValue((name, column.Column), out var actual))
                    mismatches.Add($"{name}.{column.Column}: declared {column.Type}, not in the view");
                else if (!DeclarableTypes.TryGetValue(actual, out var declarable) || !declarable.Contains(column.Type))
                    mismatches.Add($"{name}.{column.Column}: declared {column.Type}, view has {actual}");
            }

            // circles_query rejects a column that is not declared, so an undeclared one cannot be read.
            var declared = view.Columns.Select(c => c.Column).ToHashSet();
            foreach (var ((viewName, column), actual) in databaseColumns)
            {
                if (viewName == name && !declared.Contains(column))
                    mismatches.Add($"{name}.{column}: view has {actual}, not declared");
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));
    }
}

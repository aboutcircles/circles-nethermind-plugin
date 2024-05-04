using System.Text;
using Npgsql;

namespace Circles.Index.Common;

public interface ISchema
{
    public IDictionary<Tables, TableSchema> TableSchemas { get; }
    
    public void Migrate(NpgsqlConnection connection)
    {
        var sqlCommands = GenerateSqlCommands();

        using var command = connection.CreateCommand();
        command.CommandText = string.Join("\n\n", sqlCommands);
        command.ExecuteNonQuery();
    }

    private List<string> GenerateSqlCommands()
    {
        List<string> commands = new List<string>();

        foreach (var table in TableSchemas)
        {
            StringBuilder tableCommand = new StringBuilder();
            string tableName = table.Key.GetIdentifier();
            tableCommand.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");

            List<string> columnDefinitions = new List<string>();
            List<string> primaryKeyColumns = new List<string>();

            foreach (var column in table.Value.Columns)
            {
                string columnType = GetSqlType(column.Type);
                string columnName = column.Column.GetIdentifier();
                string columnDefinition = $"{columnName} {columnType}";

                if (column.IsPrimaryKey)
                {
                    primaryKeyColumns.Add(columnName);
                }

                columnDefinitions.Add(columnDefinition);
            }

            if (primaryKeyColumns.Any())
            {
                columnDefinitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns)})");
            }

            tableCommand.AppendLine(string.Join(",\n", columnDefinitions));
            tableCommand.AppendLine(");");
            commands.Add(tableCommand.ToString());

            // Generate index creation statements
            foreach (var column in table.Value.Columns.Where(c => c.IsIndexed && !c.IsPrimaryKey))
            {
                string columnName = column.Column.GetIdentifier();
                string indexName = $"idx_{tableName.ToLower()}_{columnName.ToLower()}";
                commands.Add($"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} ({columnName});");
            }
        }

        return commands;
    }

    private static string GetSqlType(ValueTypes type)
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
}
using System.Data;

namespace Circles.Index.Common;

public abstract class QueryPredicate : IQuery
{
    protected readonly string Field;
    public readonly (string Namespace, string Table) Table;
    public readonly string Column;
    protected readonly string ParameterName;
    public readonly object? Value;
    protected readonly IDatabase Database;

    internal QueryPredicate(IDatabase database, (string Namespace, string Table) table, string column, object? value)
    {
        Database = database;
        Table = table;
        Column = column;
        Field = column;
        ParameterName = $"@{column.Replace(".", "")}";
        Value = value;
    }

    public abstract string ToSql(IDatabaseSchema schema);

    public IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema)
    {
        var parameter = Database.CreateParameter();
        if (parameter is null)
        {
            throw new InvalidOperationException("The provider did not return a parameter object.");
        }

        parameter.ParameterName = ParameterName;

        if (!schema.Tables.TryGetValue(Table, out var tableSchema))
        {
            throw new Exception($"The schema doesn't contain a table with the name {Table}");
        }

        var column = tableSchema.Columns.FirstOrDefault(o => o.Column == Column);
        if (column == default)
        {
            throw new Exception($"The table {Table} doesn't contain a column with the name {Column}");
        }

        parameter.Value = Database.Convert(Value, column.Type);

        yield return parameter;
    }
}
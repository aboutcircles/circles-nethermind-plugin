using System.Data;
using System.Data.Common;

namespace Circles.Index.Common;

public abstract class QueryPredicate : IQuery
{
    protected readonly string Field;
    public readonly Tables Table;
    public readonly Columns Column;
    protected readonly string ParameterName;
    public readonly object Value;
    protected readonly DbProviderFactory Provider;

    internal QueryPredicate(DbProviderFactory provider, Tables table, Columns column, object value)
    {
        Provider = provider;
        Table = table;
        Column = column;
        Field = column.GetIdentifier();
        ParameterName = $"@{column.ToString().Replace(".", "")}";
        Value = value;
    }

    public abstract string ToSql();

    public IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema)
    {
        var parameter = Provider.CreateParameter();
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

        parameter.Value = Query.Convert(Value, column.Type);

        yield return parameter;
    }
}
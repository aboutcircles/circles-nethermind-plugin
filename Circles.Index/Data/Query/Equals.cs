using System.Data;
using System.Data.Common;

namespace Circles.Index.Data.Query;

public class Equals : IQuery
{
    private readonly string _field;
    public readonly Tables Table;
    public readonly Columns Column;
    private readonly string _parameterName;
    public readonly object? Value;
    private readonly DbProviderFactory _provider;

    internal Equals(DbProviderFactory provider, Tables table, Columns column, object? value)
    {
        _provider = provider;
        Table = table;
        Column = column;
        _field = column.GetIdentifier();
        _parameterName = $"@{column.ToString().Replace(".", "")}";
        Value = value;
    }

    public string ToSql() => $"{_field} = {_parameterName}";

    public IEnumerable<IDataParameter> GetParameters()
    {
        var parameter = _provider.CreateParameter();
        if (parameter is null)
        {
            throw new InvalidOperationException("The provider did not return a parameter object.");
        }

        parameter.ParameterName = _parameterName;
        var targetType = Schema.TableSchemas[Table].Columns.First(o => o.Column == Column).Type;
        parameter.Value = Query.Convert(Value, targetType) ?? DBNull.Value;
        yield return parameter;
    }
}
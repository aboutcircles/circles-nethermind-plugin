using System.Data;
using System.Data.Common;
using Circles.Index.Common;

namespace Circles.Index.Data.Query;

public class LessThan : IQuery
{
    private readonly string _field;
    public readonly Tables Table;
    public readonly Columns Column;
    private readonly string _parameterName;
    public readonly object Value;
    private readonly DbProviderFactory _provider;

    internal LessThan(DbProviderFactory provider, Tables table, Columns column, object value)
    {
        _provider = provider;
        Table = table;
        Column = column;
        _field = column.GetIdentifier();
        _parameterName = $"@{column.ToString().Replace(".", "")}";
        Value = value;
    }

    public string ToSql() => $"{_field} < {_parameterName}";

    public IEnumerable<IDataParameter> GetParameters()
    {
        var parameter = _provider.CreateParameter();
        if (parameter is null)
        {
            throw new InvalidOperationException("The provider did not return a parameter object.");
        }

        parameter.ParameterName = _parameterName;
        foreach (var schema in Settings.Schemas)
        {
            if (!schema.TableSchemas.TryGetValue(Table, out var tableSchema))
            {
                continue;
            }
            var column = tableSchema.Columns.FirstOrDefault(o => o.Column == Column);
            if (column == default)
            {
                continue;
            }
            
            parameter.Value = Query.Convert(Value, column.Type);
            break;
        }

        yield return parameter;
    }
}
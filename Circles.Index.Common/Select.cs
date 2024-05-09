using System.Data;
using System.Text;

namespace Circles.Index.Common;

public class Select : IQuery
{
    public readonly (string Namespace, string Table) Table;
    private readonly (string Namespace, string Table) _table;
    public readonly IEnumerable<string> Columns;
    private readonly List<string> _databaseFields;
    public readonly List<IQuery> Conditions;
    public readonly List<(string Column, SortOrder Order)> OrderBy = new();

    public Select((string Namespace, string Table) table, IEnumerable<string> columns)
    {
        var columnsArray = columns.ToArray();
        Table = table;
        _table = table;
        Columns = columnsArray;
        _databaseFields = columnsArray.ToList();
        Conditions = new List<IQuery>();
    }

    public Select Where(IQuery condition)
    {
        Conditions.Add(condition);
        return this;
    }

    public string ToSql()
    {
        var sql = new StringBuilder(
            $"SELECT {string.Join(", ", _databaseFields.Select(o => $"\"{o}\""))} FROM \"{_table}\"");
        if (Conditions.Any())
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", Conditions.Select(c => c.ToSql())));
        }

        if (OrderBy.Any())
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", OrderBy.Select(o => $"\"{o.Column}\" \"{o.Order}\"")));
        }

        return sql.ToString();
    }

    public IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema)
    {
        foreach (var condition in Conditions)
        {
            foreach (var param in condition.GetParameters(schema))
            {
                yield return param;
            }
        }
    }

    public override string ToString()
    {
        return ToSql();
    }

    public string ToString(IDatabaseSchema schema)
    {
        StringBuilder sb = new();
        sb.AppendLine("Query:");
        sb.AppendLine("------");
        sb.AppendLine(ToSql());
        sb.AppendLine();
        sb.AppendLine("Parameters:");
        sb.AppendLine("-----------");
        foreach (var parameter in GetParameters(schema))
        {
            sb.AppendLine($"{parameter.ParameterName}: {parameter.Value}");
        }

        return sb.ToString();
    }
}
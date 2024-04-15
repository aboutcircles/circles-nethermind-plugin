using System.Data;
using System.Text;

namespace Circles.Index.Data.Query;

public class Select : IQuery
{
    public readonly Tables Table;
    private readonly string _table;
    public readonly IEnumerable<Columns> Columns;
    private readonly List<string> _fields;
    public readonly List<IQuery> Conditions;

    public Select(Tables table, IEnumerable<Columns> columns)
    {
        var columnsArray = columns.ToArray();
        Table = table;
        _table = table.GetIdentifier();
        Columns = columnsArray;
        _fields = columnsArray.Select(c => c.GetIdentifier()).ToList();
        Conditions = new List<IQuery>();
    }

    public Select Where(IQuery condition)
    {
        Conditions.Add(condition);
        return this;
    }

    public string ToSql()
    {
        var sql = new StringBuilder($"SELECT {string.Join(", ", _fields)} FROM {_table}");
        if (Conditions.Any())
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", Conditions.Select(c => c.ToSql())));
        }

        return sql.ToString();
    }

    public IEnumerable<IDataParameter> GetParameters()
    {
        foreach (var condition in Conditions)
        {
            foreach (var param in condition.GetParameters())
            {
                yield return param;
            }
        }
    }

    public IQuery And(IQuery other) => Query.And(this, other);
    public IQuery Or(IQuery other) => Query.Or(this, other);

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine("Query:");
        sb.AppendLine("------");
        sb.AppendLine(ToSql());
        sb.AppendLine();
        sb.AppendLine("Parameters:");
        sb.AppendLine("-----------");
        foreach (var parameter in GetParameters())
        {
            sb.AppendLine($"{parameter.ParameterName}: {parameter.Value}");
        }

        return sb.ToString();
    }
}
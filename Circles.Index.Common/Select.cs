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
    public readonly bool Distinct;

    public Select((string Namespace, string Table) table, IEnumerable<string> columns, bool distinct)
    {
        var columnsArray = columns.ToArray();
        Table = table;
        Distinct = distinct;
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

    public string ToSql(IDatabaseSchema schema)
    {
        CheckColumnsInSelect(schema);

        var sql = new StringBuilder(
            $"SELECT{(Distinct ? " DISTINCT" : "")} {string.Join(", ", _databaseFields.Select(o => $"\"{o}\""))} FROM \"{_table.Namespace}_{_table.Table}\"");
        if (Conditions.Any())
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", Conditions.Select(c => c.ToSql(schema))));
        }

        if (OrderBy.Any())
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(", ", OrderBy.Select(o => $"\"{o.Column}\" \"{o.Order}\"")));
        }

        return sql.ToString();
    }

    private void CheckColumnsInSelect(IDatabaseSchema databaseSchema)
    {
        foreach (var column in _databaseFields)
        {
            if (databaseSchema.Tables[_table].Columns.All(c => c.Column != column))
            {
                throw new InvalidOperationException(
                    $"Column '{column}' does not exist in table '{_table.Namespace}_{_table.Table}'");
            }
        }

        foreach (var condition in Conditions)
        {
            if (condition is not QueryPredicate queryPredicate)
                continue;

            if (databaseSchema.Tables[queryPredicate.Table].Columns.All(c => c.Column != queryPredicate.Column))
            {
                throw new InvalidOperationException(
                    $"Column '{queryPredicate.Column}' does not exist in table '{queryPredicate.Table.Namespace}_{queryPredicate.Table.Table}'");
            }
        }

        foreach (var orderBy in OrderBy)
        {
            if (databaseSchema.Tables[_table].Columns.All(c => c.Column != orderBy.Column))
            {
                throw new InvalidOperationException(
                    $"Column '{orderBy.Column}' does not exist in table '{_table.Namespace}_{_table.Table}'");
            }
        }
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

    public string ToString(IDatabaseSchema schema)
    {
        StringBuilder sb = new();
        sb.AppendLine("Query:");
        sb.AppendLine("------");
        sb.AppendLine(ToSql(schema));
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
using System.Data;

namespace Circles.Index.Common;

public class LogicalAnd : IQuery
{
    public readonly IQuery[] Elements;

    internal LogicalAnd(IQuery[] elements)
    {
        Elements = elements;
    }

    public string ToSql() => $"({string.Join(" AND ", Elements.Select(e => e.ToSql()))})";

    public IEnumerable<IDataParameter> GetParameters(IDatabaseSchema schema)
    {
        foreach (var element in Elements)
        {
            foreach (var param in element.GetParameters(schema))
            {
                yield return param;
            }
        }
    }
}
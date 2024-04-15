using System.Data;

namespace Circles.Index.Data.Query;

public class LogicalAnd : IQuery
{
    public readonly IQuery[] Elements;

    internal LogicalAnd(IQuery[] elements)
    {
        Elements = elements;
    }

    public string ToSql() => $"({string.Join(" AND ", Elements.Select(e => e.ToSql()))})";

    public IEnumerable<IDataParameter> GetParameters()
    {
        foreach (var element in Elements)
        {
            foreach (var param in element.GetParameters())
            {
                yield return param;
            }
        }
    }
}
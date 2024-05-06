using System.Data.Common;

namespace Circles.Index.Common;

public class GreaterThanOrEqual : QueryPredicate
{
    internal GreaterThanOrEqual(DbProviderFactory provider, Tables table, Columns column, object value)
        : base(provider, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} >= {ParameterName}";
}
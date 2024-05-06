using System.Data.Common;

namespace Circles.Index.Common;

public class LessThan : QueryPredicate
{
    internal LessThan(DbProviderFactory provider, Tables table, Columns column, object value)
        : base(provider, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} < {ParameterName}";
}
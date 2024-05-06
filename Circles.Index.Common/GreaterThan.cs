using System.Data.Common;

namespace Circles.Index.Common;

public class GreaterThan : QueryPredicate
{
    internal GreaterThan(DbProviderFactory provider, Tables table, Columns column, object value)
        : base(provider, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} > {ParameterName}";
}
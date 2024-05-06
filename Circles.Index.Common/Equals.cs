using System.Data.Common;

namespace Circles.Index.Common;

public class Equals : QueryPredicate
{
    internal Equals(DbProviderFactory provider, Tables table, Columns column, object value)
        : base(provider, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} = {ParameterName}";
}
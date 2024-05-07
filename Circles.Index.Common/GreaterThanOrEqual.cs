namespace Circles.Index.Common;

public class GreaterThanOrEqual : QueryPredicate
{
    internal GreaterThanOrEqual(IDatabase database, string table, string column, object value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} >= {ParameterName}";
}
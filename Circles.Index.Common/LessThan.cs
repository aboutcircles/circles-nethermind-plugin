namespace Circles.Index.Common;

public class LessThan : QueryPredicate
{
    internal LessThan(IDatabase database, string table, string column, object value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} < {ParameterName}";
}
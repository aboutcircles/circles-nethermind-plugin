namespace Circles.Index.Common;

public class LessThan : QueryPredicate
{
    internal LessThan(IDatabase database, (string Namespace, string Table) table, string column, object value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql() => $"\"{Field}\" < {ParameterName}";
}
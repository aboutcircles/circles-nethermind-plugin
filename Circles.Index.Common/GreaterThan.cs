namespace Circles.Index.Common;

public class GreaterThan : QueryPredicate
{
    internal GreaterThan(IDatabase database, (string Namespace, string Table) table, string column, object value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql() => $"\"{Field}\" > {ParameterName}";
}
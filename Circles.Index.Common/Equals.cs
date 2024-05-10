namespace Circles.Index.Common;

public class Equals : QueryPredicate
{
    internal Equals(IDatabase database, (string Namespace, string Table) table, string column, object? value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql(IDatabaseSchema schema) => $"\"{Field}\" = {ParameterName}";
}
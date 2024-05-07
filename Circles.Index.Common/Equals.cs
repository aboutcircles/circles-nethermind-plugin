namespace Circles.Index.Common;

public class Equals : QueryPredicate
{
    internal Equals(IDatabase database, string table, string column, object? value)
        : base(database, table, column, value)
    {
    }

    public override string ToSql() => $"{Field} = {ParameterName}";
}
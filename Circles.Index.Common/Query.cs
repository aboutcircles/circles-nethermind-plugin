namespace Circles.Index.Common;

public static class Query
{
    private static IDatabase? _database;

    public static void Initialize(IDatabase database)
    {
        _database = database;
    }

    private static IDatabase GetDatabase()
    {
        if (_database == null)
        {
            throw new InvalidOperationException("QueryFactory has not been initialized.");
        }

        return _database;
    }

    public static Equals Equals(string table, string column, object? value) =>
        new(GetDatabase(), table, column, value);

    public static GreaterThan GreaterThan(string table, string column, object value) =>
        new(GetDatabase(), table, column, value);

    public static GreaterThanOrEqual GreaterThanOrEqual(string table, string column, object value) =>
        new(GetDatabase(), table, column, value);

    public static LessThan LessThan(string table, string column, object value) =>
        new(GetDatabase(), table, column, value);

    public static LogicalAnd And(params IQuery[] subElements) => new(subElements);
    public static LogicalOr Or(params IQuery[] subElements) => new(subElements);

    public static Select Select(string table, IEnumerable<string> columns) => new(table, columns);
}
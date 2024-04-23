using System.Data.Common;
using System.Numerics;
using Nethermind.Int256;

namespace Circles.Index.Data.Query;

public enum ValueTypes
{
    Address = 0,
    Int = 1,
    BigInt = 2,
    String = 4,
    Boolean = 6
}

public static class Query
{
    private static DbProviderFactory? _provider;

    public static void Initialize(DbProviderFactory provider)
    {
        _provider = provider;
    }

    private static DbProviderFactory GetProvider()
    {
        if (_provider == null)
        {
            throw new InvalidOperationException("QueryFactory has not been initialized.");
        }

        return _provider;
    }

    public static Equals Equals(Tables table, Columns column, object? value) =>
        new(GetProvider(), table, column, value);

    public static GreaterThan GreaterThan(Tables table, Columns column, object value) =>
        new(GetProvider(), table, column, value);

    public static GreaterThanOrEqual GreaterThanOrEqual(Tables table, Columns column, object value) =>
        new(GetProvider(), table, column, value);

    public static LessThan LessThan(Tables table, Columns column, object value) =>
        new(GetProvider(), table, column, value);

    public static LogicalAnd And(params IQuery[] subElements) => new(subElements);
    public static LogicalOr Or(params IQuery[] subElements) => new(subElements);
    public static Select Select(Tables table, IEnumerable<Columns> columns) => new(table, columns);

    public static IEnumerable<object[]> Execute(DbConnection connection, IQuery query, bool closeConnection = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query.ToSql();
        foreach (var param in query.GetParameters())
        {
            command.Parameters.Add(param);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            yield return row;
        }

        if (closeConnection)
        {
            connection.Close();
        }
    }

    public static object Convert(object input, ValueTypes target)
    {
        switch (target)
        {
            case ValueTypes.String:
                return input.ToString() ?? throw new ArgumentNullException(nameof(input));
            case ValueTypes.Int:
                return System.Convert.ToInt64(input?.ToString());
            case ValueTypes.BigInt when input is string i:
                return BigInteger.Parse(i);
            case ValueTypes.BigInt when input is BigInteger:
                return input;
            case ValueTypes.BigInt when input is UInt256:
            case ValueTypes.BigInt when input is ulong:
            case ValueTypes.BigInt when input is uint:
            case ValueTypes.BigInt when input is long:
            case ValueTypes.BigInt when input is int:
                return (BigInteger)input;
            case ValueTypes.BigInt:
                return BigInteger.Parse(input.ToString() ?? throw new ArgumentNullException(nameof(input)));
            case ValueTypes.Address when input is string i:
                return i.ToLowerInvariant();
            case ValueTypes.Address:
                return input.ToString().ToLowerInvariant();
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target,
                    $"Input was {input} of type {input.GetType()}");
        }
    }
}
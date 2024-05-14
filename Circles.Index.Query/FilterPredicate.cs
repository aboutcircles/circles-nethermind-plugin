using System.Data;
using Circles.Index.Common;
using Npgsql;

namespace Circles.Index.Query;

public record FilterPredicate(string Column, FilterType FilterType, object? Value) : ISql, IFilterPredicate
{
    public ParameterizedSql ToSql(IDatabaseUtils database)
    {
        var parameterName = $"@{Column}_{Guid.NewGuid():N}";
        var sqlCondition = FilterType switch
        {
            FilterType.Equals => $"{database.QuoteIdentifier(Column)} = {parameterName}",
            FilterType.NotEquals => $"{database.QuoteIdentifier(Column)} != {parameterName}",
            FilterType.GreaterThan => $"{database.QuoteIdentifier(Column)} > {parameterName}",
            FilterType.GreaterThanOrEquals => $"{database.QuoteIdentifier(Column)} >= {parameterName}",
            FilterType.LessThan => $"{database.QuoteIdentifier(Column)} < {parameterName}",
            FilterType.LessThanOrEquals => $"{database.QuoteIdentifier(Column)} <= {parameterName}",
            FilterType.Like => $"{database.QuoteIdentifier(Column)} LIKE {parameterName}",
            FilterType.NotLike => $"{database.QuoteIdentifier(Column)} NOT LIKE {parameterName}",
            FilterType.In =>
                $"{database.QuoteIdentifier(Column)} IN ({FormatArrayParameter(Value ?? Array.Empty<object>(), parameterName)})",
            FilterType.NotIn =>
                $"{database.QuoteIdentifier(Column)} NOT IN ({FormatArrayParameter(Value ?? Array.Empty<object>(), parameterName)})",
            _ => throw new NotImplementedException()
        };

        var parameters = CreateParameters(parameterName.Substring(1), Value);
        return new ParameterizedSql(sqlCondition, parameters);
    }


    private string FormatArrayParameter(object value, string parameterName)
    {
        if (value is IEnumerable<object> enumerable)
        {
            return string.Join(", ", enumerable.Select((_, index) => $"{parameterName}_{index}"));
        }

        throw new ArgumentException("Value must be an IEnumerable for In/NotIn filter types.");
    }

    private IEnumerable<IDbDataParameter> CreateParameters(string parameterName, object? value)
    {
        if (value is IEnumerable<object> enumerable)
        {
            return enumerable.Select((v, index) => CreateParameter($"{parameterName}_{index}", v)).ToList();
        }

        return new List<IDbDataParameter> { CreateParameter(parameterName, value) };
    }

    private IDbDataParameter CreateParameter(string name, object? value) => new NpgsqlParameter(name, value);
}
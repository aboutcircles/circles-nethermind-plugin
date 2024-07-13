using System.Collections;
using System.Data;
using System.Text.Json;
using Circles.Index.Common;

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
            FilterType.In => (Value as IEnumerable<object>)?.Any() ?? false
                ? $"{database.QuoteIdentifier(Column)} IN ({FormatArrayParameter(Value ?? Array.Empty<object>(), parameterName)})"
                : "1=0 /* empty 'in' filter */",
            FilterType.NotIn => (Value as IEnumerable<object>)?.Any() ?? false
                ? $"{database.QuoteIdentifier(Column)} NOT IN ({FormatArrayParameter(Value ?? Array.Empty<object>(), parameterName)})"
                : "1=0 /* empty 'not in' filter */",
            _ => throw new NotImplementedException()
        };

        var parameters = CreateParameters(database, parameterName.Substring(1), Value);
        return new ParameterizedSql(sqlCondition, parameters);
    }


    private string FormatArrayParameter(object value, string parameterName)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", jsonElement.EnumerateArray().Select((_, index) => $"{parameterName}_{index}"));
        }

        if (value is IEnumerable e)
        {
            return string.Join(", ", e.Cast<object>().Select((_, index) => $"{parameterName}_{index}"));
        }

        throw new ArgumentException("Value must be an IEnumerable for In/NotIn filter types. Is: " +
                                    value?.GetType().Name);
    }

    private IEnumerable<IDbDataParameter> CreateParameters(IDatabaseUtils database, string parameterName, object? value)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement.EnumerateArray()
                .Select((v, index) => database.CreateParameter($"{parameterName}_{index}", v.ToString()))
                .ToList();
        }

        if (value is IEnumerable e && value is not string)
        {
            return e.Cast<object>()
                .Select((v, index) => database.CreateParameter($"{parameterName}_{index}", v.ToString()))
                .ToList();
        }

        return new List<IDbDataParameter> { database.CreateParameter(parameterName, value) };
    }
}
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
            FilterType.Equals => $"{database.QuoteIdentifier(Column)} = {parameterName}_0",
            FilterType.NotEquals => $"{database.QuoteIdentifier(Column)} != {parameterName}_0",
            FilterType.GreaterThan => $"{database.QuoteIdentifier(Column)} > {parameterName}_0",
            FilterType.GreaterThanOrEquals => $"{database.QuoteIdentifier(Column)} >= {parameterName}_0",
            FilterType.LessThan => $"{database.QuoteIdentifier(Column)} < {parameterName}_0",
            FilterType.LessThanOrEquals => $"{database.QuoteIdentifier(Column)} <= {parameterName}_0",
            FilterType.Like => $"{database.QuoteIdentifier(Column)} LIKE {parameterName}_0",
            FilterType.ILike => $"{database.QuoteIdentifier(Column)} ILIKE {parameterName}_0",
            FilterType.NotLike => $"{database.QuoteIdentifier(Column)} NOT LIKE {parameterName}_0",
            FilterType.In => ConvertToEnumerable(Value)?.Any() ?? false
                ? $"{database.QuoteIdentifier(Column)} IN ({FormatArrayParameter(Value, parameterName)})"
                : "1=0 /* empty 'in' filter */",
            FilterType.NotIn => ConvertToEnumerable(Value)?.Any() ?? false
                ? $"{database.QuoteIdentifier(Column)} NOT IN ({FormatArrayParameter(Value, parameterName)})"
                : "1=0 /* empty 'not in' filter */",
            FilterType.IsNotNull => $"{database.QuoteIdentifier(Column)} IS NOT NULL",
            FilterType.IsNull => $"{database.QuoteIdentifier(Column)} IS NULL",
            _ => throw new NotImplementedException()
        };

        var parameters = CreateParameters(database, parameterName.Substring(1), Value);
        return new ParameterizedSql(sqlCondition, parameters);
    }

    private string FormatArrayParameter(object value, string parameterName)
    {
        var values = ConvertToEnumerable(value);
        return values != null
            ? string.Join(", ", values.Select((_, index) => $"{parameterName}_{index}"))
            : throw new ArgumentException("Value must be an IEnumerable for In/NotIn filter types.");
    }

    private IEnumerable<object> ConvertToEnumerable(object? value)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement.EnumerateArray().Select(v =>
            {
                if (v.ValueKind == JsonValueKind.Number)
                {
                    return v.GetDouble();
                }

                return (object)v.ToString();
            });
        }

        if (value is IEnumerable e && value is not string)
        {
            return e.Cast<object>();
        }

        if (FilterType == FilterType.In || FilterType == FilterType.NotIn)
        {
            throw new ArgumentException("Value must be an IEnumerable for In/NotIn filter types.");
        }

        // For non-In/NotIn filters, return a single-element list
        return new List<object> { value };
    }

    private IEnumerable<IDbDataParameter> CreateParameters(IDatabaseUtils database, string parameterName, object? value)
    {
        var values = ConvertToEnumerable(value).ToArray();

        return values.Select((v, index) => database.CreateParameter($"{parameterName}_{index}", v)).ToList();
    }
}
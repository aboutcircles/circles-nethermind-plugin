using System.Data;
using Circles.Index.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Circles.Index.Query;

public class FilterPredicateDtoConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return typeof(IFilterPredicateDto).IsAssignableFrom(objectType);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer)
    {
        JObject jo = JObject.Load(reader);
        string? type = (string?)jo["Type"];
        IFilterPredicateDto result = type switch
        {
            "FilterPredicate" => new FilterPredicateDto(),
            "Conjunction" => new ConjunctionDto(),
            _ => throw new NotSupportedException($"Unknown filter predicate type: {type}")
        };

        serializer.Populate(jo.CreateReader(), result);
        return result;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}

public interface IFilterPredicateDto
{
    string Type { get; }
}

public class FilterPredicateDto : IFilterPredicateDto
{
    public string Type => "FilterPredicate";
    public string? Column { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public FilterType FilterType { get; set; }

    public object? Value { get; set; }
}

public class OrderByDto
{
    public string? Column { get; set; }
    public string? SortOrder { get; set; }
}

public class ConjunctionDto : IFilterPredicateDto
{
    public string Type => "Conjunction";

    [JsonConverter(typeof(StringEnumConverter))]
    public ConjunctionType ConjunctionType { get; set; }

    public IFilterPredicateDto[]? Predicates { get; set; }
}

public class SelectDto
{
    public string? Namespace { get; set; }
    public string? Table { get; set; }
    public IEnumerable<string>? Columns { get; set; }
    public IEnumerable<IFilterPredicateDto>? Filter { get; set; }
    public IEnumerable<OrderByDto>? Order { get; set; }
    public bool Distinct { get; set; }
}

public enum FilterType
{
    [JsonConverter(typeof(StringEnumConverter))]
    Equals,

    [JsonConverter(typeof(StringEnumConverter))]
    NotEquals,

    [JsonConverter(typeof(StringEnumConverter))]
    GreaterThan,

    [JsonConverter(typeof(StringEnumConverter))]
    GreaterThanOrEquals,

    [JsonConverter(typeof(StringEnumConverter))]
    LessThan,

    [JsonConverter(typeof(StringEnumConverter))]
    LessThanOrEquals,

    [JsonConverter(typeof(StringEnumConverter))]
    Like,

    [JsonConverter(typeof(StringEnumConverter))]
    NotLike,

    [JsonConverter(typeof(StringEnumConverter))]
    In,

    [JsonConverter(typeof(StringEnumConverter))]
    NotIn
}

public enum ConjunctionType
{
    [JsonConverter(typeof(StringEnumConverter))]
    And,

    [JsonConverter(typeof(StringEnumConverter))]
    Or
}

public interface ISql
{
    GeneratedSql ToSql(IDatabaseUtils database);
}

public record GeneratedSql(string Sql, IEnumerable<IDbDataParameter> Parameters);

public interface IFilterPredicate;

public record FilterPredicate(string Column, FilterType FilterType, object? Value) : ISql, IFilterPredicate
{
    public GeneratedSql ToSql(IDatabaseUtils database)
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

        var parameters = CreateParameters(parameterName, Value);
        return new GeneratedSql(sqlCondition, parameters);
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

public record OrderBy(string Column, string SortOrder) : ISql
{
    public GeneratedSql ToSql(IDatabaseUtils database)
    {
        var sql = $"{QuoteIdentifier(Column)} {SortOrder.ToUpper()}";
        return new GeneratedSql(sql, Enumerable.Empty<IDbDataParameter>());
    }

    private string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
}

public record Conjunction(ConjunctionType ConjunctionType, IFilterPredicate[] Predicates) : ISql, IFilterPredicate
{
    public GeneratedSql ToSql(IDatabaseUtils database)
    {
        var conjunction = ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        var predicateSqls = Predicates.OfType<ISql>().Select(p => p.ToSql(database)).ToList();

        var sql = $"({string.Join(conjunction, predicateSqls.Select(p => p.Sql))})";
        var parameters = predicateSqls.SelectMany(p => p.Parameters).ToList();

        return new GeneratedSql(sql, parameters);
    }
}

public record Select(
    string Namespace,
    string Table,
    IEnumerable<string> Columns,
    IEnumerable<IFilterPredicate> Filter,
    IEnumerable<OrderBy> Order,
    bool Distinct = false) : ISql
{
    public GeneratedSql ToSql(IDatabaseUtils database)
    {
        if (!database.Schema.Tables.TryGetValue((Namespace, Table), out var tableSchema))
        {
            throw new InvalidOperationException($"Table {Namespace}_{Table} not found in schema.");
        }

        var tableSchemaColumns = tableSchema.Columns.ToDictionary(o => o.Column, o => o);
        if (Columns.Any() && Columns.Any(c => !tableSchemaColumns.ContainsKey(c)))
        {
            throw new InvalidOperationException($"Select column not found in schema.");
        }

        if (Filter.OfType<FilterPredicate>().Any(f => !tableSchemaColumns.ContainsKey(f.Column)))
        {
            throw new InvalidOperationException($"Filter column not found in schema.");
        }

        if (Order.Any(o => !tableSchemaColumns.ContainsKey(o.Column)))
        {
            throw new InvalidOperationException($"Order column not found in schema.");
        }

        var columns = Columns.Any() ? string.Join(", ", Columns.Select(QuoteIdentifier)) : "*";
        var filterSql = Filter.Any()
            ? string.Join(" AND ", Filter.OfType<ISql>().Select(f => f.ToSql(database).Sql))
            : string.Empty;
        var orderBySql = Order.Any()
            ? $" ORDER BY {string.Join(", ", Order.Select(o => o.ToSql(database).Sql))}"
            : string.Empty;
        var distinctSql = Distinct ? "DISTINCT " : string.Empty;

        var sql = $"SELECT {distinctSql}{columns} FROM {QuoteIdentifier($"{Namespace}_{Table}")}";
        if (!string.IsNullOrEmpty(filterSql))
        {
            sql += $" WHERE {filterSql}";
        }

        if (!string.IsNullOrEmpty(orderBySql))
        {
            sql += orderBySql;
        }

        var parameters = Filter.OfType<ISql>().SelectMany(f => f.ToSql(database).Parameters)
            .Concat(Order.OfType<ISql>().SelectMany(o => o.ToSql(database).Parameters))
            .ToList();

        return new GeneratedSql(sql, parameters);
    }

    private string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
}

public static class ConversionExtensions
{
    private static FilterPredicateDto ToDto(this FilterPredicate model) => new()
    {
        Column = model.Column,
        FilterType = model.FilterType,
        Value = model.Value
    };

    private static OrderByDto ToDto(this OrderBy model) => new()
    {
        Column = model.Column,
        SortOrder = model.SortOrder
    };

    private static ConjunctionDto ToDto(this Conjunction model) => new()
    {
        ConjunctionType = model.ConjunctionType,
        Predicates = model.Predicates.Select(p => p switch
        {
            FilterPredicate fp => (IFilterPredicateDto)fp.ToDto(),
            Conjunction conj => (IFilterPredicateDto)conj.ToDto(),
            _ => throw new NotImplementedException()
        }).ToArray()
    };

    public static SelectDto ToDto(this Select model) => new()
    {
        Namespace = model.Namespace,
        Table = model.Table,
        Columns = model.Columns,
        Filter = model.Filter.Select(f => f switch
        {
            FilterPredicate fp => (IFilterPredicateDto)fp.ToDto(),
            Conjunction conj => (IFilterPredicateDto)conj.ToDto(),
            _ => throw new NotImplementedException()
        }).ToArray(),
        Order = model.Order.Select(o => o.ToDto()).ToArray(),
        Distinct = model.Distinct
    };

    private static FilterPredicate ToModel(this FilterPredicateDto dto) =>
        new((dto.Column ?? throw new InvalidOperationException("Column is null")), dto.FilterType, dto.Value);

    private static OrderBy ToModel(this OrderByDto dto) => new(
        dto.Column ?? throw new InvalidOperationException("Column is null"),
        dto.SortOrder ?? throw new InvalidOperationException("SortOrder is null"));

    private static Conjunction ToModel(this ConjunctionDto dto) => new(dto.ConjunctionType, dto.Predicates?.Select(
        p => p switch
        {
            FilterPredicateDto fpDto => fpDto.ToModel() as IFilterPredicate,
            ConjunctionDto conjDto => conjDto.ToModel() as IFilterPredicate,
            _ => throw new NotImplementedException()
        }).ToArray() ?? []);

    public static Select ToModel(this SelectDto dto) => new(
        dto.Namespace ?? throw new InvalidOperationException("Namespace is null"),
        dto.Table ?? throw new InvalidOperationException("Table is null"),
        dto.Columns ?? [],
        dto.Filter?.Select(f => f switch
        {
            FilterPredicateDto fpDto => fpDto.ToModel() as IFilterPredicate,
            ConjunctionDto conjDto => conjDto.ToModel() as IFilterPredicate,
            _ => throw new NotImplementedException()
        }) ?? new List<IFilterPredicate>(),
        dto.Order?.Select(o => o.ToModel()) ?? new List<OrderBy>(),
        dto.Distinct
    );
}
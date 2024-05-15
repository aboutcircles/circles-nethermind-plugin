using Circles.Index.Query.Dto;

namespace Circles.Index.Query;

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
        new(dto.Column ?? throw new InvalidOperationException("Column is null"), dto.FilterType, dto.Value);

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
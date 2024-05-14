using System.Text.Json.Serialization;

namespace Circles.Index.Query.Dto;

public class ConjunctionDto : IFilterPredicateDto
{
    public string Type => "Conjunction";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConjunctionType ConjunctionType { get; set; }

    [JsonConverter(typeof(FilterPredicateArrayConverter))]
    public IFilterPredicateDto[]? Predicates { get; set; }
}
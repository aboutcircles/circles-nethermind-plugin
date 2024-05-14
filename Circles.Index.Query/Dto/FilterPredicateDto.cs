using System.Text.Json.Serialization;

namespace Circles.Index.Query.Dto;

public class FilterPredicateDto : IFilterPredicateDto
{
    public string Type => "FilterPredicate";
    public string? Column { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FilterType FilterType { get; set; }

    [JsonConverter(typeof(ObjectToInferredTypeConverter))]
    public object? Value { get; set; }
}
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Query.Dto;

namespace Circles.Rpc.Host;

/// <summary>
/// Static JsonSerializerOptions singletons to avoid per-request allocation.
/// JsonSerializerOptions is thread-safe after first use (auto-frozen).
/// </summary>
public static class SharedJsonOptions
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions FilterPredicate = CreateFilterPredicateOptions();

    private static JsonSerializerOptions CreateFilterPredicateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new FilterPredicateArrayConverter());
        options.Converters.Add(new FilterPredicateDtoConverter());
        return options;
    }
}

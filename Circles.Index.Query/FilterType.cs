using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Circles.Index.Query;

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
using System.Text.Json.Serialization;

namespace Circles.Index.Query;

public enum FilterType
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    Equals,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    NotEquals,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    GreaterThan,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    GreaterThanOrEquals,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    LessThan,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    LessThanOrEquals,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    Like,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    ILike,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    NotLike,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    In,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    NotIn,
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    IsNotNull,
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    IsNull
}
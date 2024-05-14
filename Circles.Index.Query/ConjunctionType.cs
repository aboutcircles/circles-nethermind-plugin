using System.Text.Json.Serialization;

namespace Circles.Index.Query;

public enum ConjunctionType
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    And,

    [JsonConverter(typeof(JsonStringEnumConverter))]
    Or
}
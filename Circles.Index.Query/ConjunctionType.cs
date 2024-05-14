using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Circles.Index.Query;

public enum ConjunctionType
{
    [JsonConverter(typeof(StringEnumConverter))]
    And,

    [JsonConverter(typeof(StringEnumConverter))]
    Or
}
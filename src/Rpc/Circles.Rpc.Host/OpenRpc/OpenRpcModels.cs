using System.Text.Json.Serialization;

namespace Circles.Rpc.Host.OpenRpc;

/// <summary>
/// POCO models for the OpenRPC 1.3.2 document structure.
/// See: https://spec.open-rpc.org/
/// </summary>
public class OpenRpcDocument
{
    [JsonPropertyName("openrpc")]
    public string OpenRpc { get; set; } = "1.3.2";

    [JsonPropertyName("info")]
    public OpenRpcInfo Info { get; set; } = new();

    [JsonPropertyName("methods")]
    public List<OpenRpcMethod> Methods { get; set; } = [];

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenRpcComponents? Components { get; set; }
}

public class OpenRpcInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public class OpenRpcMethod
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("params")]
    public List<OpenRpcParam> Params { get; set; } = [];

    [JsonPropertyName("result")]
    public OpenRpcResult Result { get; set; } = new();

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenRpcTag>? Tags { get; set; }
}

public class OpenRpcParam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("schema")]
    public JsonSchemaObject Schema { get; set; } = new();
}

public class OpenRpcResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "result";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("schema")]
    public JsonSchemaObject Schema { get; set; } = new();
}

public class OpenRpcTag
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class OpenRpcComponents
{
    [JsonPropertyName("schemas")]
    public Dictionary<string, JsonSchemaObject> Schemas { get; set; } = new();
}

/// <summary>
/// Simplified JSON Schema object for OpenRPC parameter/result schemas.
/// Supports type, $ref, array items, object properties, and pattern.
/// </summary>
public class JsonSchemaObject
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("$ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; set; }

    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchemaObject? Items { get; set; }

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonSchemaObject>? Properties { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }

    [JsonPropertyName("pattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; set; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; set; }

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; set; }

    [JsonPropertyName("nullable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Nullable { get; set; }

    [JsonPropertyName("additionalProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchemaObject? AdditionalProperties { get; set; }

    [JsonPropertyName("oneOf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonSchemaObject>? OneOf { get; set; }
}

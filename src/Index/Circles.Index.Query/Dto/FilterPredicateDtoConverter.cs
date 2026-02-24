using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Index.Query.Dto;

public class FilterPredicateDtoConverter : JsonConverter<IFilterPredicateDto>
{
    private const string TypePropertyName = "type";

    public override IFilterPredicateDto? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        string? type = GetDiscriminator(root);

        if (type is null)
        {
            throw new JsonException("Missing 'type' discriminator on filter predicate.");
        }

        IFilterPredicateDto? result = type switch
        {
            "FilterPredicate" => JsonSerializer.Deserialize<FilterPredicateDto>(root.GetRawText(), options),
            "Conjunction" => JsonSerializer.Deserialize<ConjunctionDto>(root.GetRawText(), options),
            _ => throw new NotSupportedException($"Unknown filter predicate type: {type}")
        };

        return result;
    }

    public override void Write(Utf8JsonWriter writer, IFilterPredicateDto value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static string? GetDiscriminator(JsonElement root)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            bool isTypeProperty = string.Equals(property.Name, TypePropertyName, StringComparison.OrdinalIgnoreCase);

            if (!isTypeProperty)
            {
                continue;
            }

            return property.Value.GetString();
        }

        return null;
    }
}

public class FilterPredicateArrayConverter : JsonConverter<IFilterPredicateDto[]>
{
    private const string TypePropertyName = "type";

    public override IFilterPredicateDto[] Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        var elements = root.EnumerateArray();
        var predicates = new IFilterPredicateDto[root.GetArrayLength()];
        int i = 0;

        foreach (JsonElement element in elements)
        {
            string? type = GetDiscriminator(element);

            if (type is null)
            {
                throw new JsonException("Missing 'type' discriminator on filter predicate array element.");
            }

            IFilterPredicateDto? result = type switch
            {
                "FilterPredicate" => JsonSerializer.Deserialize<FilterPredicateDto>(element.GetRawText(), options),
                "Conjunction" => JsonSerializer.Deserialize<ConjunctionDto>(element.GetRawText(), options),
                _ => throw new NotSupportedException($"Unknown filter predicate type: {type}")
            };

            predicates[i++] = result ?? throw new JsonException("Failed to deserialize filter predicate.");
        }

        return predicates;
    }

    public override void Write(Utf8JsonWriter writer, IFilterPredicateDto[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (IFilterPredicateDto predicate in value)
        {
            JsonSerializer.Serialize(writer, predicate, predicate.GetType(), options);
        }

        writer.WriteEndArray();
    }

    private static string? GetDiscriminator(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool isTypeProperty = string.Equals(property.Name, TypePropertyName, StringComparison.OrdinalIgnoreCase);

            if (!isTypeProperty)
            {
                continue;
            }

            return property.Value.GetString();
        }

        return null;
    }
}

public class ObjectToInferredTypeConverter : JsonConverter<object>
{
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? throw new JsonException("Unexpected null string value.");
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out int intValue))
                {
                    return intValue;
                }

                if (reader.TryGetInt64(out long longValue))
                {
                    return longValue;
                }

                return reader.GetDouble();
            case JsonTokenType.True:
            case JsonTokenType.False:
                return reader.GetBoolean();
            default:
                return JsonDocument.ParseValue(ref reader).RootElement.Clone();
        }
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

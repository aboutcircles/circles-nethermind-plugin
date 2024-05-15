using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Index.Query.Dto;

public class FilterPredicateDtoConverter : JsonConverter<IFilterPredicateDto>
{
    public override IFilterPredicateDto? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        string? type = root.GetProperty("Type").GetString();

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
}

public class FilterPredicateArrayConverter : JsonConverter<IFilterPredicateDto[]>
{
    public override IFilterPredicateDto[]? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        var elements = root.EnumerateArray();
        var predicates = new IFilterPredicateDto[root.GetArrayLength()];
        int i = 0;

        foreach (var element in elements)
        {
            string? type = element.GetProperty("Type").GetString();

            IFilterPredicateDto? result = type switch
            {
                "FilterPredicate" => JsonSerializer.Deserialize<FilterPredicateDto>(element.GetRawText(), options),
                "Conjunction" => JsonSerializer.Deserialize<ConjunctionDto>(element.GetRawText(), options),
                _ => throw new NotSupportedException($"Unknown filter predicate type: {type}")
            };

            predicates[i++] = result;
        }

        return predicates;
    }

    public override void Write(Utf8JsonWriter writer, IFilterPredicateDto[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var predicate in value)
        {
            JsonSerializer.Serialize(writer, predicate, predicate.GetType(), options);
        }

        writer.WriteEndArray();
    }
}

public class ObjectToInferredTypeConverter : JsonConverter<object>
{
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
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
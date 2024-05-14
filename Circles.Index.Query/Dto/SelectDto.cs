using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Index.Query.Dto;

[JsonConverter(typeof(SelectDtoConverter))]
public class SelectDto
{
    public string? Namespace { get; set; }
    public string? Table { get; set; }
    public IEnumerable<string>? Columns { get; set; }
    public IEnumerable<IFilterPredicateDto>? Filter { get; set; }
    public IEnumerable<OrderByDto>? Order { get; set; }
    public bool Distinct { get; set; }
}

public class SelectDtoConverter : JsonConverter<SelectDto>
{
    public override SelectDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = new SelectDto();

        var newOptions = new JsonSerializerOptions(options)
        {
            PropertyNameCaseInsensitive = true
        };
        newOptions.Converters.Add(new FilterPredicateArrayConverter());
        newOptions.Converters.Add(new FilterPredicateDtoConverter());

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "namespace":
                    dto.Namespace = property.Value.GetString();
                    break;
                case "table":
                    dto.Table = property.Value.GetString();
                    break;
                case "columns":
                    dto.Columns =
                        JsonSerializer.Deserialize<IEnumerable<string>>(property.Value.GetRawText(), newOptions);
                    break;
                case "filter":
                    dto.Filter =
                        JsonSerializer.Deserialize<IEnumerable<IFilterPredicateDto>>(property.Value.GetRawText(),
                            newOptions);
                    break;
                case "order":
                    dto.Order = JsonSerializer.Deserialize<IEnumerable<OrderByDto>>(property.Value.GetRawText(),
                        newOptions);
                    break;
                case "distinct":
                    dto.Distinct = property.Value.GetBoolean();
                    break;
            }
        }

        return dto;
    }

    public override void Write(Utf8JsonWriter writer, SelectDto value, JsonSerializerOptions options)
    {
        var newOptions = new JsonSerializerOptions(options);
        newOptions.Converters.Add(new FilterPredicateArrayConverter());
        newOptions.Converters.Add(new FilterPredicateDtoConverter());

        writer.WriteStartObject();

        if (value.Namespace != null)
        {
            writer.WriteString("namespace", value.Namespace);
        }

        if (value.Table != null)
        {
            writer.WriteString("table", value.Table);
        }

        if (value.Columns != null)
        {
            writer.WritePropertyName("columns");
            JsonSerializer.Serialize(writer, value.Columns, newOptions);
        }

        if (value.Filter != null)
        {
            writer.WritePropertyName("filter");
            JsonSerializer.Serialize(writer, value.Filter, newOptions);
        }

        if (value.Order != null)
        {
            writer.WritePropertyName("order");
            JsonSerializer.Serialize(writer, value.Order, newOptions);
        }

        writer.WriteBoolean("distinct", value.Distinct);

        writer.WriteEndObject();
    }
}